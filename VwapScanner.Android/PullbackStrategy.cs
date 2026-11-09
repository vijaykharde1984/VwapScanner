namespace VwapScanner;

/// <summary>
/// Tunable thresholds of the VWAP Trend Pullback strategy (long only). Same idea as the "VWAP bounce" in the Rani trading guide: in an uptrend the price
/// runs above VWAP, pulls back to the VWAP area, buyers react there, and the trade is taken on the confirmation candle.
/// </summary>
public sealed class PullbackOptions
{
    // Trend regime (must all hold on the signal candle)
    public int TrendBars { get; set; } = 30;                 // window used to judge the trend, in 1-minute candles
    public decimal MinAboveVwapPercent { get; set; } = 70m;  // share of the last TrendBars closes that were above VWAP
    public decimal MinVwapSlopeAtr { get; set; } = 0.5m;     // VWAP must have risen at least this many ATRs over TrendBars
    public int SwingBars { get; set; } = 3;                  // candles on each side that make a swing high / low

    // Pullback to VWAP
    public int PullbackBars { get; set; } = 8;               // the touch must be within the last N candles (also the cool-down after a signal)
    public decimal TouchAtr { get; set; } = 0.3m;            // a low this many ATRs above VWAP (or lower) counts as a touch
    public decimal MaxBreakAtr { get; set; } = 0.5m;         // a low more than this many ATRs below VWAP is a break, not a pullback

    // Risk and reward
    public decimal StopBufferAtr { get; set; } = 0.25m;      // stop = lower of (pullback low, VWAP) minus this many ATRs
    public decimal MinRiskAtr { get; set; } = 0.5m;          // reject a stop closer than this (noise)
    public decimal MaxRiskAtr { get; set; } = 2.5m;          // reject a stop further than this (too wide)
    public decimal RewardRiskRatio { get; set; } = 2.0m;     // the guide's minimum is 1:2
    public decimal MinRoomRisk { get; set; } = 1.5m;         // the day's high must be at least this many risks above the entry (room to the next level)
    public decimal MinTargetBps { get; set; } = 15m;         // target distance must clear costs

    // Participation and time
    public decimal MinRelativeVolume { get; set; } = 1.0m;   // signal candle volume vs the average of the prior 20 candles
    public decimal MinBarTurnover { get; set; } = 500000m;   // close * volume of the signal candle, INR
    public TimeOnly EntryStart { get; set; } = new(9, 45);   // the trend needs some candles to form
    public TimeOnly LastEntry { get; set; } = new(14, 45);

    public void Validate()
    {
        if (TrendBars is < 10 or > 120 || SwingBars is < 1 or > 10 || PullbackBars is < 2 or > 60)
            throw new ArgumentException("Pullback: trend bars 10-120, swing bars 1-10, pullback bars 2-60.");
        if (MinAboveVwapPercent is < 1 or > 100 || MinVwapSlopeAtr < 0 || TouchAtr < 0 || MaxBreakAtr < 0 || StopBufferAtr < 0)
            throw new ArgumentException("Pullback: percentages and ATR distances must not be negative (above-VWAP share 1-100).");
        if (MinRiskAtr <= 0 || MaxRiskAtr <= MinRiskAtr || RewardRiskRatio <= 0 || MinRoomRisk < 0)
            throw new ArgumentException("Pullback: risk limits must satisfy 0 < min risk < max risk, and reward : risk must be positive.");
        if (EntryStart >= LastEntry)
            throw new ArgumentException("Pullback: the last entry time must be later than the first entry time.");
    }
}

/// <summary>For each candle checked by the pullback strategy, how many met each condition on its own (a signal needs all of them). Shows which filter is the bottleneck.</summary>
public sealed class PullbackDiagnostics
{
    public int NotReady { get; set; }
    public int Evaluated { get; set; }
    public int InUptrend { get; set; }        // above VWAP most of the time, VWAP rising, EMA 9 > 21, higher highs and higher lows
    public int Touched { get; set; }          // low reached the VWAP zone within the last PullbackBars candles
    public int Confirmed { get; set; }        // bullish candle closing above the previous high, upper half of its range, above VWAP
    public int VolumeOk { get; set; }
    public int TurnoverOk { get; set; }
    public int RiskOk { get; set; }           // stop distance between MinRiskAtr and MaxRiskAtr
    public int RoomOk { get; set; }           // day's high far enough above the entry
    public int TargetClearsCosts { get; set; }
    public int InEntryWindow { get; set; }
    public int CooledDown { get; set; }       // signals dropped because another one fired in the last PullbackBars candles

    public string Describe(int niftyBlocked) =>
        $"Candles checked {Evaluated} (not ready {NotReady}, NIFTY downtrend {niftyBlocked}). Each test alone: uptrend {InUptrend}, VWAP touch {Touched}, "
        + $"confirmation {Confirmed}, rel. volume {VolumeOk}, turnover {TurnoverOk}, stop distance {RiskOk}, room to day high {RoomOk}, "
        + $"target clears costs {TargetClearsCosts}, entry window {InEntryWindow}; dropped by cool-down {CooledDown}.";
}

