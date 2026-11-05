using Android.App;
using Android.Content;
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
