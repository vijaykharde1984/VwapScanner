namespace VwapScanner;

public sealed record ScanRequest(DateOnly? Date, TimeOnly? At, int Lookback, bool UseBenchmark, VwapOptions Options, string StrategyName = StrategyCatalog.ScalpVwap, PullbackOptions? Pullback = null,
    IndicatorOptions? Indicators = null, SignalFilterOptions? ScalpFilters = null, SignalFilterOptions? PullbackFilters = null);

public sealed record ScanResult(string Date, string AsOf, int Scanned, int Failed, bool BenchmarkFilter, string? Message, IReadOnlyList<VwapSignal> Signals, VwapDiagnostics? Diagnostics = null, string StrategyName = StrategyCatalog.ScalpVwap, string? DiagnosticsText = null, string? FilterText = null);

/// <summary>Runs only the ScalpVwap strategy over a list of stocks and returns only the stocks that signal.</summary>
/// <summary>Completed 1-minute candles already downloaded, per instrument and day, so a repeat scan only fetches the new minutes.</summary>
public sealed class CandleCache
{
    private readonly Dictionary<(int Token, DateOnly Day), List<Candle>> store = [];

    public List<Candle> For(int token, DateOnly day)
    {
        if (!store.TryGetValue((token, day), out var list)) store[(token, day)] = list = [];
        return list;
    }

    /// <summary>Keeps only the most recent days so memory stays small.</summary>
    public void Prune(DateOnly keepFrom)
    {
        foreach (var key in store.Keys.Where(k => k.Day < keepFrom).ToList()) store.Remove(key);
    }
}

public sealed class Scanner(KiteClient api, IReadOnlyDictionary<string, int> master, CandleCache cache)
{
    private const int BenchmarkToken = 256265; // NIFTY 50 index
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public async Task<ScanResult> ScanAsync(IEnumerable<string> requested, ScanRequest req, CancellationToken ct, Action<string>? log = null)
    {
        var nowIst = DateTimeOffset.UtcNow.ToOffset(Ist);
        var date = req.Date ?? DateOnly.FromDateTime(nowIst.DateTime);
        var open = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 15)), Ist);
        var close = new DateTimeOffset(date.ToDateTime(new TimeOnly(15, 30)), Ist);
        var asOf = req.At is { } at ? new DateTimeOffset(date.ToDateTime(at), Ist) : (nowIst < close ? nowIst : close);
        string D() => date.ToString("yyyy-MM-dd");
        string A() => asOf.ToString("HH:mm");

        var wanted = requested.ToArray();
        var list = wanted.Where(master.ContainsKey).ToArray();
        var unknown = wanted.Except(list, StringComparer.OrdinalIgnoreCase).ToArray();
        var notes = new List<string>();
        if (unknown.Length > 0) notes.Add($"Skipped {unknown.Length} symbol(s) not in the NSE equity master: {string.Join(", ", unknown.Take(10))}{(unknown.Length > 10 ? ", ..." : "")}.");
        if (list.Length == 0) return new(D(), A(), 0, 0, req.UseBenchmark, string.Join(' ', notes.Append("No symbol matched the NSE equity master.")), []);

        cache.Prune(date.AddDays(-4));
        // A candle is complete once its minute has ended; Kite also returns the minute that is still forming.
        List<Candle> Complete(List<Candle> raw) => raw.Where(c => c.Time >= open && c.Time.AddMinutes(1) <= asOf).OrderBy(c => c.Time).ToList();

        // Only the minutes that are not cached yet are downloaded; a finished past day is downloaded once.
        async Task<List<Candle>> BarsAsync(int token)
        {
            var have = cache.For(token, date);
            var from = have.Count > 0 ? have[^1].Time.AddMinutes(1) : open;
            if (from.AddMinutes(1) <= asOf)
                foreach (var c in Complete(await api.MinuteCandlesAsync(token, from, close, ct)).Where(c => have.Count == 0 || c.Time > have[^1].Time))
                    have.Add(c);
            return have.Where(c => c.Time.AddMinutes(1) <= asOf).ToList();
        }

        var blocked = new HashSet<DateTimeOffset>();
        if (req.UseBenchmark)
        {
            var bench = await BarsAsync(BenchmarkToken);
            if (bench.Count == 0)
                return new(D(), A(), 0, 0, true, $"No NIFTY 50 candles for {D()} up to {A()}. The market may be closed; pass a trading day as date.", []);
            var benchState = new VwapState();
            foreach (var c in bench) { benchState.Update(c); if (benchState.Downtrend) blocked.Add(c.Time); }
        }

        var strategyName = StrategyCatalog.Normalize(req.StrategyName);
        var pullbackDiagnostics = strategyName == StrategyCatalog.VwapPullback ? new PullbackDiagnostics() : null;
        var filterDiagnostics = req.Indicators is not null ? new FilterDiagnostics() : null;
        var newState = StrategyCatalog.Factory(strategyName, req, pullbackDiagnostics, filterDiagnostics);
        var signals = new List<VwapSignal>();
        var diagnostics = new VwapDiagnostics();
        int scanned = 0, failed = 0;
        foreach (var symbol in list)
        {
            ct.ThrowIfCancellationRequested();
            var id = master[symbol];
            List<Candle> bars;
            try { bars = await BarsAsync(id); }
            catch (ZerodhaException e) when (e is not ZerodhaAuthException && !e.Message.StartsWith("Cannot reach", StringComparison.Ordinal))
            { failed++; log?.Invoke($"  {symbol}: {e.Message}"); continue; }
            scanned++;
            var state = newState();
            for (var i = 0; i < bars.Count; i++)
            {
                state.Update(bars[i]);
                if (i < bars.Count - req.Lookback) continue;
                if (blocked.Contains(bars[i].Time)) { diagnostics.BenchmarkBlocked++; continue; }
                if (state.Candidate(symbol, id, diagnostics) is { } s) signals.Add(s);
            }
            if (scanned % 25 == 0) log?.Invoke($"  scanned {scanned}/{list.Length}...");
        }
        var ordered = signals.OrderByDescending(s => s.BarTime).ThenByDescending(s => s.Score).ToList();
        return new(D(), A(), scanned, failed, req.UseBenchmark, notes.Count == 0 ? null : string.Join(' ', notes), ordered, diagnostics, strategyName, pullbackDiagnostics?.Describe(diagnostics.BenchmarkBlocked), filterDiagnostics is { Candidates: > 0 } ? filterDiagnostics.Describe() : null);
    }
}
