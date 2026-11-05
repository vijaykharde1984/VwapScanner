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
[Service(Name = "com.vijaykharde.vwapscanner.KeepAliveService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public class KeepAliveService : Service
{
    public const string ChannelId = "keepalive", ExtraText = "text", ActionStop = "com.vijaykharde.vwapscanner.STOP";
    private const int NotificationId = 1001;
    private PowerManager.WakeLock? wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var text = intent?.GetStringExtra(ExtraText) ?? "Scanning for VWAP signals";
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(ChannelId, "Background scanning", NotificationImportance.Low) { Description = "Shown while auto-refresh is running" };
            (GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
        }
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("VWAP Scanner");
        builder.SetContentText(text);
        builder.SetSmallIcon(Android.Resource.Drawable.IcMenuRecentHistory);
        builder.SetOngoing(true);
        builder.SetContentIntent(PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent));
        var stop = new Intent(this, typeof(KeepAliveService));
        stop.SetAction(ActionStop);
        const PendingIntentFlags stopFlags = PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;
        builder.AddAction(Android.Resource.Drawable.IcMenuCloseClearCancel, "Stop auto-refresh",
            OperatingSystem.IsAndroidVersionAtLeast(26) ? PendingIntent.GetForegroundService(this, 1, stop, stopFlags) : PendingIntent.GetService(this, 1, stop, stopFlags));
        var notification = builder.Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else StartForeground(NotificationId, notification);

        if (wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "VwapScanner:autorefresh");
            wakeLock?.Acquire(7 * 60 * 60 * 1000L); // safety timeout; released earlier when auto-refresh stops
        }

        var coordinator = IPlatformApplication.Current?.Services.GetService<ScanCoordinator>();
        if (intent?.Action == ActionStop)
        {
            coordinator?.StopAuto(); // also clears the "resume" flag
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        if (intent is null)
        {
            // Android restarted the service after it stopped the app's process: pick auto-refresh up again if it was on.
            _ = Task.Run(async () =>
            {
                try { if (coordinator is null || !await coordinator.ResumeIfEnabledAsync(interactive: false)) { StopForeground(StopForegroundFlags.Remove); StopSelf(); } }
                catch (Exception) { StopSelf(); }
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
        try { if (wakeLock is { IsHeld: true }) wakeLock.Release(); } catch (Exception) { }
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
            ContextCompat.StartForegroundService(context, intent);
        }
        catch (Exception) { /* auto-refresh still works while the app is on screen */ }
    }

    public void Stop()
    {
        try
        {
            var context = Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(KeepAliveService)));
        }
        catch (Exception) { }
    }
}