/// <summary>
/// Running 1-minute state of one instrument for the VWAP Trend Pullback strategy (session VWAP, EMA 9/21, ATR 10, relative volume, swing highs and lows).
/// Same shape as <see cref="VwapState"/>: feed every completed candle to <see cref="Update"/>, then ask <see cref="Candidate"/> about the latest one.
///
/// Long entry on the latest candle, all of:
///   Uptrend        at least MinAboveVwapPercent of the last TrendBars closes above VWAP, VWAP up by MinVwapSlopeAtr ATRs over that window, EMA 9 above EMA 21,
///                  and the last two swing highs and the last two swing lows both rising (higher highs and higher lows).
///   Pullback       within the last PullbackBars candles a low came within TouchAtr ATRs of VWAP (or dipped at most MaxBreakAtr ATRs under it).
///   Confirmation   the candle is green, closes above the previous candle's high and in the upper half of its range, and closes above VWAP.
///   Participation  relative volume and turnover of the candle.
///   Risk           stop = lower of (pullback low, VWAP) minus StopBufferAtr ATRs, between MinRiskAtr and MaxRiskAtr ATRs away;
///                  target = RewardRiskRatio x risk; the day's high is at least MinRoomRisk risks above the entry.
///   Time           between EntryStart and LastEntry.
/// A signal on a candle is dropped when another signal condition set was met in the previous PullbackBars candles, so one pullback gives one signal.
/// The signal fields keep the meaning they have for ScalpVwap, so the portfolio optimizer, Exit Monitor and grids work unchanged: StretchAtr is
/// (VWAP - close) / ATR, which is negative here because the price is above VWAP.
/// </summary>
public sealed class PullbackState(PullbackOptions options, PullbackDiagnostics? diagnostics = null) : ISignalState
{
    private const int FastEma = 9, SlowEma = 21, AtrPeriod = 10;
    private readonly PullbackOptions o = options;
    private readonly List<Candle> bars = [];
    private readonly List<decimal> vwapSeries = [];
    private decimal fast, slow, cumulativeValue, cumulativeVolume;
    private int lastRaw = -1;       // index of the last candle that met every condition (before the latest one)
    private int currentRaw = -1;    // index of the latest candle if it met every condition, else -1
    private Plan? currentPlan;
    private bool currentCooled = true;

    public decimal Atr { get; private set; }
    public decimal Vwap { get; private set; }
    public decimal RelativeVolume { get; private set; }

    public bool Ready => bars.Count >= Math.Max(Math.Max(o.TrendBars, 21), 2 * o.SwingBars + 5) + 1;

    private sealed record Plan(decimal Stop, decimal Target, decimal Score, decimal StretchAtr);

