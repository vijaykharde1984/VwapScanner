namespace VwapScanner;

/// <summary>A stock's open, high, low and close for the trading day, built from its 1-minute candles up to <see cref="AsOf"/> (IST, HH:mm).
/// The close is the close of the last completed candle; after the market closes it is the day's closing value.</summary>
public sealed record DayOhlc(decimal Open, decimal High, decimal Low, decimal Close, string AsOf);
