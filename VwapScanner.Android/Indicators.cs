namespace VwapScanner;

/// <summary>Periods of the shared indicators shown on every signal and used by the optional filters. VWAP and ATR(10) are part of the strategies themselves.</summary>
public sealed class IndicatorOptions
{
    public int EmaFast { get; set; } = 9;
    public int EmaSlow { get; set; } = 20;
    public int RsiPeriod { get; set; } = 14;
    public int SuperTrendPeriod { get; set; } = 10;
    public decimal SuperTrendMultiplier { get; set; } = 3m;

    public void Validate()
    {
        if (EmaFast < 2 || EmaSlow > 200 || EmaFast >= EmaSlow)
            throw new ArgumentException("Indicators: the fast EMA period must be at least 2 and smaller than the slow EMA period (at most 200).");
        if (RsiPeriod is < 2 or > 50)
            throw new ArgumentException("Indicators: the RSI period must be between 2 and 50.");
        if (SuperTrendPeriod is < 2 or > 50 || SuperTrendMultiplier is < 0.5m or > 10m)
            throw new ArgumentException("Indicators: the SuperTrend period must be 2-50 and its multiplier 0.5-10.");
    }
}

/// <summary>
/// Optional extra tests on a signal the strategy already found. Each one is off by default, so the strategies behave as before until you switch a filter on.
/// SuperTrend: the SuperTrend must be bullish (price above its line). EMA: the close must be above the fast EMA. RSI: the RSI must lie between MinRsi and MaxRsi.
/// </summary>
public sealed class SignalFilterOptions
{
    public bool UseSuperTrend { get; set; }
    public bool UseEma { get; set; }
    public bool UseRsi { get; set; }
    public decimal MinRsi { get; set; }
    public decimal MaxRsi { get; set; } = 100m;

    public bool Any => UseSuperTrend || UseEma || UseRsi;

    /// <summary>ScalpVwap buys weakness, so the RSI window defaults to oversold-ish values. All three filters start enabled.</summary>
    public static SignalFilterOptions ForScalp() =>
        new()
        {
            UseSuperTrend = true,
            UseEma = true,
            UseRsi = true,
            MinRsi = 0m,
            MaxRsi = 40m,
        };

    /// <summary>VwapPullback buys a dip inside an uptrend: not yet overbought, not collapsing.</summary>
    public static SignalFilterOptions ForPullback() => new() { MinRsi = 45m, MaxRsi = 75m };

    public void Validate(string name)
    {
        if (MinRsi < 0 || MaxRsi > 100 || MinRsi >= MaxRsi)
            throw new ArgumentException($"{name}: the RSI window must satisfy 0 <= min < max <= 100.");
    }
}

public sealed class FilterDiagnostics
{
    public int Candidates { get; set; }     // strategy signals that reached the filters
    public int Rejected { get; set; }
    public int RsiFailed { get; set; }
    public int SuperTrendFailed { get; set; }
    public int EmaFailed { get; set; }

    public string Describe() =>
        $"Indicator filters: {Candidates} strategy signal(s) checked, {Rejected} rejected (RSI {RsiFailed}, SuperTrend {SuperTrendFailed}, EMA {EmaFailed}).";
}

/// <summary>Running EMA fast / slow, RSI (Wilder) and SuperTrend, fed one completed candle at a time.</summary>
public sealed class IndicatorSet(IndicatorOptions options)
{
    private readonly IndicatorOptions o = options;
    private int count;
    private decimal previousClose;

    // RSI
    private decimal avgGain, avgLoss, gainSum, lossSum;

    // SuperTrend
    private decimal trSum, atr, finalUpper, finalLower;
    private bool bandsStarted;

    public decimal EmaFast { get; private set; }
    public decimal EmaSlow { get; private set; }

    /// <summary>0-100, or null until RsiPeriod + 1 candles have been seen.</summary>
    public decimal? Rsi { get; private set; }

    /// <summary>True when price is above the SuperTrend line (bullish), false when below, null until SuperTrendPeriod candles have been seen.</summary>
    public bool? SuperTrendUp { get; private set; }

    public decimal? SuperTrendLine { get; private set; }

