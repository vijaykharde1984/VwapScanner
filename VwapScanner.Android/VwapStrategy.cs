namespace VwapScanner;

public sealed record Candle(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

/// <summary>Tunable thresholds. The defaults are the GptTrading ScalpVwap preset.</summary>
public sealed class VwapOptions
{
    public decimal MinStretchAtr { get; set; } = 1m;          // price at least this many ATRs below VWAP
    public decimal MinRelativeVolume { get; set; } = 1.0m;    // bar volume vs the average of the prior 20 bars
    public decimal MinBarTurnover { get; set; } = 500000m;    // close * volume of the signal bar, INR
    public decimal MinTargetBps { get; set; } = 15m;          // target distance must clear costs
    public decimal StopAtrMultiple { get; set; } = 1m;
    public decimal RewardRiskRatio { get; set; } = 1.5m;
    public TimeOnly EntryStart { get; set; } = new(9, 30);
    public TimeOnly LastEntry { get; set; } = new(15, 0);
}

public sealed record VwapSignal(string Symbol, int InstrumentId, DateTimeOffset BarTime, decimal Close, decimal Vwap,
    decimal StretchAtr, decimal Atr, decimal RelativeVolume, decimal Score, decimal Stop, decimal Target, long BarVolume = 0,
    decimal? Rsi = null, bool? SuperTrendUp = null, decimal? EmaFast = null, decimal? EmaSlow = null);

/// <summary>
/// Running 1-minute indicators for one instrument over one session (EMA 5/13, ATR 10, 3-bar momentum, session VWAP, relative volume).
/// Same arithmetic as GptTrading's Trend class with the ScalpVwap preset applied.
/// </summary>
public sealed class VwapState
{
    private const int FastEma = 5, SlowEma = 13, AtrPeriod = 10, MomentumBars = 3;
    private readonly List<Candle> history = [];
    private decimal fast, slow, cumulativeValue, cumulativeVolume;

    public decimal Atr { get; private set; }
    public decimal Vwap { get; private set; }
    public decimal Momentum { get; private set; }
    public decimal RelativeVolume { get; private set; }
    public IReadOnlyList<Candle> Bars => history;

    public bool Ready => history.Count >= Math.Max(SlowEma, Math.Max(AtrPeriod + 1, Math.Max(MomentumBars + 1, 21)));
    public bool Downtrend => Ready && fast < slow && Momentum < 0;

    public void Update(Candle bar)
    {
        var previous = history.Count == 0 ? bar.Close : history[^1].Close;
        fast = history.Count == 0 ? bar.Close : fast + 2m / (FastEma + 1) * (bar.Close - fast);
        slow = history.Count == 0 ? bar.Close : slow + 2m / (SlowEma + 1) * (bar.Close - slow);
        var tr = Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - previous), Math.Abs(bar.Low - previous)));
        Atr = history.Count == 0 ? tr : (Atr * (AtrPeriod - 1) + tr) / AtrPeriod;
        var prior = history.TakeLast(20).ToArray();
        var averageVolume = prior.Length == 0 ? 0m : prior.Average(x => (decimal)x.Volume);
        RelativeVolume = averageVolume <= 0 ? 0m : bar.Volume / averageVolume;
        cumulativeValue += (bar.High + bar.Low + bar.Close) / 3m * bar.Volume;
        cumulativeVolume += bar.Volume;
        Vwap = cumulativeVolume == 0 ? bar.Close : cumulativeValue / cumulativeVolume;
        history.Add(bar);
        Momentum = history.Count <= MomentumBars ? 0m : bar.Close - history[^(MomentumBars + 1)].Close;
    }

    /// <summary>
    /// ScalpVwap entry on the latest candle: closed at least MinStretchAtr below VWAP, ticked up, closed in the upper half of its range.
    /// <paramref name="d"/> (optional) counts, for every candle checked, which individual conditions it met, so an empty result can be explained.
    /// </summary>
    public VwapSignal? Candidate(string symbol, int instrumentId, VwapOptions o, VwapDiagnostics? d = null)
    {
        if (!Ready || Atr <= 0 || history.Count < 2) { if (d is not null) d.NotReady++; return null; }
        var b = history[^1];
        var prev = history[^2];
        var stretch = (Vwap - b.Close) / Atr;
        var decision = TimeOnly.FromDateTime(b.Time.AddMinutes(1).DateTime);
        var cStretch = stretch >= o.MinStretchAtr;
        var cTurnUp = b.Close > prev.Close && b.High > b.Low && b.Close >= b.Low + (b.High - b.Low) / 2m;
        var cVolume = RelativeVolume >= o.MinRelativeVolume;
        var cTurnover = b.Close * b.Volume >= o.MinBarTurnover;
        var cTarget = o.MinTargetBps <= 0 || o.StopAtrMultiple * Atr * o.RewardRiskRatio / b.Close * 10000m >= o.MinTargetBps;
        var cWindow = decision >= o.EntryStart && decision <= o.LastEntry;
        if (d is not null)
        {
            d.Evaluated++;
            if (cStretch) d.BelowVwap++;
            if (cTurnUp) d.TurnUp++;
            if (cVolume) d.RelativeVolumeOk++;
            if (cTurnover) d.TurnoverOk++;
            if (cTarget) d.TargetClearsCosts++;
            if (cWindow) d.InEntryWindow++;
        }
        if (!(cStretch && cTurnUp && cVolume && cTurnover && cTarget && cWindow)) return null;
        var risk = o.StopAtrMultiple * Atr;
        var score = stretch + Math.Min(RelativeVolume, 3m) / 10m;
        return new(symbol, instrumentId, b.Time, b.Close, Vwap, stretch, Atr, RelativeVolume, score, b.Close - risk, b.Close + risk * o.RewardRiskRatio, b.Volume);
    }
}

/// <summary>For each candle checked, how many met each condition on its own (a signal needs all of them). Shows which filter is the bottleneck.</summary>
public sealed class VwapDiagnostics
{
    public int NotReady { get; set; }          // fewer than 21 candles so far
    public int Evaluated { get; set; }         // candles checked
    public int BenchmarkBlocked { get; set; }  // skipped because NIFTY 50 was in a downtrend
    public int BelowVwap { get; set; }         // closed at least MinStretchAtr ATRs below VWAP
    public int TurnUp { get; set; }            // closed up, in the upper half of its range
    public int RelativeVolumeOk { get; set; }
    public int TurnoverOk { get; set; }
    public int TargetClearsCosts { get; set; } // ATR large enough for MinTargetBps
    public int InEntryWindow { get; set; }
}
