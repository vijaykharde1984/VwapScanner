using Microsoft.Extensions.Configuration;
using System.Globalization;
using System.Text.Json;
using System.Web;

namespace VwapScanner;

/// <summary>Holds the Zerodha session and the instrument master; reads the settings on every request; scans run one at a time (Kite allows about 3 requests a second).</summary>
public sealed class ApiState(IConfiguration config, string contentRoot)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private KiteClient? api;
    private IReadOnlyDictionary<string, int>? master;
    private readonly CandleCache candles = new();

    public bool Connected => api is not null;
    public DateTime? ConnectedUtc { get; private set; }

    private ZerodhaSettings Zerodha => config.GetSection("Zerodha").Get<ZerodhaSettings>() ?? new();

    private string TokenPath
    {
        get { var f = Zerodha.TokenFile; return Path.IsPathRooted(f) ? f : Path.Combine(contentRoot, f); }
    }

    public string LoginUrl()
    {
        var key = Zerodha.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new ZerodhaException("Zerodha:ApiKey is not set in appsettings.json / appsettings.Local.json.");
        return KiteClient.LoginUrl(key);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var z = Zerodha;
        if (string.IsNullOrWhiteSpace(z.ApiKey)) return;
        if (await TokenStore.LoadAsync(TokenPath, z.ApiKey, ct) is { } s) Use(s);
    }

    private void Use(Session s)
    {
        api?.Dispose();
        api = new KiteClient(s.ApiKey, s.AccessToken);
        master = null;
        ConnectedUtc = DateTime.UtcNow;
    }

    private void Disconnect() { api?.Dispose(); api = null; master = null; ConnectedUtc = null; }

    /// <summary>Finishes the Zerodha login from the redirect (query string with request_token=...).</summary>
    public async Task CompleteLoginAsync(string query, CancellationToken ct)
    {
        var z = Zerodha;
        if (string.IsNullOrWhiteSpace(z.ApiKey) || string.IsNullOrWhiteSpace(z.ApiSecret))
            throw new ZerodhaException("Zerodha:ApiKey and Zerodha:ApiSecret are not set in appsettings.json / appsettings.Local.json.");
        var values = HttpUtility.ParseQueryString(query.TrimStart('?'));
        var status = values["status"];
        if (!string.IsNullOrEmpty(status) && !status.Equals("success", StringComparison.OrdinalIgnoreCase))
            throw new ZerodhaException($"Zerodha login was not completed (status={status}). Open the loginUrl again.");
        var requestToken = values["request_token"];
        if (string.IsNullOrWhiteSpace(requestToken)) throw new ZerodhaException("The redirect has no request_token. Open the loginUrl again.");
        var token = await KiteClient.ExchangeAsync(z.ApiKey, z.ApiSecret, requestToken, ct);
        var session = new Session(z.ApiKey, token);
        TokenStore.Save(TokenPath, session);
        await gate.WaitAsync(ct);
        try { Use(session); } finally { gate.Release(); }
    }

    /// <summary>
    /// The NSE instrument master (several MB) is downloaded once per day and kept in instruments-nse.json; later runs and restarts read the file.
    /// Instrument tokens do not change during a day, and Zerodha republishes the list every morning.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> LoadMasterAsync(KiteClient client, CancellationToken ct)
    {
        var path = Path.Combine(contentRoot, "instruments-nse.json");
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(path))
            {
                var cached = JsonSerializer.Deserialize<MasterFile>(File.ReadAllText(path));
                if (cached?.Date == today && cached.Items is { Count: > 0 })
                    return new Dictionary<string, int>(cached.Items, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { /* re-download below */ }
        var fresh = await client.EquitiesAsync(ct);
        try { File.WriteAllText(path, JsonSerializer.Serialize(new MasterFile(today, fresh))); }
        catch (IOException) { /* the list still works from memory */ }
        return fresh;
    }

    public sealed record MasterFile(string Date, Dictionary<string, int> Items);

    /// <summary>One run of the whole business logic with the current settings: VWAP scan, then portfolio optimization of the signals.</summary>
    /// <param name="minLookback">Auto-refresh passes its interval so the candles between two scans are still checked.</param>
    public async Task<RunResult> RunAsync(CancellationToken ct, Action<string>? progress = null, int minLookback = 0)
    {
        ScanSettings scan; PortfolioOptions limits; string[] symbols; ScanRequest request;
        try
        {
            scan = config.GetSection("Scan").Get<ScanSettings>() ?? new();
            limits = config.GetSection("Portfolio").Get<PortfolioOptions>() ?? new();
            symbols = await Universe.LoadAsync(scan.Symbols, scan.Universe);
            scan.Lookback = Math.Min(400, Math.Max(scan.Lookback, minLookback));
            if (scan.Lookback is < 1 or > 400) throw new ArgumentException("Scan:Lookback must be between 1 and 400.");
            DateOnly? date = string.IsNullOrWhiteSpace(scan.Date) ? null : DateOnly.ParseExact(scan.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            TimeOnly? at = string.IsNullOrWhiteSpace(scan.At) ? null : TimeOnly.ParseExact(scan.At, "HH:mm", CultureInfo.InvariantCulture);
            limits.Validate();
            request = new(date, at, scan.Lookback, scan.UseBenchmark, scan.Strategy);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException)
        { throw new ConfigurationException(e.Message); }

        await gate.WaitAsync(ct);
        try
        {
            if (api is null) throw new ZerodhaAuthException("Not connected to Zerodha. Open loginUrl in a browser.");
            master ??= await LoadMasterAsync(api, ct);
            var result = await new Scanner(api, master, candles).ScanAsync(symbols, request, ct, progress);
            var plan = PortfolioOptimizer.Optimize(result.Signals, limits);
            Reporter.Report(config.GetSection("Output").Get<OutputSettings>() ?? new(), result, plan, contentRoot);
            return new RunResult("ScalpVwap", scan.Symbols.Count > 0 ? "custom symbols" : scan.Universe, result, plan);
        }
        catch (ZerodhaAuthException) { Disconnect(); throw; }
        finally { gate.Release(); }
    }
}

/// <summary>Typed result of one run (the web API returns the same data as JSON).</summary>
public sealed record RunResult(string Strategy, string Universe, ScanResult Scan, PortfolioPlan Plan);

public sealed class ConfigurationException(string message) : Exception(message);
