namespace VwapScanner;

/// <summary>
/// What the Exit Monitor says about one portfolio row: keep holding it, or sell it and why. <see cref="Time"/> is the 1-minute candle (IST, HH:mm) in which the exit
/// happened and <see cref="Price"/> the price to sell at (the target / stop / breakeven level, or the candle close for a reversal or square-off).
/// <see cref="Peak"/> is the highest price since the buy, <see cref="Guard"/> the Reversal Guard state while holding (WATCH, ARMED or FADING).
/// <see cref="NetGain"/> is the profit or loss in INR of selling at <see cref="Price"/> instead of holding, after both legs' fees and the sell slippage (null while holding).
/// Reasons never contain commas, so they are safe in the CSV files.
/// </summary>
public sealed record ExitDecision(
    string Status,
    string Time,
    decimal Price,
    string Reason,
    decimal Peak = 0,
    string PeakTime = "",
    string Guard = "",
    decimal? NetGain = null
)
{
    public const string Hold = "HOLD",
        Target = "EXIT_TARGET",
        Stop = "EXIT_STOP",
        Reversal = "EXIT_REVERSAL",
        Breakeven = "EXIT_BREAKEVEN",
        Close = "EXIT_CLOSE";
    public const string Watch = "WATCH",
        Armed = "ARMED",
        Fading = "FADING",
        Reprieve = "REPRIEVE";
    public static readonly ExitDecision Holding = new(Hold, "", 0, "");
    public bool IsExit => Status != Hold;
}
