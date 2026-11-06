using System.Globalization;

namespace VwapScanner;

/// <summary>The Exit Monitor and Reversal Guard settings. <paramref name="CloseOut"/> is the IST time of day when every position still held is squared off.</summary>
public sealed record ExitRules(bool GuardEnabled, decimal ArmPercent, decimal GivebackPercent, int FadeCandles, decimal BreakevenBps, int ProjectionMinutes, TimeSpan CloseOut);

/// <summary>
/// Decides, from the 1-minute candles of the day, whether a portfolio row (a long position bought at the close of its signal candle) should now be sold.
/// It replays the candles after the buy in order, so the answer is the same whether it is asked every 30 seconds or once after an app restart.
///
/// Exit Monitor (always):
///   EXIT_TARGET  a candle's high reaches the target.
///   EXIT_STOP    a candle's low reaches the stop. When one candle touches both, the stop wins (the candle does not say which came first).
///   EXIT_CLOSE   still held at the close-out time: square off at the last close.
/// Reversal Guard (when enabled), for a position that has risen but is fading:
///   Armed        the highest price since the buy has covered ArmPercent of the way from the buy price to the target.
///   Breakeven    once armed, the stop moves up to the buy price plus a small cost buffer: touching it is EXIT_BREAKEVEN.
///   Reversal     once armed, after a completed candle: price gave back GivebackPercent of the peak gain, or FadeCandles lower closes in a row below the day's
///                VWAP, or the trend of the last closes projected ProjectionMinutes ahead ends at or below the breakeven level: EXIT_REVERSAL.
/// High / low checks use the candle that is still forming too (a high only rises, a low only falls, so those hits are final); the reversal rules, which depend on a
/// close, are applied to completed candles only.
/// </summary>
public static class ExitEvaluator
{
    private static string F(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    /// <param name="candles">The day's 1-minute candles from 09:15 up to now (oldest first); the VWAP is built from all of them.</param>
    /// <param name="final">True at or after the close-out time: a row that is still held is squared off.</param>
    public static ExitDecision Evaluate(PortfolioPosition p, IReadOnlyList<Candle> candles, DateTimeOffset now, ExitRules r, bool final)
    {
        var entry = p.BarTime.AddMinutes(1); // bought at the close of the signal candle: the next candle is the first one that counts
        var breakeven = p.Price * (1 + r.BreakevenBps / 10000m);
        var range = p.Target - p.Price;
        var guard = r.GuardEnabled && range > 0;
        decimal pv = 0, vol = 0, peak = 0;
        var peakTime = "";
        var armed = false;
        var fading = false;
        var closes = new List<decimal>();
        Candle? last = null;

        foreach (var c in candles)
        {
            var typical = (c.High + c.Low + c.Close) / 3m;
            pv += typical * c.Volume; vol += c.Volume; closes.Add(c.Close);
            if (c.Time < entry) continue;
            if (c.Time.TimeOfDay >= r.CloseOut) break;
            last = c;
            var time = c.Time.ToString("HH:mm", CultureInfo.InvariantCulture);

            var floor = armed ? Math.Max(p.Stop, breakeven) : p.Stop;
            if (c.Low <= floor)
            {
                return armed && breakeven > p.Stop
                    ? new(ExitDecision.Breakeven, time, floor, $"Back at the buy price {F(p.Price)} after rising to {F(peak)}", peak, peakTime, ExitDecision.Fading)
                    : new(ExitDecision.Stop, time, p.Stop, $"Stop {F(p.Stop)} reached", peak, peakTime, guard ? (armed ? ExitDecision.Fading : ExitDecision.Watch) : "");
            }
            if (c.High >= p.Target)
                return new(ExitDecision.Target, time, p.Target, $"Target {F(p.Target)} reached", Math.Max(peak, c.High), peak >= c.High ? peakTime : time, guard ? ExitDecision.Armed : "");

            if (c.High > peak) { peak = c.High; peakTime = time; }
            if (guard && !armed && (peak - p.Price) / range * 100m >= r.ArmPercent) armed = true;
            if (!armed) continue;

            var previous = closes.Count >= 2 ? closes[^2] : c.Close;
            fading = c.Close < previous && c.Close < peak;
            if (c.Time.AddMinutes(1) > now) continue; // still forming: its close can still change

            var gain = peak - p.Price;
            if (c.Close <= breakeven)
                return new(ExitDecision.Breakeven, time, c.Close, $"Closed at {F(c.Close)} back at the buy price {F(p.Price)} after rising to {F(peak)}", peak, peakTime, ExitDecision.Fading);
            if (gain > 0 && peak - c.Close >= r.GivebackPercent / 100m * gain)
                return new(ExitDecision.Reversal, time, c.Close, $"Gave back {(peak - c.Close) / gain * 100m:0}% of the gain (peak {F(peak)} now {F(c.Close)})", peak, peakTime, ExitDecision.Fading);

            var n = r.FadeCandles;
            if (closes.Count >= n + 1 && c.Close < pv / Math.Max(vol, 1m))
            {
                var lower = true;
                for (var i = closes.Count - n; i < closes.Count; i++) lower &= closes[i] < closes[i - 1];
                if (lower)
                    return new(ExitDecision.Reversal, time, c.Close, $"{n} lower closes in a row below VWAP {F(pv / Math.Max(vol, 1m))} (peak {F(peak)} now {F(c.Close)})", peak, peakTime, ExitDecision.Fading);
            }

            var k = Math.Min(closes.Count, Math.Max(n + 1, 4));
            if (k >= 3)
            {
                var slope = Slope(closes.GetRange(closes.Count - k, k));
                var projected = c.Close + slope * r.ProjectionMinutes;
                if (slope < 0 && projected <= breakeven)
                    return new(ExitDecision.Reversal, time, c.Close, $"Falling {F(-slope)} a minute: {F(projected)} in {r.ProjectionMinutes} min is at or below the buy price {F(p.Price)}", peak, peakTime, ExitDecision.Fading);
            }
        }

        if (final)
            return new(ExitDecision.Close, $"{(int)r.CloseOut.TotalHours:00}:{r.CloseOut.Minutes:00}", last?.Close ?? p.Price,
                $"Squared off at the close-out time (target and stop not reached)", peak, peakTime, guard ? (armed ? ExitDecision.Armed : ExitDecision.Watch) : "");
        var state = !guard ? "" : !armed ? ExitDecision.Watch : fading ? ExitDecision.Fading : ExitDecision.Armed;
        var note = armed ? $"Armed: sells at {F(Math.Max(p.Stop, breakeven))} or on a reversal" : "";
        return new(ExitDecision.Hold, "", 0, note, peak, peakTime, state);
    }

    /// <summary>
    /// Net gain in INR of buying the position at its price and selling all of it at <paramref name="exitPrice"/>: same formula as the portfolio's net_gain_at_vwap_inr
    /// (sell slippage on the exit price, then the buy and sell fees), so the two columns can be compared. Negative for a loss.
    /// </summary>
    public static decimal NetGain(PortfolioPosition p, decimal exitPrice, PortfolioOptions o)
    {
        var sell = exitPrice * (1 - o.SlippageBpsPerSide / 10000m);
        return Math.Round(p.Quantity * (sell - p.Price) - o.Fees.Cost(p.Quantity * p.Price, true) - o.Fees.Cost(p.Quantity * sell, false), 2);
    }

    /// <summary>Least-squares slope of the values per step (price change per minute).</summary>
    private static decimal Slope(List<decimal> y)
    {
        decimal n = y.Count, sx = 0, sy = 0, sxy = 0, sxx = 0;
        for (var i = 0; i < y.Count; i++) { sx += i; sy += y[i]; sxy += i * y[i]; sxx += (decimal)i * i; }
        var d = n * sxx - sx * sx;
        return d == 0 ? 0 : (n * sxy - sx * sy) / d;
    }
}
