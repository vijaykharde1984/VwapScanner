using System.Globalization;

namespace VwapScanner;

/// <summary>
/// The result grids as CSV: the same columns as the web API's vwap-signals-*.csv and vwap-portfolio-*.csv (the portfolio grid has a leading scanned_at column).
/// Used by the grids on screen, the Export / Share buttons and the automatic save at market close.
/// </summary>
public static class GridCsv
{
    public static readonly string[] SignalHeaders =
        ["symbol", "instrument_id", "bar_time", "close", "vwap", "stretch_atr", "atr", "relative_volume", "score", "stop", "target",
         "day_open", "day_high", "day_low", "day_close", "ohlc_as_of"];

    public static readonly string[] PositionHeaders =
        ["scanned_at", "symbol", "instrument_id", "bar_time", "price", "stop", "target", "vwap_exit", "quantity", "notional", "weight_percent",
         "risk_at_stop_inr", "risk_share_percent", "net_gain_at_vwap_inr", "score",
         "day_open", "day_high", "day_low", "day_close", "ohlc_as_of", "price_x_quantity"];

    private static string N(decimal x) => x.ToString("0.####", CultureInfo.InvariantCulture);
    private static string T(DateTimeOffset x) => x.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>The day's open, high, low, close and the time they are as of; empty cells when they are not known (rows saved by an older version).</summary>
    private static string[] OhlcCells(DayOhlc? o) => o is null ? ["", "", "", "", ""] : [N(o.Open), N(o.High), N(o.Low), N(o.Close), o.AsOf];

    public static string[] SignalCells(VwapSignal s, DayOhlc? ohlc = null) =>
        [s.Symbol, s.InstrumentId.ToString(CultureInfo.InvariantCulture), T(s.BarTime), N(s.Close), N(s.Vwap), N(s.StretchAtr), N(s.Atr), N(s.RelativeVolume), N(s.Score), N(s.Stop), N(s.Target), .. OhlcCells(ohlc)];

    public static string[] PositionCells(DateTimeOffset at, PortfolioPosition p, DayOhlc? ohlc = null) =>
        [at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), p.Symbol, p.InstrumentId.ToString(CultureInfo.InvariantCulture), T(p.BarTime), N(p.Price), N(p.Stop), N(p.Target), N(p.VwapExit),
         p.Quantity.ToString(CultureInfo.InvariantCulture), N(p.Notional), N(p.WeightPercent), N(p.RiskAtStopInr), N(p.RiskSharePercent), N(p.NetGainAtVwapInr), N(p.Score),
         .. OhlcCells(ohlc), N(p.Price * p.Quantity)];

    public static string ToCsv(string[] headers, IEnumerable<string[]> rows) =>
        string.Join('\n', rows.Select(r => string.Join(',', r)).Prepend(string.Join(',', headers))) + "\n";

    public static string SignalsCsv(IEnumerable<VwapSignal> signals, Func<VwapSignal, DayOhlc?>? ohlc = null) =>
        ToCsv(SignalHeaders, signals.Select(s => SignalCells(s, ohlc?.Invoke(s))));

    public static string PortfolioCsv(IEnumerable<(DateTimeOffset At, PortfolioPosition Position)> positions, Func<PortfolioPosition, DayOhlc?>? ohlc = null) =>
        ToCsv(PositionHeaders, positions.Select(x => PositionCells(x.At, x.Position, ohlc?.Invoke(x.Position))));
}
