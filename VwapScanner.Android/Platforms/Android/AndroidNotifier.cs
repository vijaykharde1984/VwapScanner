using Android.App;
using Android.Content;
using Android.Media;
using AndroidX.Core.App;

namespace VwapScanner.Mobile;

/// <summary>Android notifications for new VWAP signals; tapping one opens the app.</summary>
public sealed class AndroidNotifier : INotifier
{
    private const string ChannelId = "signals";

    public async Task<bool> EnsurePermissionAsync()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return true; // the permission exists from Android 13
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            return status == PermissionStatus.Granted;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The reminder. Android fixes sound and vibration per notification channel, so there is one channel for each combination
    /// (sound + vibration, sound only, vibration only, silent) and the notification is posted on the one that matches the settings.
    /// </summary>
    public void Remind(string title, string text, bool sound, bool vibrate)
    {
        try
        {
            var context = Android.App.Application.Context;
            var channelId = "reminder-" + (sound ? "sound" : "nosound") + "-" + (vibrate ? "vibrate" : "novibrate");
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var channel = new NotificationChannel(channelId, "Scan reminder (" + (sound ? "sound" : "silent") + (vibrate ? ", vibration" : "") + ")", NotificationImportance.High)
                { Description = "Daily reminder to start the VWAP scan" };
                if (sound)
                {
                    var attributes = new AudioAttributes.Builder();
                    attributes.SetUsage(AudioUsageKind.Notification);
                    attributes.SetContentType(AudioContentType.Sonification);
                    channel.SetSound(RingtoneManager.GetDefaultUri(RingtoneType.Notification), attributes.Build());
                }
                else channel.SetSound(null, null);
                channel.EnableVibration(vibrate);
                if (vibrate) channel.SetVibrationPattern([0L, 500L, 250L, 500L, 250L, 500L]);
                (context.GetSystemService(Context.NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            }
            var open = new Intent(context, typeof(MainActivity));
            open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var pending = PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = new NotificationCompat.Builder(context, channelId);
            builder.SetContentTitle(title);
            builder.SetContentText(text);
            builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(text));
            builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
            builder.SetPriority(NotificationCompat.PriorityHigh);
            builder.SetCategory(NotificationCompat.CategoryReminder);
            builder.SetAutoCancel(true);
            builder.SetContentIntent(pending);
            // Android 7 and older have no channels: sound and vibration are set on the notification itself.
            builder.SetDefaults((sound ? NotificationCompat.DefaultSound : 0) | (vibrate ? NotificationCompat.DefaultVibrate : 0));
            NotificationManagerCompat.From(context)?.Notify(3, builder.Build());
        }
        catch (Exception) { /* no notification permission: nothing to show */ }
    }

    public void Notify(int id, string title, string text)
    {
        try
        {
            var context = Android.App.Application.Context;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                var channel = new NotificationChannel(ChannelId, "VWAP signals", NotificationImportance.High) { Description = "New VWAP scan signals" };
                (context.GetSystemService(Context.NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            }
            var open = new Intent(context, typeof(MainActivity));
            open.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
            var pending = PendingIntent.GetActivity(context, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            var builder = new NotificationCompat.Builder(context, ChannelId);
            builder.SetContentTitle(title);
            builder.SetContentText(text);
            builder.SetStyle(new NotificationCompat.BigTextStyle().BigText(text));
            builder.SetSmallIcon(Android.Resource.Drawable.IcDialogInfo);
            builder.SetPriority(NotificationCompat.PriorityHigh);
            builder.SetAutoCancel(true);
            builder.SetContentIntent(pending);
            var notification = builder.Build();
            NotificationManagerCompat.From(context)?.Notify(id, notification);
        }
        catch (Exception) { /* no permission or no notification service: the scan result is still shown in the app */ }
    }
}
