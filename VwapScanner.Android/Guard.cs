using System.Diagnostics;

namespace VwapScanner.Mobile;

/// <summary>
/// Every UI event handler is "async void": an exception that escapes one of them closes the whole app. All handlers run through here,
/// so an unexpected error becomes a message on the screen instead of a crash.
/// </summary>
public static class Guard
{
    public static async Task Run(Page page, string title, Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { /* cancelled on purpose */ }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VwapScanner] {title}: {ex}");
            AppLog.Error("Ui", ex, title);
            try { await page.DisplayAlertAsync(title, Describe(ex), "OK"); }
            catch (Exception) { /* the page is gone or no window is available: nothing more to do */ }
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        ZerodhaException z => z.Message,
        HttpRequestException or TimeoutException or System.Net.Sockets.SocketException => "Network problem - check the internet connection and try again. (" + ex.Message + ")",
        UnauthorizedAccessException => "The app is not allowed to do that (permission denied). " + ex.Message,
        IOException => "File problem: " + ex.Message,
        FeatureNotSupportedException => "This phone does not support that: " + ex.Message,
        _ => ex.GetType().Name + ": " + ex.Message,
    };
}
