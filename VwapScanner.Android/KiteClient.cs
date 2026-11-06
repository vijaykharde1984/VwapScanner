using System.Globalization;
using System.Reflection;
using KiteConnect;

namespace VwapScanner;

public class ZerodhaException(string message) : Exception(message);

/// <summary>Zerodha refused the access token (expired or invalid).</summary>
public sealed class ZerodhaAuthException(string message) : ZerodhaException(message);

/// <summary>
/// Read-only Zerodha access through the official Kite Connect .NET client (Tech.Zerodha.KiteConnect, the same package GptTrading uses):
/// the NSE instrument master and 1-minute historical candles. Paced to stay under 3 requests a second; the SDK's blocking calls run on the thread pool.
/// </summary>
public sealed class KiteClient : IDisposable
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private readonly Kite kite;
    private DateTime next = DateTime.MinValue;
    private readonly object paceLock = new();

    public KiteClient(string apiKey, string accessToken)
    {
        kite = NewKite(apiKey); // the NSE instrument dump is several MB
        kite.SetAccessToken(accessToken);
    }

    private const int TimeoutMs = 60000;

    /// <summary>
    /// The Kite SDK sends every request with the synchronous HttpClient.Send. Android's default (native) HTTP handler does not support that and throws
    /// PlatformNotSupportedException ("Operation is not supported on this platform"), so the SDK's private HttpClient is replaced with one that
    /// uses the managed SocketsHttpHandler, which supports synchronous sends. Everything else about the SDK is unchanged.
    /// </summary>
    private static Kite NewKite(string apiKey)
    {
        var kite = new Kite(apiKey, Timeout: TimeoutMs);
        var field = typeof(Kite).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(kite) is not HttpClient original)
            throw new ZerodhaException("Unsupported Kite client version: its HTTP client could not be replaced. Pin Tech.Zerodha.KiteConnect 5.0.0.");
        field.SetValue(kite, new HttpClient(new SocketsHttpHandler()) { BaseAddress = original.BaseAddress, Timeout = TimeSpan.FromMilliseconds(TimeoutMs) });
        original.Dispose();
        return kite;
    }

    public static string LoginUrl(string apiKey) => new Kite(apiKey).GetLoginURL();

    /// <summary>Exchanges the single-use request token for an access token (needs the API secret).</summary>
    public static async Task<string> ExchangeAsync(string apiKey, string apiSecret, string requestToken, CancellationToken ct)
    {
        try
        {
            var user = await Task.Run(() => NewKite(apiKey).GenerateSession(requestToken, apiSecret), ct);
            return string.IsNullOrWhiteSpace(user.AccessToken) ? throw new ZerodhaException("Zerodha returned no access token.") : user.AccessToken;
        }
        catch (TokenException) { throw new ZerodhaException("Zerodha rejected the request token. It is single-use and short-lived: log in again."); }
        catch (InputException) { throw new ZerodhaException("Zerodha rejected the login. Check the API key/secret and that http://127.0.0.1:9079/ is the app's redirect URL."); }
        catch (KiteConnect.PermissionException) { throw new ZerodhaException("Zerodha denied the login for this API app."); }
        catch (HttpRequestException e) { throw new ZerodhaException("Cannot reach Zerodha (api.kite.trade): " + e.Message + " Check the internet/DNS/VPN, then log in again (the request token is single-use)."); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ZerodhaException("Zerodha (api.kite.trade) did not answer within 60 seconds. Check the internet/DNS/VPN/proxy, then open the login URL again (the request token is single-use)."); }
        catch (KiteException e) { throw new ZerodhaException("Zerodha could not complete the token exchange: " + e.Message); }
    }

    private async Task<T> CallAsync<T>(Func<T> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            // Each call reserves its own slot, so the scan and the target tracker (two loops sharing this client) can never fire together.
            TimeSpan wait;
            lock (paceLock)
            {
                var now = DateTime.UtcNow;
                var start = next > now ? next : now;
                next = start.AddMilliseconds(380);
                wait = start - now;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            try { return await Task.Run(call, ct); }
            catch (TokenException e) { throw new ZerodhaAuthException("Zerodha rejected the access token (it expires every day). Run again with --login. " + e.Message); }
            catch (NetworkException e) when (attempt < 3) { await Task.Delay(1000 * (attempt + 1), ct); _ = e; }
            catch (NetworkException e) { throw new ZerodhaException("Zerodha network error: " + e.Message); }
            catch (KiteException e) { throw new ZerodhaException("Zerodha: " + e.Message); }
            catch (HttpRequestException e) { throw new ZerodhaException("Cannot reach Zerodha (api.kite.trade): " + e.Message); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ZerodhaException("Cannot reach Zerodha (api.kite.trade): the request timed out."); }
        }
    }

    /// <summary>Cheap authenticated call used to check that a saved access token still works.</summary>
    public Task ProfileAsync(CancellationToken ct) => CallAsync(() => kite.GetProfile(), ct);

    /// <summary>NSE cash equities (instrument type EQ) by trading symbol.</summary>
    public async Task<Dictionary<string, int>> EquitiesAsync(CancellationToken ct)
    {
        var rows = await CallAsync(() => kite.GetInstruments("NSE"), ct);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in rows)
            if (i.InstrumentType == "EQ" && i.Segment == "NSE" && !string.IsNullOrWhiteSpace(i.TradingSymbol))
                result[i.TradingSymbol] = (int)i.InstrumentToken;
        return result;
    }

    /// <summary>1-minute candles of one instrument between two IST instants, including the minute still forming (the caller drops it).</summary>
    public async Task<List<Candle>> MinuteCandlesAsync(int token, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var f = from.ToOffset(Ist).DateTime;
        var t = to.ToOffset(Ist).DateTime;
        var rows = await CallAsync(() => kite.GetHistoricalData(token.ToString(CultureInfo.InvariantCulture), f, t, "minute"), ct);
        return rows.Select(h => new Candle(ToIst(h.TimeStamp), h.Open, h.High, h.Low, h.Close, Convert.ToInt64(h.Volume, CultureInfo.InvariantCulture))).ToList();
    }

    // The client parses "+0530" timestamps into DateTimeKind.Local, so the instant is right on any machine time zone.
    private static DateTimeOffset ToIst(DateTime stamp) => stamp.Kind == DateTimeKind.Unspecified
        ? new DateTimeOffset(stamp, Ist)
        : new DateTimeOffset(stamp).ToOffset(Ist);

    public void Dispose() { }
}