    public void Update(Candle bar)
    {
        var previous = bars.Count == 0 ? bar.Close : bars[^1].Close;
        fast = bars.Count == 0 ? bar.Close : fast + 2m / (FastEma + 1) * (bar.Close - fast);
        slow = bars.Count == 0 ? bar.Close : slow + 2m / (SlowEma + 1) * (bar.Close - slow);
        var tr = Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - previous), Math.Abs(bar.Low - previous)));
        Atr = bars.Count == 0 ? tr : (Atr * (AtrPeriod - 1) + tr) / AtrPeriod;
        var count = Math.Min(20, bars.Count);
        decimal volumeSum = 0;
        for (var i = bars.Count - count; i < bars.Count; i++)
            volumeSum += bars[i].Volume;
        var averageVolume = count == 0 ? 0m : volumeSum / count;
        RelativeVolume = averageVolume <= 0 ? 0m : bar.Volume / averageVolume;
        cumulativeValue += (bar.High + bar.Low + bar.Close) / 3m * bar.Volume;
        cumulativeVolume += bar.Volume;
        Vwap = cumulativeVolume == 0 ? bar.Close : cumulativeValue / cumulativeVolume;
        bars.Add(bar);
        vwapSeries.Add(Vwap);

        // Judge this candle now (without counting), so the one-signal-per-pullback rule is the same however many candles a scan looks at.
        if (currentRaw >= 0)
            lastRaw = currentRaw;
        currentPlan = Evaluate(null);
        currentRaw = currentPlan is null ? -1 : bars.Count - 1;
        currentCooled = lastRaw < 0 || bars.Count - 1 - lastRaw >= o.PullbackBars;
    }

    public VwapSignal? Candidate(string symbol, int instrumentId, VwapDiagnostics? d = null)
    {
        if (!Ready || Atr <= 0 || bars.Count < 2)
        {
            if (d is not null) d.NotReady++;
            if (diagnostics is not null) diagnostics.NotReady++;
            return null;
        }
        if (d is not null) d.Evaluated++;
        if (diagnostics is not null)
        {
            diagnostics.Evaluated++;
            Evaluate(diagnostics); // second pass only to count each condition on its own
            if (currentPlan is not null && !currentCooled) diagnostics.CooledDown++;
        }
        if (currentPlan is null || !currentCooled)
            return null;
        var b = bars[^1];
        return new(symbol, instrumentId, b.Time, b.Close, Vwap, currentPlan.StretchAtr, Atr, RelativeVolume, currentPlan.Score, currentPlan.Stop, currentPlan.Target, b.Volume);
    }

    /// <summary>Checks every condition on the latest candle. With <paramref name="count"/> every condition is evaluated and counted; without it the check stops at the first cheap failure.</summary>
    private Plan? Evaluate(PullbackDiagnostics? count)
    {
        if (!Ready || Atr <= 0 || bars.Count < 2)
            return null;
        var n = bars.Count;
        var b = bars[n - 1];
        var prev = bars[n - 2];

        var decision = TimeOnly.FromDateTime(b.Time.AddMinutes(1).DateTime);
        var cWindow = decision >= o.EntryStart && decision <= o.LastEntry;
        var cConfirm = b.Close > b.Open && b.Close > prev.High && b.High > b.Low
            && b.Close >= b.Low + (b.High - b.Low) / 2m && b.Close > Vwap;
        var cVolume = RelativeVolume >= o.MinRelativeVolume;
        var cTurnover = b.Close * b.Volume >= o.MinBarTurnover;
        if (count is null && !(cWindow && cConfirm && cVolume && cTurnover))
            return null;

        // Uptrend regime.
        var window = Math.Min(o.TrendBars, n - 1);
        var above = 0;
        for (var i = n - window; i < n; i++)
            if (bars[i].Close > vwapSeries[i]) above++;
        var abovePercent = above * 100m / window;
        var slopeAtr = (Vwap - vwapSeries[n - 1 - window]) / Atr;
        var highs = Swings(true);
        var lows = Swings(false);
        var structure = highs.Count >= 2 && lows.Count >= 2
            && bars[highs[0]].High > bars[highs[1]].High && bars[lows[0]].Low > bars[lows[1]].Low;
        var cTrend = abovePercent >= o.MinAboveVwapPercent && slopeAtr >= o.MinVwapSlopeAtr && fast > slow && structure;

        // Pullback: a recent low at the VWAP zone. The lowest low since the first touch becomes the stop reference.
        var touchFrom = -1;
        for (var i = Math.Max(0, n - o.PullbackBars); i < n; i++)
        {
            var vwapThen = vwapSeries[i];
            if (bars[i].Low <= vwapThen + o.TouchAtr * Atr && bars[i].Low >= vwapThen - o.MaxBreakAtr * Atr)
            {
                touchFrom = i;
                break;
            }
        }
        var cTouch = touchFrom >= 0;

        // Risk and reward.
        var pullbackLow = decimal.MaxValue;
        if (cTouch)
            for (var i = touchFrom; i < n; i++)
                pullbackLow = Math.Min(pullbackLow, bars[i].Low);
        var stop = cTouch ? Math.Min(pullbackLow, Vwap) - o.StopBufferAtr * Atr : 0m;
        var risk = b.Close - stop;
        var cRisk = cTouch && risk >= o.MinRiskAtr * Atr && risk <= o.MaxRiskAtr * Atr;
        decimal dayHigh = 0;
        for (var i = 0; i < n; i++)
            dayHigh = Math.Max(dayHigh, bars[i].High);
        var cRoom = cRisk && dayHigh - b.Close >= o.MinRoomRisk * risk;
        var target = b.Close + risk * o.RewardRiskRatio;
        var cTarget = cRisk && (o.MinTargetBps <= 0 || (target - b.Close) / b.Close * 10000m >= o.MinTargetBps);

        if (count is not null)
        {
            count.InUptrend += cTrend ? 1 : 0;
            count.Touched += cTouch ? 1 : 0;
            count.Confirmed += cConfirm ? 1 : 0;
            count.VolumeOk += cVolume ? 1 : 0;
            count.TurnoverOk += cTurnover ? 1 : 0;
            count.RiskOk += cRisk ? 1 : 0;
            count.RoomOk += cRoom ? 1 : 0;
            count.TargetClearsCosts += cTarget ? 1 : 0;
            count.InEntryWindow += cWindow ? 1 : 0;
        }
        if (!(cWindow && cConfirm && cVolume && cTurnover && cTrend && cTouch && cRisk && cRoom && cTarget))
            return null;

        var score = 1m + Math.Min(RelativeVolume, 3m) / 3m + Math.Min(slopeAtr, 2m) / 2m + Math.Min((dayHigh - b.Close) / risk, 3m) / 6m;
        return new(stop, target, score, (Vwap - b.Close) / Atr);
    }

    /// <summary>Indexes of confirmed swing highs (or lows), newest first, at most two. A swing needs SwingBars candles on each side, so the newest candles cannot be one yet.</summary>
    private List<int> Swings(bool highs)
    {
        var s = o.SwingBars;
        var found = new List<int>(2);
        for (var k = bars.Count - 1 - s; k >= s && found.Count < 2; k--)
        {
            var v = highs ? bars[k].High : bars[k].Low;
            var ok = true;
            for (var j = 1; j <= s && ok; j++)
                ok = highs
                    ? v > bars[k - j].High && v >= bars[k + j].High
                    : v < bars[k - j].Low && v <= bars[k + j].Low;
            if (ok)
                found.Add(k);
        }
        return found;
    }
}
