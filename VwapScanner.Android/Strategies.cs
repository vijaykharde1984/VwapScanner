namespace VwapScanner;

/// <summary>
/// One instrument's running state for a signal strategy. The scanner feeds every completed 1-minute candle of the day to <see cref="Update"/> and, for each of the
/// last Lookback candles, asks <see cref="Candidate"/> whether that candle is a buy signal. The signal carries its own stop and target, so everything after the
/// scan (portfolio sizing, Exit Monitor, grids, CSV) is the same whichever strategy produced it.
/// </summary>
public interface ISignalState
{
    void Update(Candle bar);

    /// <summary>The buy signal on the candle that was fed last, or null. <paramref name="d"/> counts the candles checked (and, for ScalpVwap, each condition).</summary>
    VwapSignal? Candidate(string symbol, int instrumentId, VwapDiagnostics? d = null);
}

/// <summary>The original ScalpVwap strategy (<see cref="VwapState"/> and <see cref="VwapOptions"/>, unchanged) behind the common interface.</summary>
public sealed class ScalpVwapState(VwapOptions options) : ISignalState
{
    private readonly VwapState state = new();

    public void Update(Candle bar) => state.Update(bar);

    public VwapSignal? Candidate(string symbol, int instrumentId, VwapDiagnostics? d = null) =>
        state.Candidate(symbol, instrumentId, options, d);
}

/// <summary>The strategies the app can run, chosen with the setting Scan:StrategyName. To add one: write its state class, add its name here and a case in <see cref="Factory"/>.</summary>
public static class StrategyCatalog
{
    public const string ScalpVwap = "ScalpVwap";
    public const string VwapPullback = "VwapPullback";

    public static readonly string[] Names = [ScalpVwap, VwapPullback];

    /// <summary>The canonical spelling of a strategy name (case-insensitive; empty means ScalpVwap). Throws ArgumentException for an unknown name.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ScalpVwap;
        return Names.FirstOrDefault(n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Strategy '{name}' not found. Available: {string.Join(", ", Names)}.");
    }

    /// <summary>One line for the screen and the README.</summary>
    public static string Describe(string name) =>
        name switch
        {
            VwapPullback => "VWAP Trend Pullback: buys the first confirmed bounce off VWAP in an uptrend (price mostly above VWAP, VWAP rising, higher highs and higher lows).",
            _ => "ScalpVwap: buys a volume-backed turn-up after the price is stretched at least 1 ATR below VWAP.",
        };

    /// <summary>Creates a fresh state for each instrument. <paramref name="pullbackDiagnostics"/> is shared by all states of one scan.</summary>
    public static Func<ISignalState> Factory(string name, ScanRequest request, PullbackDiagnostics? pullbackDiagnostics) =>
        Normalize(name) switch
        {
            VwapPullback => () => new PullbackState(request.Pullback ?? new PullbackOptions(), pullbackDiagnostics),
            _ => () => new ScalpVwapState(request.Options),
        };
}
