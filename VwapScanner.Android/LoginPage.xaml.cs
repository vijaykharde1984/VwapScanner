namespace VwapScanner.Mobile;

/// <summary>
/// Zerodha login inside the app. The web API receives Zerodha's redirect on http://127.0.0.1:9079/; here the WebView watches for that same redirect URL
/// (so the Kite app's registered Redirect URL does not change), cancels the navigation and finishes the login with the request_token in it.
/// The outcome is always shown: a success message when the session is connected, an error message otherwise.
/// </summary>
public partial class LoginPage : ContentPage
{
    private readonly ApiState state;
    private readonly AppConfig config;
    private bool handled;

    public LoginPage(ApiState state, AppConfig config)
    {
        InitializeComponent();
        this.state = state;
        this.config = config;
    }

    /// <summary>Loads Zerodha's login page; throws ZerodhaException when the API key is missing.</summary>
    public void Start() => Web.Source = new UrlWebViewSource { Url = state.LoginUrl() };

    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!e.Url.StartsWith(config.RedirectUrl, StringComparison.OrdinalIgnoreCase)) return;
        e.Cancel = true; // the phone never opens 127.0.0.1; only the request_token in the address is needed
        if (handled) return;
        handled = true;

        string? error = null;
        try
        {
            await state.CompleteLoginAsync(new Uri(e.Url).Query, CancellationToken.None);
        }
        catch (ZerodhaException ex) { error = ex.Message; }
        catch (UriFormatException) { error = "Zerodha returned an unreadable redirect address. Please log in again."; }
        catch (OperationCanceledException) { error = "The login was cancelled."; }
        catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }

        if (error is null && state.Connected)
            await DisplayAlertAsync("Zerodha login", "Login successful. You are connected to Zerodha until 06:00 IST tomorrow.", "OK");
        else
            await DisplayAlertAsync("Zerodha login failed", error ?? "Zerodha did not return a session. Please log in again.", "OK");
        await Navigation.PopAsync();
    }

    /// <summary>The login page itself could not be loaded (no internet, timeout...).</summary>
    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (handled || e.Result == WebNavigationResult.Success) return;
        handled = true;
        await DisplayAlertAsync("Zerodha login failed",
            $"Could not open the Zerodha login page ({e.Result}). Check the internet connection (and any VPN), then try again.", "OK");
        await Navigation.PopAsync();
    }
}
