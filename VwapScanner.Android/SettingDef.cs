using System.Globalization;

namespace VwapScanner;

public enum SettingKind
{
    Text,
    Integer,
    Decimal,
    Bool,
    Time,
    ShortTime,
    Date,
    Choice,
}

/// <summary>
/// One setting the user can change in the app. Keys are the same configuration paths as in the web API's appsettings.json, so the values go straight
/// into the IConfiguration that ApiState reads on every scan.
/// </summary>
public sealed record SettingDef(
    string Key,
    string Label,
    SettingKind Kind,
    string Section,
    string Hint = "",
    decimal? Min = null,
    decimal? Max = null,
    string[]? Options = null
)
{
    public static readonly IReadOnlyList<SettingDef> All =
    [
        new("Scan:Universe", "Stock list", SettingKind.Choice, "Scan", Options: Universe.Names),
        new(
            "Scan:StrategyName",
            "Strategy",
            SettingKind.Choice,
            "Scan",
            "ScalpVwap buys a turn-up stretched below VWAP. VwapPullback buys the first confirmed bounce off VWAP in an uptrend. Each has its own thresholds below.",
            Options: StrategyCatalog.Names
        ),
        new(
            "Scan:Lookback",
            "Lookback (candles)",
            SettingKind.Integer,
            "Scan",
            "Also report signals from the last N one-minute candles.",
            1,
            400
        ),
        new(
            "Scan:UseBenchmark",
            "NIFTY 50 downtrend filter",
            SettingKind.Bool,
            "Scan",
            "Skip signals while NIFTY 50 is in a downtrend."
        ),
        new(
            "Scan:Date",
            "Replay date",
            SettingKind.Date,
            "Scan",
            "yyyy-MM-dd to replay a past trading day; empty = today."
        ),
        new(
            "Scan:At",
            "Replay time",
            SettingKind.ShortTime,
            "Scan",
            "HH:mm to evaluate as of that time; empty = now."
        ),
        new(
            "Scan:Strategy:MinStretchAtr",
            "Min stretch below VWAP (ATR)",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0,
            Max: 20
        ),
        new(
            "Scan:Strategy:MinRelativeVolume",
            "Min relative volume",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0,
            Max: 100
        ),
        new(
            "Scan:Strategy:MinBarTurnover",
            "Min bar turnover (INR)",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0,
            Max: 1000000000
        ),
        new(
            "Scan:Strategy:MinTargetBps",
            "Min target (bps)",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0,
            Max: 1000
        ),
        new(
            "Scan:Strategy:StopAtrMultiple",
            "Stop (ATR multiple)",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0.1m,
            Max: 20
        ),
        new(
            "Scan:Strategy:RewardRiskRatio",
            "Reward : risk",
            SettingKind.Decimal,
            "ScalpVwap strategy",
            Min: 0.1m,
            Max: 20
        ),
        new(
            "Scan:Strategy:EntryStart",
            "First entry time",
            SettingKind.Time,
            "ScalpVwap strategy",
            "HH:mm or HH:mm:ss"
        ),
        new(
            "Scan:Strategy:LastEntry",
            "Last entry time",
            SettingKind.Time,
            "ScalpVwap strategy",
            "HH:mm or HH:mm:ss"
        ),
        new(
            "Scan:Pullback:TrendBars",
            "Trend window (candles)",
            SettingKind.Integer,
            "Pullback strategy",
            "How many one-minute candles are used to judge the uptrend.",
            Min: 10,
            Max: 120
        ),
        new(
            "Scan:Pullback:MinAboveVwapPercent",
            "Min % of closes above VWAP",
            SettingKind.Decimal,
            "Pullback strategy",
            "Share of the trend window that must have closed above VWAP.",
            Min: 1,
            Max: 100
        ),
        new(
            "Scan:Pullback:MinVwapSlopeAtr",
            "Min VWAP rise (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "VWAP must have risen at least this many ATRs over the trend window.",
            Min: 0,
            Max: 20
        ),
        new(
            "Scan:Pullback:SwingBars",
            "Swing size (candles)",
            SettingKind.Integer,
            "Pullback strategy",
            "Candles on each side that make a swing high or low (higher highs and higher lows are required).",
            Min: 1,
            Max: 10
        ),
        new(
            "Scan:Pullback:PullbackBars",
            "Pullback window (candles)",
            SettingKind.Integer,
            "Pullback strategy",
            "The touch of VWAP must be within this many candles. Also the gap between two signals in one stock.",
            Min: 2,
            Max: 60
        ),
        new(
            "Scan:Pullback:TouchAtr",
            "VWAP touch distance (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "A low this far above VWAP, or lower, counts as a touch.",
            Min: 0,
            Max: 5
        ),
        new(
            "Scan:Pullback:MaxBreakAtr",
            "Max dip below VWAP (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "A low further below VWAP than this is a breakdown, not a pullback.",
            Min: 0,
            Max: 5
        ),
        new(
            "Scan:Pullback:StopBufferAtr",
            "Stop buffer (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "Stop = lower of pullback low and VWAP, minus this many ATRs.",
            Min: 0,
            Max: 5
        ),
        new(
            "Scan:Pullback:MinRiskAtr",
            "Min stop distance (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "Reject a stop closer than this.",
            Min: 0.1m,
            Max: 10
        ),
        new(
            "Scan:Pullback:MaxRiskAtr",
            "Max stop distance (ATR)",
            SettingKind.Decimal,
            "Pullback strategy",
            "Reject a stop further than this.",
            Min: 0.2m,
            Max: 20
        ),
        new(
            "Scan:Pullback:RewardRiskRatio",
            "Reward : risk",
            SettingKind.Decimal,
            "Pullback strategy",
            "Target distance as a multiple of the stop distance. The guide's minimum is 2.",
            Min: 0.1m,
            Max: 20
        ),
        new(
            "Scan:Pullback:MinRoomRisk",
            "Min room to day high (x risk)",
            SettingKind.Decimal,
            "Pullback strategy",
            "The day's high must be at least this many stop distances above the entry.",
            Min: 0,
            Max: 20
        ),
        new(
            "Scan:Pullback:MinTargetBps",
            "Min target (bps)",
            SettingKind.Decimal,
            "Pullback strategy",
            Min: 0,
            Max: 1000
        ),
        new(
            "Scan:Pullback:MinRelativeVolume",
            "Min relative volume",
            SettingKind.Decimal,
            "Pullback strategy",
            Min: 0,
            Max: 100
        ),
        new(
            "Scan:Pullback:MinBarTurnover",
            "Min bar turnover (INR)",
            SettingKind.Decimal,
            "Pullback strategy",
            Min: 0,
            Max: 1000000000
        ),
        new(
            "Scan:Pullback:EntryStart",
            "First entry time",
            SettingKind.Time,
            "Pullback strategy",
            "HH:mm or HH:mm:ss"
        ),
        new(
            "Scan:Pullback:LastEntry",
            "Last entry time",
            SettingKind.Time,
            "Pullback strategy",
            "HH:mm or HH:mm:ss"
        ),
        new(
            "Portfolio:Capital",
            "Capital (INR)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 1,
            Max: 1000000000
        ),
        new(
            "Portfolio:RiskPerTradePercent",
            "Risk per trade (%)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 0.01m,
            Max: 100
        ),
        new(
            "Portfolio:MaxPortfolioStopRiskPercent",
            "Max portfolio risk at stops (%)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 0.01m,
            Max: 100
        ),
        new(
            "Portfolio:MaxAssetAllocationPercent",
            "Max allocation per stock (%)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 0.01m,
            Max: 100
        ),
        new(
            "Portfolio:MaxPositions",
            "Max positions",
            SettingKind.Integer,
            "Portfolio",
            Min: 1,
            Max: 50
        ),
        new(
            "Portfolio:MaxParticipationPercent",
            "Max share of bar volume (%)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 0.01m,
            Max: 100
        ),
        new(
            "Portfolio:SlippageBpsPerSide",
            "Slippage per side (bps)",
            SettingKind.Decimal,
            "Portfolio",
            Min: 0,
            Max: 1000
        ),
        new(
            "App:AutoStart",
            "Start auto-refresh automatically",
            SettingKind.Bool,
            "Auto-refresh",
            "Starts by itself when you open the app, signed in to Zerodha, on market days until 15:30 IST. Switching it off on the main screen (or Stop in the notification) keeps it off for the rest of that day."
        ),
        new(
            "App:ReminderTime",
            "Reminder time (IST)",
            SettingKind.ShortTime,
            "Reminder",
            "HH:mm, e.g. 09:10. A notification on weekdays reminding you to start the scan; skipped when auto-refresh is already on. Empty = no reminder. For an exact time on Android 12+, allow \"Alarms & reminders\" for this app in Android settings."
        ),
        new("App:ReminderSound", "Reminder sound", SettingKind.Bool, "Reminder"),
        new("App:ReminderVibrate", "Reminder vibration", SettingKind.Bool, "Reminder"),
        new(
            "App:SaveCsvAtClose",
            "Save CSV files at market close",
            SettingKind.Bool,
            "Auto-refresh",
            "After the closing scan (15:30 IST) the signals and portfolio CSV files of the day are saved to Download/VwapScanner. If the app was not running then, they are saved the next time you open it that day."
        ),
        new(
            "App:AutoRefreshMinutes",
            "Auto-refresh every (minutes)",
            SettingKind.Integer,
            "Auto-refresh",
            "Used by the Auto-refresh switch on the main screen. The lookback is raised automatically so no candle between two scans is missed.",
            1,
            120
        ),
        new(
            "App:Notify",
            "Notify on new signals",
            SettingKind.Bool,
            "Auto-refresh",
            "Android notification when an auto-refresh finds a signal not seen before."
        ),
        new(
            "App:ExitMonitorEnabled",
            "Exit Monitor",
            SettingKind.Bool,
            "Exit Monitor",
            "A separate loop watches every portfolio row with live 1-minute candles and says when to sell: EXIT_TARGET, EXIT_STOP, EXIT_REVERSAL, EXIT_BREAKEVEN, or EXIT_CLOSE at the close-out time (grid and CSV column exit_status). Off = those columns stay empty."
        ),
        new(
            "App:ExitCheckSeconds",
            "Check exits every (seconds)",
            SettingKind.Integer,
            "Exit Monitor",
            "Pause between two checks. Each check downloads one request per stock that is still held (Kite allows about 3 a second, shared with the scan).",
            15,
            300
        ),
        new(
            "App:ExitCloseTime",
            "Close-out time (IST)",
            SettingKind.ShortTime,
            "Exit Monitor",
            "HH:mm between 09:30 and 15:30 (default 15:15). Positions still held then get EXIT_CLOSE: sell at the market. Brokers usually square off intraday positions around 15:15-15:20."
        ),
        new(
            "App:GuardEnabled",
            "Reversal Guard",
            SettingKind.Bool,
            "Reversal Guard",
            "Protects a position that rose toward the target and is now fading, so a gain does not turn into a loss. Off = only target, stop and close-out."
        ),
        new(
            "App:GuardArmPercent",
            "Arm after (% of the way to target)",
            SettingKind.Decimal,
            "Reversal Guard",
            "The guard arms when the highest price since the buy has covered this share of the distance from the buy price to the target. Once armed, the stop moves up to the buy price (plus the buffer below).",
            10,
            100
        ),
        new(
            "App:GuardGivebackPercent",
            "Sell when this % of the peak gain is given back",
            SettingKind.Decimal,
            "Reversal Guard",
            "Example: bought 400, peak 408, 40% of the 8 gain = 3.2, so a close at 404.8 or lower sells (EXIT_REVERSAL).",
            10,
            100
        ),
        new(
            "App:GuardFadeCandles",
            "Lower closes in a row (below VWAP)",
            SettingKind.Integer,
            "Reversal Guard",
            "Sell when the price closes lower this many times in a row while below the day's VWAP. Stop Reprieve uses it too: a wait is cancelled after this many lower closes in a row.",
            2,
            10
        ),
        new(
            "App:GuardBreakevenBps",
            "Breakeven buffer (bps)",
            SettingKind.Decimal,
            "Reversal Guard",
            "Added to the buy price for the breakeven level, to cover brokerage and slippage. 5 bps = 0.05%: 400 becomes 400.20.",
            0,
            100
        ),
        new(
            "App:StopReprieveEnabled",
            "Stop Reprieve",
            SettingKind.Bool,
            "Stop Reprieve",
            "When a dip touches the stop but the price is already back above it and the VWAP leaves room to progress (at least one buy-to-stop distance), wait instead of selling. The stop stays the only stop loss: while waiting, a completed 1-minute close at or below it sells. Off = sell at the stop."
        ),
        new(
            "App:StopReprieveMinutes",
            "Wait up to (minutes)",
            SettingKind.Integer,
            "Stop Reprieve",
            "How long to wait after the dip. The wait is cancelled early (sold) after the number of lower closes in a row set under Reversal Guard, or when by the half-way point the price is not above where it was when the wait began. Only one reprieve per position.",
            1,
            60
        ),
    ];

    /// <summary>Checks and normalizes what the user typed; on failure <paramref name="error"/> says what is expected.</summary>
    public bool TryNormalize(string? input, out string value, out string error)
    {
        var text = (input ?? "").Trim();
        var inv = CultureInfo.InvariantCulture;
        value = text;
        error = "";
        switch (Kind)
        {
            case SettingKind.Integer:
                if (
                    int.TryParse(text, NumberStyles.Integer, inv, out var i)
                    && (Min is not { } lo || i >= lo)
                    && (Max is not { } hi || i <= hi)
                )
                {
                    value = i.ToString(inv);
                    return true;
                }
                error = $"{Label}: enter a whole number" + Range();
                return false;
            case SettingKind.Decimal:
                if (
                    decimal.TryParse(text, NumberStyles.Number, inv, out var d)
                    && d >= (Min ?? 0m)
                    && (Max is not { } top || d <= top)
                )
                {
                    value = d.ToString(inv);
                    return true;
                }
                error = $"{Label}: enter a number" + Range() + " (use a dot for decimals)";
                return false;
            case SettingKind.Bool:
                value = text.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                return true;
            case SettingKind.Time:
                if (
                    TimeOnly.TryParseExact(
                        text,
                        ["HH:mm", "HH:mm:ss"],
                        inv,
                        DateTimeStyles.None,
                        out var t
                    )
                )
                {
                    value = t.ToString("HH:mm:ss", inv);
                    return true;
                }
                error = $"{Label}: use HH:mm or HH:mm:ss";
                return false;
            case SettingKind.ShortTime:
                if (text.Length == 0)
                    return true;
                if (TimeOnly.TryParseExact(text, "HH:mm", inv, DateTimeStyles.None, out var s))
                {
                    value = s.ToString("HH:mm", inv);
                    return true;
                }
                error = $"{Label}: use HH:mm or leave empty";
                return false;
            case SettingKind.Date:
                if (text.Length == 0)
                    return true;
                if (
                    DateOnly.TryParseExact(
                        text,
                        "yyyy-MM-dd",
                        inv,
                        DateTimeStyles.None,
                        out var day
                    )
                )
                {
                    if (day > DateOnly.FromDateTime(DateTime.Today))
                    {
                        error = $"{Label}: choose today or an earlier day";
                        return false;
                    }
                    if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                    {
                        error = $"{Label}: the market is closed on weekends";
                        return false;
                    }
                    value = day.ToString("yyyy-MM-dd", inv);
                    return true;
                }
                error = $"{Label}: use yyyy-MM-dd or leave empty";
                return false;
            case SettingKind.Choice:
                if (
                    Options?.FirstOrDefault(o =>
                        o.Equals(text, StringComparison.OrdinalIgnoreCase)
                    ) is
                    { } match
                )
                {
                    value = match;
                    return true;
                }
                error = $"{Label}: choose one of {string.Join(", ", Options ?? [])}";
                return false;
            default:
                return true;
        }
    }

    private string Range() =>
        (Min, Max) switch
        {
            ({ } a, { } b) => $" from {a:0.##} to {b:0.##}",
            ({ } a, null) when a > 0 => $" of at least {a:0.##}",
            _ => "",
        };
}