    public void Update(Candle bar)
    {
        var first = count == 0;
        EmaFast = first ? bar.Close : EmaFast + 2m / (o.EmaFast + 1) * (bar.Close - EmaFast);
        EmaSlow = first ? bar.Close : EmaSlow + 2m / (o.EmaSlow + 1) * (bar.Close - EmaSlow);

        if (!first)
        {
            var change = bar.Close - previousClose;
            var gain = Math.Max(change, 0m);
            var loss = Math.Max(-change, 0m);
            if (count <= o.RsiPeriod)
            {
                gainSum += gain;
                lossSum += loss;
                if (count == o.RsiPeriod)
                {
                    avgGain = gainSum / o.RsiPeriod;
                    avgLoss = lossSum / o.RsiPeriod;
                    Rsi = ToRsi();
                }
            }
            else
            {
                avgGain = (avgGain * (o.RsiPeriod - 1) + gain) / o.RsiPeriod;
                avgLoss = (avgLoss * (o.RsiPeriod - 1) + loss) / o.RsiPeriod;
                Rsi = ToRsi();
            }
        }

        var tr = first
            ? bar.High - bar.Low
            : Math.Max(bar.High - bar.Low, Math.Max(Math.Abs(bar.High - previousClose), Math.Abs(bar.Low - previousClose)));
        if (count < o.SuperTrendPeriod)
        {
            trSum += tr;
            if (count == o.SuperTrendPeriod - 1)
            {
                atr = trSum / o.SuperTrendPeriod;
                StartBands(bar);
            }
        }
        else
        {
            atr = (atr * (o.SuperTrendPeriod - 1) + tr) / o.SuperTrendPeriod;
            AdvanceBands(bar);
        }

        previousClose = bar.Close;
        count++;
    }

    private decimal ToRsi() => avgLoss == 0m ? (avgGain == 0m ? 50m : 100m) : 100m - 100m / (1m + avgGain / avgLoss);

    private void StartBands(Candle bar)
    {
        var mid = (bar.High + bar.Low) / 2m;
        finalUpper = mid + o.SuperTrendMultiplier * atr;
        finalLower = mid - o.SuperTrendMultiplier * atr;
        bandsStarted = true;
        SuperTrendUp = bar.Close >= mid;
        SuperTrendLine = SuperTrendUp == true ? finalLower : finalUpper;
    }

    private void AdvanceBands(Candle bar)
    {
        if (!bandsStarted)
            return;
        var mid = (bar.High + bar.Low) / 2m;
        var upper = mid + o.SuperTrendMultiplier * atr;
        var lower = mid - o.SuperTrendMultiplier * atr;
        var previousUpper = finalUpper;
        var previousLower = finalLower;
        finalLower = lower > previousLower || previousClose < previousLower ? lower : previousLower;
        finalUpper = upper < previousUpper || previousClose > previousUpper ? upper : previousUpper;
        var wasUp = SuperTrendUp == true;
        var up = wasUp ? !(bar.Close < finalLower) : bar.Close > finalUpper;
        SuperTrendUp = up;
        SuperTrendLine = up ? finalLower : finalUpper;
    }
}

/// <summary>
/// Wraps any strategy: attaches RSI, SuperTrend and EMA values to every signal it finds (so they show on screen and in the CSV) and, for the filters that are switched on,
/// drops a signal that fails them. The strategy itself is not changed.
/// </summary>
public sealed class FilteredState(ISignalState inner, IndicatorOptions indicators, SignalFilterOptions? filter, FilterDiagnostics? diagnostics = null) : ISignalState
{
    private readonly IndicatorSet set = new(indicators);

    public void Update(Candle bar)
    {
        inner.Update(bar);
        set.Update(bar);
    }

    public VwapSignal? Candidate(string symbol, int instrumentId, VwapDiagnostics? d = null)
    {
        if (inner.Candidate(symbol, instrumentId, d) is not { } s)
            return null;
        s = s with { Rsi = set.Rsi, SuperTrendUp = set.SuperTrendUp, EmaFast = set.EmaFast, EmaSlow = set.EmaSlow };
        if (filter is null || !filter.Any)
            return s;

        // A filter whose indicator is not available yet cannot approve the signal.
        var rsiBad = filter.UseRsi && (set.Rsi is not { } r || r < filter.MinRsi || r > filter.MaxRsi);
        var trendBad = filter.UseSuperTrend && set.SuperTrendUp != true;
        var emaBad = filter.UseEma && s.Close <= set.EmaFast;
        if (diagnostics is not null)
        {
            diagnostics.Candidates++;
            diagnostics.RsiFailed += rsiBad ? 1 : 0;
            diagnostics.SuperTrendFailed += trendBad ? 1 : 0;
            diagnostics.EmaFailed += emaBad ? 1 : 0;
            diagnostics.Rejected += rsiBad || trendBad || emaBad ? 1 : 0;
        }
        return rsiBad || trendBad || emaBad ? null : s;
    }
}
