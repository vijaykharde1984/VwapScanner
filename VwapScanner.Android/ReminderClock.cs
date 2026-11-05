namespace VwapScanner;

/// <summary>When the daily "start your scan" reminder fires: the configured time (IST) on weekdays.</summary>
public static class ReminderClock
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    /// <summary>The first weekday occurrence of <paramref name="at"/> (IST) that is after <paramref name="now"/>.</summary>
    public static DateTimeOffset Next(TimeOnly at, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToOffset(Ist).DateTime);
        for (var i = 0; i < 8; i++)
        {
            var day = today.AddDays(i);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var candidate = new DateTimeOffset(day.ToDateTime(at), Ist);
            if (candidate > now) return candidate;
        }
        throw new InvalidOperationException("No weekday found.");
    }
}
