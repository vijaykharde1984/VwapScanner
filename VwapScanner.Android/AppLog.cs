using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VwapScanner;

/// <summary>
/// One log file per day (India time) in the app's private storage: logs/vwap-yyyyMMdd.log, kept for 30 days. It records Zerodha/Kite errors and the other
/// failures of the app, plus the main events (login, auto-refresh start/stop, CSV saves). Tokens, keys and secrets are removed from every line.
/// Logging itself never throws.
/// </summary>
public static partial class AppLog
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private const int KeepDays = 30,
        MaxLineChars = 4000;
    private static bool cleaned;

    public static string Folder => Path.Combine(FileSystem.AppDataDirectory, "logs");

    public static string TodayFile => Path.Combine(Folder, $"vwap-{Now:yyyyMMdd}.log");

    private static DateTimeOffset Now => DateTimeOffset.UtcNow.ToOffset(Ist);

    public static void Info(string area, string message) => Write("INFO ", area, message);

    public static void Warn(string area, string message) => Write("WARN ", area, message);

    /// <summary>Zerodha/Kite and configuration errors are business errors (type and message are enough); anything else is unexpected and gets its stack trace.</summary>
    public static void Error(string area, Exception ex, string? context = null)
    {
        var business = ex is ZerodhaException or ConfigurationException;
        var text = (context is null ? "" : context + ": ") + ex.GetType().Name + ": " + ex.Message;
        if (!business && ex.StackTrace is { } trace)
            text += Environment.NewLine + trace;
        if (ex.InnerException is { } inner)
            text +=
                Environment.NewLine + "  caused by " + inner.GetType().Name + ": " + inner.Message;
        Write("ERROR", area, text);
    }

    private static void Write(string level, string area, string message)
    {
        try
        {
            var text = Redact(message);
            if (text.Length > MaxLineChars)
                text = text[..MaxLineChars] + "...";
            var line =
                $"{Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} IST [{level}] [{area}] {text}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(TodayFile, line, Encoding.UTF8);
                if (!cleaned)
                {
                    cleaned = true;
                    DeleteOld();
                }
            }
        }
        catch (Exception)
        { /* a full disk or a missing folder must never break the app */
        }
    }

    private static void DeleteOld()
    {
        var limit = Now.Date.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(Folder, "vwap-*.log"))
            if (File.GetLastWriteTimeUtc(file) < limit.ToUniversalTime())
                File.Delete(file);
    }

    /// <summary>Removes values of tokens, keys and secrets (JSON or query string form) so they never reach the file.</summary>
    public static string Redact(string text) => SecretPattern().Replace(text, "${name}${sep}***");

    [GeneratedRegex(
        """(?<name>"?(?:access_token|public_token|refresh_token|enctoken|request_token|api_secret|api_key|checksum|secret)"?)(?<sep>\s*[:=]\s*"?)[^"&\s,}]+""",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SecretPattern();
}
