using System.Globalization;

namespace VwapScanner;

public enum SettingKind { Text, Integer, Decimal, Bool, Time, ShortTime, Date, Choice }

/// <summary>
/// One setting the user can change in the app. Keys are the same configuration paths as in the web API's appsettings.json, so the values go straight
/// into the IConfiguration that ApiState reads on every scan.
/// </summary>
public sealed record SettingDef(string Key, string Label, SettingKind Kind, string Section, string Hint = "",
    decimal? Min = null, decimal? Max = null, string[]? Options = null)
{
    public static readonly IReadOnlyList<SettingDef> All =
    [
        new("Scan:Universe", "Stock list", SettingKind.Choice, "Scan", Options: Universe.Names),
        new("Scan:Lookback", "Lookback (candles)", SettingKind.Integer, "Scan", "Also report signals from the last N one-minute candles.", 1, 400),
        new("Scan:UseBenchmark", "NIFTY 50 downtrend filter", SettingKind.Bool, "Scan", "Skip signals while NIFTY 50 is in a downtrend."),
        new("Scan:Date", "Replay date", SettingKind.Date, "Scan", "yyyy-MM-dd to replay a past trading day; empty = today."),
        new("Scan:At", "Replay time", SettingKind.ShortTime, "Scan", "HH:mm to evaluate as of that time; empty = now."),

        new("Scan:Strategy:MinStretchAtr", "Min stretch below VWAP (ATR)", SettingKind.Decimal, "Strategy", Min: 0, Max: 20),
        new("Scan:Strategy:MinRelativeVolume", "Min relative volume", SettingKind.Decimal, "Strategy", Min: 0, Max: 100),
        new("Scan:Strategy:MinBarTurnover", "Min bar turnover (INR)", SettingKind.Decimal, "Strategy", Min: 0, Max: 1000000000),
        new("Scan:Strategy:MinTargetBps", "Min target (bps)", SettingKind.Decimal, "Strategy", Min: 0, Max: 1000),
        new("Scan:Strategy:StopAtrMultiple", "Stop (ATR multiple)", SettingKind.Decimal, "Strategy", Min: 0.1m, Max: 20),
        new("Scan:Strategy:RewardRiskRatio", "Reward : risk", SettingKind.Decimal, "Strategy", Min: 0.1m, Max: 20),
        new("Scan:Strategy:EntryStart", "First entry time", SettingKind.Time, "Strategy", "HH:mm or HH:mm:ss"),
        new("Scan:Strategy:LastEntry", "Last entry time", SettingKind.Time, "Strategy", "HH:mm or HH:mm:ss"),

        new("Portfolio:Capital", "Capital (INR)", SettingKind.Decimal, "Portfolio", Min: 1, Max: 1000000000),
        new("Portfolio:RiskPerTradePercent", "Risk per trade (%)", SettingKind.Decimal, "Portfolio", Min: 0.01m, Max: 100),
        new("Portfolio:MaxPortfolioStopRiskPercent", "Max portfolio risk at stops (%)", SettingKind.Decimal, "Portfolio", Min: 0.01m, Max: 100),
        new("Portfolio:MaxAssetAllocationPercent", "Max allocation per stock (%)", SettingKind.Decimal, "Portfolio", Min: 0.01m, Max: 100),
        new("Portfolio:MaxPositions", "Max positions", SettingKind.Integer, "Portfolio", Min: 1, Max: 50),
        new("Portfolio:MaxParticipationPercent", "Max share of bar volume (%)", SettingKind.Decimal, "Portfolio", Min: 0.01m, Max: 100),
        new("Portfolio:SlippageBpsPerSide", "Slippage per side (bps)", SettingKind.Decimal, "Portfolio", Min: 0, Max: 1000),

        new("App:AutoStart", "Start auto-refresh automatically", SettingKind.Bool, "Auto-refresh",
            "Starts by itself when you open the app, signed in to Zerodha, on market days until 15:30 IST. Switching it off on the main screen (or Stop in the notification) keeps it off for the rest of that day."),
        new("App:ReminderTime", "Reminder time (IST)", SettingKind.ShortTime, "Reminder",
            "HH:mm, e.g. 09:10. A notification on weekdays reminding you to start the scan; skipped when auto-refresh is already on. Empty = no reminder. For an exact time on Android 12+, allow \"Alarms & reminders\" for this app in Android settings."),
        new("App:ReminderSound", "Reminder sound", SettingKind.Bool, "Reminder"),
        new("App:ReminderVibrate", "Reminder vibration", SettingKind.Bool, "Reminder"),
        new("App:SaveCsvAtClose", "Save CSV files at market close", SettingKind.Bool, "Auto-refresh",
            "After the closing scan (15:30 IST) the signals and portfolio CSV files of the day are saved to Download/VwapScanner. If the app was not running then, they are saved the next time you open it that day."),
        new("App:AutoRefreshMinutes", "Auto-refresh every (minutes)", SettingKind.Integer, "Auto-refresh",
            "Used by the Auto-refresh switch on the main screen. The lookback is raised automatically so no candle between two scans is missed.", 1, 120),
        new("App:Notify", "Notify on new signals", SettingKind.Bool, "Auto-refresh", "Android notification when an auto-refresh finds a signal not seen before."),
    ];

    /// <summary>Checks and normalizes what the user typed; on failure <paramref name="error"/> says what is expected.</summary>
    public bool TryNormalize(string? input, out string value, out string error)
    {
        var text = (input ?? "").Trim();
        var inv = CultureInfo.InvariantCulture;
        value = text; error = "";
        switch (Kind)
        {
            case SettingKind.Integer:
                if (int.TryParse(text, NumberStyles.Integer, inv, out var i) && (Min is not { } lo || i >= lo) && (Max is not { } hi || i <= hi))
                { value = i.ToString(inv); return true; }
                error = $"{Label}: enter a whole number" + Range();
                return false;
            case SettingKind.Decimal:
                if (decimal.TryParse(text, NumberStyles.Number, inv, out var d) && d >= (Min ?? 0m) && (Max is not { } top || d <= top))
                { value = d.ToString(inv); return true; }
                error = $"{Label}: enter a number" + Range() + " (use a dot for decimals)";
                return false;
            case SettingKind.Bool:
                value = text.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
                return true;
            case SettingKind.Time:
                if (TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], inv, DateTimeStyles.None, out var t))
                { value = t.ToString("HH:mm:ss", inv); return true; }
                error = $"{Label}: use HH:mm or HH:mm:ss";
                return false;
            case SettingKind.ShortTime:
                if (text.Length == 0) return true;
                if (TimeOnly.TryParseExact(text, "HH:mm", inv, DateTimeStyles.None, out var s))
                { value = s.ToString("HH:mm", inv); return true; }
                error = $"{Label}: use HH:mm or leave empty";
                return false;
            case SettingKind.Date:
                if (text.Length == 0) return true;
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", inv, DateTimeStyles.None, out var day))
                {
                    if (day > DateOnly.FromDateTime(DateTime.Today)) { error = $"{Label}: choose today or an earlier day"; return false; }
                    if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { error = $"{Label}: the market is closed on weekends"; return false; }
                    value = day.ToString("yyyy-MM-dd", inv); return true;
                }
                error = $"{Label}: use yyyy-MM-dd or leave empty";
                return false;
            case SettingKind.Choice:
                if (Options?.FirstOrDefault(o => o.Equals(text, StringComparison.OrdinalIgnoreCase)) is { } match) { value = match; return true; }
                error = $"{Label}: choose one of {string.Join(", ", Options ?? [])}";
                return false;
            default:
                return true;
        }
    }

    private string Range() => (Min, Max) switch
    {
        ({ } a, { } b) => $" from {a:0.##} to {b:0.##}",
        ({ } a, null) when a > 0 => $" of at least {a:0.##}",
        _ => ""
    };
}
