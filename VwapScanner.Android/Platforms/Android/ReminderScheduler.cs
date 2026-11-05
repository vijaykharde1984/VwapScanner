using Android.App;
using Android.Content;

namespace VwapScanner.Mobile;

/// <summary>Schedules the daily reminder with AlarmManager: it fires even when the app is closed or the phone is idle.</summary>
public sealed class AndroidReminderScheduler(AppConfig config) : IReminderScheduler
{
    internal const string ActionReminder = "com.vijaykharde.vwapscanner.REMINDER";

    public void Schedule()
    {
        try
        {
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms) return;
            var intent = new Intent(context, typeof(ReminderReceiver));
            intent.SetAction(ActionReminder);
            var pending = PendingIntent.GetBroadcast(context, 77, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            if (pending is null) return;
            alarms.Cancel(pending);
            if (config.ReminderTime is not { } time) return; // no time set: no reminder
            var at = ReminderClock.Next(time, DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            // Exact alarms need the "Alarms & reminders" permission on Android 12+; without it the reminder still fires, a few minutes late at worst.
            if (!OperatingSystem.IsAndroidVersionAtLeast(31) || alarms.CanScheduleExactAlarms())
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);
            else
                alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, at, pending);
        }
        catch (Exception) { /* scheduling is best effort; it is retried whenever the app opens */ }
    }
}

/// <summary>Fires at the reminder time: shows the reminder unless auto-refresh is already running, then schedules the next one.</summary>
[BroadcastReceiver(Name = "com.vijaykharde.vwapscanner.ReminderReceiver", Enabled = true, Exported = false)]
public class ReminderReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        var services = IPlatformApplication.Current?.Services;
        try
        {
            var config = services?.GetService<AppConfig>();
            var notifier = services?.GetService<INotifier>();
            var coordinator = services?.GetService<ScanCoordinator>();
            if (config is not null && notifier is not null && !(coordinator?.IsAutoActiveToday ?? false))
                notifier.Remind("Time to start your VWAP scan", "Open the app to start auto-refresh and scan for signals.", config.ReminderSound, config.ReminderVibrate);
        }
        finally
        {
            services?.GetService<IReminderScheduler>()?.Schedule();
        }
    }
}

/// <summary>Alarms are cleared by a reboot or an app update, so the reminder is scheduled again after either.</summary>
[BroadcastReceiver(Name = "com.vijaykharde.vwapscanner.BootReceiver", Enabled = true, Exported = true)]
[IntentFilter(new[] { Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced })]
public class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent) =>
        IPlatformApplication.Current?.Services.GetService<IReminderScheduler>()?.Schedule();
}
