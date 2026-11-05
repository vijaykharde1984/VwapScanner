using System.Globalization;

namespace VwapScanner;

/// <summary>
/// The result grids as CSV: the same columns as the web API's vwap-signals-*.csv and vwap-portfolio-*.csv (the portfolio grid has a leading scanned_at column).
/// Used by the grids on screen, the Export / Share buttons and the automatic save at market close.
/// </summary>
public static class GridCsv
{
    public static readonly string[] SignalHeaders =
        ["symbol", "instrument_id", "bar_time", "close", "vwap", "stretch_atr", "atr", "relative_volume", "score", "stop", "target"];

    public static readonly string[] PositionHeaders =
        ["scanned_at", "symbol", "instrument_id", "bar_time", "price", "stop", "target", "vwap_exit", "quantity", "notional", "weight_percent",
         "risk_at_stop_inr", "risk_share_percent", "net_gain_at_vwap_inr", "score"];

    private static string N(decimal x) => x.ToString("0.####", CultureInfo.InvariantCulture);
    private static string T(DateTimeOffset x) => x.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    public static string[] SignalCells(VwapSignal s) =>
        [s.Symbol, s.InstrumentId.ToString(CultureInfo.InvariantCulture), T(s.BarTime), N(s.Close), N(s.Vwap), N(s.StretchAtr), N(s.Atr), N(s.RelativeVolume), N(s.Score), N(s.Stop), N(s.Target)];

    public static string[] PositionCells(DateTimeOffset at, PortfolioPosition p) =>
        [at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), p.Symbol, p.InstrumentId.ToString(CultureInfo.InvariantCulture), T(p.BarTime), N(p.Price), N(p.Stop), N(p.Target), N(p.VwapExit),
         p.Quantity.ToString(CultureInfo.InvariantCulture), N(p.Notional), N(p.WeightPercent), N(p.RiskAtStopInr), N(p.RiskSharePercent), N(p.NetGainAtVwapInr), N(p.Score)];

    public static string ToCsv(string[] headers, IEnumerable<string[]> rows) =>
        string.Join('\n', rows.Select(r => string.Join(',', r)).Prepend(string.Join(',', headers))) + "\n";

    public static string SignalsCsv(IEnumerable<VwapSignal> signals) => ToCsv(SignalHeaders, signals.Select(SignalCells));

    public static string PortfolioCsv(IEnumerable<(DateTimeOffset At, PortfolioPosition Position)> positions) =>
        ToCsv(PositionHeaders, positions.Select(x => PositionCells(x.At, x.Position)));
}
