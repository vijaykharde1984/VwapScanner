using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace VwapScanner.Mobile;

/// <summary>
/// Foreground service that exists only to keep the app process (and the auto-refresh loop inside it) alive while the screen is off or another app is open.
/// It does no work itself. Android limits "data sync" foreground services to about 6 hours a day.
/// </summary>
[Service(
    Name = "com.vijaykharde.vwapscanner.KeepAliveService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeDataSync
)]
public class KeepAliveService : Service
{
    public const string ChannelId = "keepalive",
        ExtraText = "text",
        ActionStop = "com.vijaykharde.vwapscanner.STOP",
        ActionQuit = "com.vijaykharde.vwapscanner.QUIT",
        ActionAutoResume = "com.vijaykharde.vwapscanner.AUTORESUME";

    /// <summary>True from the moment the app asks for the service until it asks it to end (or the service ends itself).</summary>
    public static volatile bool Requested;
    private const int NotificationId = 1001;
    private PowerManager.WakeLock? wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId
    )
    {
        var text = intent?.GetStringExtra(ExtraText) ?? "Scanning for VWAP signals";
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Background scanning",
                NotificationImportance.Low
            )
            {
                Description = "Shown while auto-refresh is running",
            };
            (
                GetSystemService(NotificationService) as NotificationManager
            )?.CreateNotificationChannel(channel);
        }
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("VWAP Scanner");
        builder.SetContentText(text);
        builder.SetSmallIcon(Android.Resource.Drawable.IcMenuRecentHistory);
        builder.SetOngoing(true);
        builder.SetContentIntent(
            PendingIntent.GetActivity(
                this,
                0,
                open,
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
            )
        );
        var stop = new Intent(this, typeof(KeepAliveService));
        stop.SetAction(ActionStop);
        const PendingIntentFlags stopFlags =
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;
        builder.AddAction(
            Android.Resource.Drawable.IcMenuCloseClearCancel,
            "Stop auto-refresh",
            OperatingSystem.IsAndroidVersionAtLeast(26)
                ? PendingIntent.GetForegroundService(this, 1, stop, stopFlags)
                : PendingIntent.GetService(this, 1, stop, stopFlags)
        );
        var notification = builder.Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);

        if (wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "VwapScanner:autorefresh");
            wakeLock?.Acquire(7 * 60 * 60 * 1000L); // safety timeout; released earlier when auto-refresh stops
        }

        if (intent?.Action == ActionQuit)
        {
            // Asked by the app (auto-refresh ended). Goes through OnStartCommand so startForeground() has already been called above;
            // a plain StopService() right after StartForegroundService() crashes the app.
            Requested = false;
            StopForeground(StopForegroundFlags.Remove);
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }
        Requested = true;
        var coordinator = IPlatformApplication.Current?.Services.GetService<ScanCoordinator>();
        if (intent?.Action == ActionStop)
        {
            coordinator?.StopAuto(); // also clears the "resume" flag
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent is null || intent.Action == ActionAutoResume)
        {
            // Android restarted the service after it stopped the app's process, or the 09:10 alarm started it: pick auto-refresh up again if it was on.
            _ = Task.Run(async () =>
            {
                try
                {
                    if (
                        coordinator is null
                        || !await coordinator.ResumeIfEnabledAsync(interactive: false)
                    )
                    {
                        StopForeground(StopForegroundFlags.Remove);
                        StopSelf();
                    }
                }
                catch (Exception)
                {
                    StopSelf();
                }
            });
        }
        return StartCommandResult.Sticky;
    }

    /// <summary>Android 15 ends a data-sync foreground service after about 6 hours and requires it to stop at once.</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        StopSelf();
        base.OnTimeout(startId, fgsType);
    }

    public override void OnDestroy()
    {
        try
        {
            if (wakeLock is { IsHeld: true })
                wakeLock.Release();
        }
        catch (Exception) { }
        wakeLock = null;
        base.OnDestroy();
    }
}

public sealed class AndroidKeepAlive : IKeepAlive
{
    public void Start(string text)
    {
        try
        {
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(KeepAliveService));
            intent.PutExtra(KeepAliveService.ExtraText, text);
            KeepAliveService.Requested = true;
            ContextCompat.StartForegroundService(context, intent);
        }
        catch (Exception)
        { /* auto-refresh still works while the app is on screen */
        }
    }

    private const int AlarmRequestCode = 78;

    public void ScheduleStart(DateTimeOffset at)
    {
        try
        {
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Context.AlarmService) is not AlarmManager alarms)
                return;
            var pending = StartAlarmIntent(context);
            if (pending is null)
                return;
            alarms.Cancel(pending);
            var millis = at.ToUnixTimeMilliseconds();
            if (!OperatingSystem.IsAndroidVersionAtLeast(31) || alarms.CanScheduleExactAlarms())
                alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, millis, pending);
            else
                alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, millis, pending);
            AppLog.Info("Service", $"Alarm set to start the background service at {at:HH:mm} IST.");
        }
        catch (Exception e)
        {
            AppLog.Error("Service", e, "Could not set the 09:10 alarm");
        }
    }

    public void CancelScheduledStart()
    {
        try
        {
            var context = Android.App.Application.Context;
            if (
                context.GetSystemService(Context.AlarmService) is AlarmManager alarms
                && StartAlarmIntent(context) is { } pending
            )
                alarms.Cancel(pending);
        }
        catch (Exception) { }
    }

    private static PendingIntent? StartAlarmIntent(Context context)
    {
        var intent = new Intent(context, typeof(ServiceStartReceiver));
        intent.SetAction(KeepAliveService.ActionAutoResume);
        return PendingIntent.GetBroadcast(
            context,
            AlarmRequestCode,
            intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
        );
    }

    public void Stop()
    {
        try
        {
            if (!KeepAliveService.Requested)
                return;
            KeepAliveService.Requested = false;
            var context = Android.App.Application.Context;
            var intent = new Intent(context, typeof(KeepAliveService));
            intent.SetAction(KeepAliveService.ActionQuit);
            ContextCompat.StartForegroundService(context, intent);
        }
        catch (Exception) { }
    }
}

/// <summary>Fires at 09:10 IST when auto-refresh was switched on earlier: starts the background service, which resumes auto-refresh (also in a freshly started app process).</summary>
[BroadcastReceiver(
    Name = "com.vijaykharde.vwapscanner.ServiceStartReceiver",
    Enabled = true,
    Exported = false
)]
public class ServiceStartReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null)
            return;
        try
        {
            var start = new Intent(context, typeof(KeepAliveService));
            start.SetAction(KeepAliveService.ActionAutoResume);
            KeepAliveService.Requested = true;
            ContextCompat.StartForegroundService(context, start);
            AppLog.Info("Service", "The 09:10 alarm started the background service.");
        }
        catch (Exception e)
        {
            // Android refused to start a service from the background (no "Alarms & reminders" permission): ask the user to open the app instead.
            KeepAliveService.Requested = false;
            AppLog.Error("Service", e, "The 09:10 alarm could not start the service");
            try
            {
                IPlatformApplication
                    .Current?.Services.GetService<INotifier>()
                    ?.Remind(
                        "Open VWAP Scanner",
                        "Auto-refresh could not start by itself. Open the app to continue.",
                        true,
                        true
                    );
            }
            catch (Exception) { }
        }
    }
}
