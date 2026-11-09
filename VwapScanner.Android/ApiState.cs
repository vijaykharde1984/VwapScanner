using System.Globalization;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Configuration;

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
        get
        {
            var f = Zerodha.TokenFile;
            return Path.IsPathRooted(f) ? f : Path.Combine(contentRoot, f);
        }
    }

    public string LoginUrl()
    {
        var key = Zerodha.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new ZerodhaException(
                "Zerodha:ApiKey is not set in appsettings.json / appsettings.Local.json."
            );
        return KiteClient.LoginUrl(key);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var z = Zerodha;
        if (string.IsNullOrWhiteSpace(z.ApiKey))
            return;
        if (await TokenStore.LoadAsync(TokenPath, z.ApiKey, ct) is { } s)
            Use(s);
    }

    private void Use(Session s)
    {
        api?.Dispose();
        api = new KiteClient(s.ApiKey, s.AccessToken);
        master = null;
        ConnectedUtc = DateTime.UtcNow;
    }

    private void Disconnect()
    {
        api?.Dispose();
        api = null;
        master = null;
        ConnectedUtc = null;
    }

    /// <summary>Finishes the Zerodha login from the redirect (query string with request_token=...).</summary>
    public async Task CompleteLoginAsync(string query, CancellationToken ct)
    {
        var z = Zerodha;
        if (string.IsNullOrWhiteSpace(z.ApiKey) || string.IsNullOrWhiteSpace(z.ApiSecret))
            throw new ZerodhaException(
                "Zerodha:ApiKey and Zerodha:ApiSecret are not set in appsettings.json / appsettings.Local.json."
            );
        var values = HttpUtility.ParseQueryString(query.TrimStart('?'));
        var status = values["status"];
        if (
            !string.IsNullOrEmpty(status)
            && !status.Equals("success", StringComparison.OrdinalIgnoreCase)
        )
            throw new ZerodhaException(
                $"Zerodha login was not completed (status={status}). Open the loginUrl again."
            );
        var requestToken = values["request_token"];
        if (string.IsNullOrWhiteSpace(requestToken))
            throw new ZerodhaException(
                "The redirect has no request_token. Open the loginUrl again."
            );
        var token = await KiteClient.ExchangeAsync(z.ApiKey, z.ApiSecret, requestToken, ct);
        var session = new Session(z.ApiKey, token);
        TokenStore.Save(TokenPath, session);
        await gate.WaitAsync(ct);
        try
        {
            Use(session);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The NSE instrument master (several MB) is downloaded once per day and kept in instruments-nse.json; later runs and restarts read the file.
    /// Instrument tokens do not change during a day, and Zerodha republishes the list every morning.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> LoadMasterAsync(
        KiteClient client,
        CancellationToken ct
    )
    {
        var path = Path.Combine(contentRoot, "instruments-nse.json");
        var today = DateOnly
            .FromDateTime(DateTime.UtcNow.AddHours(5.5))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(path))
            {
                var cached = JsonSerializer.Deserialize<MasterFile>(File.ReadAllText(path));
                if (cached?.Date == today && cached.Items is { Count: > 0 })
                    return new Dictionary<string, int>(
                        cached.Items,
                        StringComparer.OrdinalIgnoreCase
                    );
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        { /* re-download below */
        }
        var fresh = await client.EquitiesAsync(ct);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new MasterFile(today, fresh)));
        }
        catch (IOException)
        { /* the list still works from memory */
        }
        return fresh;
    }

    public sealed record MasterFile(string Date, Dictionary<string, int> Items);

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    /// <summary>Open, high, low and close of the day from the completed candles that ended by <paramref name="asOf"/>; null when there are none.</summary>
    private static DayOhlc? Summarize(List<Candle> have, DateOnly day, DateTimeOffset asOf)
    {
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), Ist);
        var bars = have.Where(c => c.Time >= open && c.Time.AddMinutes(1) <= asOf)
            .OrderBy(c => c.Time)
            .ToList();
        return bars.Count == 0
            ? null
            : new DayOhlc(
                bars[0].Open,
                bars.Max(c => c.High),
                bars.Min(c => c.Low),
                bars[^1].Close,
                bars[^1].Time.AddMinutes(1).ToString("HH:mm", CultureInfo.InvariantCulture)
            );
    }

    /// <summary>The day's open/high/low/close of every signalled stock as of the scan time. It reads the candles the scan just downloaded: no extra Kite calls.</summary>
    private Dictionary<string, DayOhlc> OhlcOfSignals(ScanResult result)
    {
        var map = new Dictionary<string, DayOhlc>(StringComparer.OrdinalIgnoreCase);
        if (master is null || result.Signals.Count == 0)
            return map;
        var day = DateOnly.ParseExact(result.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var asOf = new DateTimeOffset(
            day.ToDateTime(TimeOnly.ParseExact(result.AsOf, "HH:mm", CultureInfo.InvariantCulture)),
            Ist
        );
        foreach (
            var symbol in result
                .Signals.Select(x => x.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
            if (
                master.TryGetValue(symbol, out var token)
                && Summarize(candles.For(token, day), day, asOf) is { } ohlc
            )
                map[symbol] = ohlc;
        return map;
    }

    /// <summary>
    /// The final open/high/low/close of the day for the given stocks, for use after the market has closed (15:30 IST): any candles still missing
    /// from the cache are downloaded, so it also works after the app was restarted.
    /// </summary>
    public async Task<Dictionary<string, DayOhlc>> FinalOhlcAsync(
        IEnumerable<string> symbols,
        DateOnly day,
        CancellationToken ct
    )
    {
        var map = new Dictionary<string, DayOhlc>(StringComparer.OrdinalIgnoreCase);
        var open = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 15)), Ist);
        var close = new DateTimeOffset(day.ToDateTime(new TimeOnly(15, 30)), Ist);
        await gate.WaitAsync(ct);
        try
        {
            if (api is null)
                throw new ZerodhaAuthException("Not connected to Zerodha.");
            master ??= await LoadMasterAsync(api, ct);
            foreach (var symbol in symbols.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!master.TryGetValue(symbol, out var token))
                    continue;
                var have = candles.For(token, day);
                var from = have.Count > 0 ? have[^1].Time.AddMinutes(1) : open;
                if (from < close)
                    foreach (
                        var c in (await api.MinuteCandlesAsync(token, from, close, ct))
                            .Where(c => c.Time >= open && c.Time.AddMinutes(1) <= close)
                            .OrderBy(c => c.Time)
                    )
                        if (have.Count == 0 || c.Time > have[^1].Time)
                            have.Add(c);
                if (Summarize(have, day, close) is { } ohlc)
                    map[symbol] = ohlc;
            }
            return map;
        }
        catch (ZerodhaAuthException)
        {
            Disconnect();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Fresh 1-minute candles of one stock from <paramref name="from"/> until now, including the minute still forming, for the target tracker.
    /// It does not wait for a running scan (it never takes the scan gate for long) and never touches the shared completed-candle cache.
    /// Returns null when the instrument list is not loaded yet (the first scan loads it).
    /// </summary>
    public async Task<List<Candle>?> LiveCandlesAsync(
        string symbol,
        DateTimeOffset from,
        CancellationToken ct
    )
    {
        var client = api ?? throw new ZerodhaAuthException("Not connected to Zerodha.");
        var map = master;
        if (map is null)
            return null;
        if (!map.TryGetValue(symbol, out var token))
            return [];
        try
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Ist);
            return (await client.MinuteCandlesAsync(token, from, now.AddMinutes(1), ct))
                .OrderBy(c => c.Time)
                .ToList();
        }
        catch (ZerodhaAuthException)
        {
            Disconnect();
            throw;
        }
    }

    /// <summary>One run of the whole business logic with the current settings: VWAP scan, then portfolio optimization of the signals.</summary>
    /// <param name="minLookback">Auto-refresh passes its interval so the candles between two scans are still checked.</param>
    /// <param name="capital">The capital still free: cash not tied up in positions that are still held, plus gains already made. Null = the full Portfolio:Capital.
    /// The risk limits are percentages of it, so they shrink with it.</param>
    /// <param name="alreadyBought">Signals that already have a position today: they are not bought a second time, whatever the capital.</param>
    public async Task<RunResult> RunAsync(
        CancellationToken ct,
        Action<string>? progress = null,
        int minLookback = 0,
        decimal? capital = null,
        Func<VwapSignal, bool>? alreadyBought = null
    )
    {
        ScanSettings scan;
        PortfolioOptions limits;
        string[] symbols;
        ScanRequest request;
        try
        {
            scan = config.GetSection("Scan").Get<ScanSettings>() ?? new();
            limits = config.GetSection("Portfolio").Get<PortfolioOptions>() ?? new();
            symbols = await Universe.LoadAsync(scan.Symbols, scan.Universe);
            scan.Lookback = Math.Min(400, Math.Max(scan.Lookback, minLookback));
            if (scan.Lookback is < 1 or > 400)
                throw new ArgumentException("Scan:Lookback must be between 1 and 400.");
            DateOnly? date = string.IsNullOrWhiteSpace(scan.Date)
                ? null
                : DateOnly.ParseExact(scan.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            TimeOnly? at = string.IsNullOrWhiteSpace(scan.At)
                ? null
                : TimeOnly.ParseExact(scan.At, "HH:mm", CultureInfo.InvariantCulture);
            limits.Validate();
            var strategyName = StrategyCatalog.Normalize(scan.StrategyName);
            if (strategyName == StrategyCatalog.VwapPullback)
                scan.Pullback.Validate();
            scan.Indicators.Validate();
            (strategyName == StrategyCatalog.VwapPullback ? scan.PullbackFilters : scan.ScalpFilters).Validate(strategyName);
            request = new(date, at, scan.Lookback, scan.UseBenchmark, scan.Strategy, strategyName, scan.Pullback, scan.Indicators, scan.ScalpFilters, scan.PullbackFilters);
        }
        catch (Exception e)
            when (e is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new ConfigurationException(e.Message);
        }

        await gate.WaitAsync(ct);
        try
        {
            if (api is null)
                throw new ZerodhaAuthException(
                    "Not connected to Zerodha. Open loginUrl in a browser."
                );
            master ??= await LoadMasterAsync(api, ct);
            var result = await new Scanner(api, master, candles).ScanAsync(
                symbols,
                request,
                ct,
                progress
            );
            // The scan itself (and the signals grid) is unchanged; only what may be bought now is limited by the capital that is left.
            IReadOnlyList<VwapSignal> candidates = alreadyBought is null
                ? result.Signals
                : result.Signals.Where(s => !alreadyBought(s)).ToList();
            if (capital is { } free)
            {
                limits.Capital = Math.Max(free, 1m);
                if (free < 1m)
                    candidates = Array.Empty<VwapSignal>(); // nothing left to invest
            }
            var plan = PortfolioOptimizer.Optimize(candidates, limits);
            return new RunResult(
                request.StrategyName,
                scan.Symbols.Count > 0 ? "custom symbols" : scan.Universe,
                result,
                plan,
                OhlcOfSignals(result)
            );
        }
        catch (ZerodhaAuthException)
        {
            Disconnect();
            throw;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Typed result of one run (the web API returns the same data as JSON). <paramref name="Ohlc"/>: day open/high/low/close per signalled stock, as of the scan time.</summary>
public sealed record RunResult(
    string Strategy,
    string Universe,
    ScanResult Scan,
    PortfolioPlan Plan,
    IReadOnlyDictionary<string, DayOhlc> Ohlc
);

public sealed class ConfigurationException(string message) : Exception(message);
