namespace VwapScanner.Mobile;

/// <summary>
/// Zerodha login inside the app. The web API receives Zerodha's redirect on http://127.0.0.1:9079/; here the WebView watches for that same redirect URL
/// (so the Kite app's registered Redirect URL does not change), cancels the navigation and finishes the login with the request_token in it.
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
        e.Cancel = true;
        if (handled) return;
        handled = true;
        try
        {
            await state.CompleteLoginAsync(new Uri(e.Url).Query, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ZerodhaException or UriFormatException)
        {
            await DisplayAlertAsync("Zerodha login", ex.Message, "OK");
        }
        await Navigation.PopAsync();
    }
}
