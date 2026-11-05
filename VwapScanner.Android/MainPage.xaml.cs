using System.Globalization;

namespace VwapScanner.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ApiState state;
    private readonly AppConfig config;
    private readonly ScanCoordinator coordinator;
    private readonly IServiceProvider services;
    private int restoredRevision = -1;
    private bool updatingSwitch;
    private RunResult? shown;

    public MainPage(ApiState state, AppConfig config, ScanCoordinator coordinator, IServiceProvider services)
    {
        InitializeComponent();
        this.state = state;
        this.config = config;
        this.coordinator = coordinator;
        this.services = services;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        coordinator.Changed += OnCoordinatorChanged;
        if (restoredRevision != config.Revision)
        {
            restoredRevision = config.Revision;
            StatusLabel.Text = "Checking the saved Zerodha session...";
            try
            {
                await config.LoadSecretsAsync();
                await Task.Run(() => state.RestoreAsync(CancellationToken.None));
            }
            catch (ZerodhaException e) { StatusLabel.Text = "Could not check the saved Zerodha token: " + e.Message; return; }
        }
        Refresh();
    }

    protected override void OnDisappearing()
    {
        coordinator.Changed -= OnCoordinatorChanged;
        base.OnDisappearing();
    }

    private void OnCoordinatorChanged() => MainThread.BeginInvokeOnMainThread(Refresh);

    /// <summary>Redraws everything from the connection state and the coordinator (which keeps scanning while this page is not shown).</summary>
    private void Refresh()
    {
        var connected = state.Connected;
        var busy = coordinator.Busy;
        LoginButton.IsVisible = !connected;
        LoginButton.IsEnabled = !busy;
        ScanButton.Text = busy ? "Cancel" : "Scan now";
        ScanButton.IsEnabled = connected;
        Busy.IsVisible = Busy.IsRunning = busy;
        AutoLabel.Text = $"Auto-refresh every {config.AutoRefreshMinutes} min until 15:30 IST";
        AutoSwitch.IsEnabled = connected || coordinator.AutoRunning;
        if (AutoSwitch.IsToggled != coordinator.AutoRunning)
        {
            updatingSwitch = true;
            AutoSwitch.IsToggled = coordinator.AutoRunning;
            updatingSwitch = false;
        }

        var help = !connected
            ? string.IsNullOrWhiteSpace(config.ApiKey)
                ? "Not connected. Open Settings and enter your Kite API key and secret, then log in."
                : "Not connected to Zerodha yet. Tap Zerodha login (sessions end at 06:00 IST)."
            : "";
        StatusLabel.Text = string.Join("\n", new[] { coordinator.Status, help }.Where(x => x.Length > 0).DefaultIfEmpty("Connected to Zerodha."));

        if (coordinator.Last is { } last && !ReferenceEquals(last, shown))
        {
            shown = last;
            Show(last);
        }
    }

    private async void OnSettings(object? sender, EventArgs e) =>
        await Navigation.PushAsync(services.GetRequiredService<SettingsPage>());

    private async void OnLogin(object? sender, EventArgs e)
    {
        try
        {
            await config.LoadSecretsAsync();
            var login = services.GetRequiredService<LoginPage>();
            login.Start(); // throws when the API key is not set
            await Navigation.PushAsync(login);
        }
        catch (ZerodhaException ex) { await DisplayAlertAsync("Zerodha login", ex.Message + "\nOpen Settings to enter the key.", "OK"); }
    }

    private async void OnScan(object? sender, EventArgs e)
    {
        if (coordinator.Busy) { coordinator.CancelScan(); return; } // second tap cancels the running scan
        await coordinator.ScanOnceAsync(auto: false);
    }

    private async void OnAutoToggled(object? sender, ToggledEventArgs e)
    {
        if (updatingSwitch) return;
        if (e.Value)
        {
            if (!await coordinator.StartAutoAsync()) Refresh();
        }
        else coordinator.StopAuto();
    }

    private async void OnShare(object? sender, EventArgs e)
    {
        if (coordinator.Last is not { } r) return;
        try
        {
            var stamp = $"{r.Scan.Date.Replace("-", "")}-{r.Scan.AsOf.Replace(":", "")}";
            var files = new List<ShareFile>();
            if (r.Scan.Signals.Count > 0) files.Add(Write($"vwap-signals-{stamp}.csv", Reporter.SignalsCsv(r.Scan.Signals)));
            if (r.Plan.Positions.Count > 0) files.Add(Write($"vwap-portfolio-{stamp}.csv", Reporter.PortfolioCsv(r.Plan)));
            if (files.Count == 0) { await DisplayAlertAsync("Share CSV", "No signals or positions to share.", "OK"); return; }
            await Share.Default.RequestAsync(new ShareMultipleFilesRequest { Title = $"VWAP scan {r.Scan.Date} {r.Scan.AsOf}", Files = files });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FeatureNotSupportedException)
        {
            await DisplayAlertAsync("Share CSV", ex.Message, "OK");
        }
    }

    private static ShareFile Write(string name, string csv)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        File.WriteAllText(path, csv);
        return new ShareFile(path, "text/csv");
    }

    private void Show(RunResult r)
    {
        var scan = r.Scan; var plan = r.Plan;
        var summary = $"{scan.Date} as of {scan.AsOf} IST - {scan.Signals.Count} signal(s) in {scan.Scanned} stocks ({r.Universe})" +
            (scan.Failed > 0 ? $", {scan.Failed} failed" : "") + (scan.BenchmarkFilter ? ", NIFTY 50 downtrend filter on." : ", benchmark filter off.");
        if (scan.Message is not null) summary += "\n" + scan.Message;
        if (scan.Diagnostics is { } d)
            summary += $"\nCandles checked {d.Evaluated} (not ready {d.NotReady}, NIFTY downtrend {d.BenchmarkBlocked}). Each test alone: below VWAP {d.BelowVwap}, turn-up {d.TurnUp}, " +
                $"rel. volume {d.RelativeVolumeOk}, turnover {d.TurnoverOk}, target clears costs {d.TargetClearsCosts}, entry window {d.InEntryWindow}.";
        SummaryLabel.Text = summary;

        SignalsHeader.IsVisible = true;
        ShareButton.IsVisible = scan.Signals.Count > 0 || plan.Positions.Count > 0;
        SignalsHeader.Text = scan.Signals.Count == 0 ? "Signals - none right now" : $"Signals ({scan.Signals.Count})";
        BindableLayout.SetItemsSource(SignalsList, scan.Signals.Select(s => new Row(
            $"{s.Symbol}   {s.BarTime:HH:mm}   score {F(s.Score)}",
            $"Close {F(s.Close)} | VWAP {F(s.Vwap)} | stretch {F(s.StretchAtr)} ATR | rel. vol {F(s.RelativeVolume)}\nStop {F(s.Stop)} | Target {F(s.Target)}",
            coordinator.NewKeys.Contains(ScanCoordinator.Key(s)))).ToList());

        PortfolioHeader.IsVisible = PortfolioSummary.IsVisible = true;
        PortfolioSummary.Text = $"Capital INR {plan.Capital:N0}, invested {plan.Invested:N0} ({(plan.Capital > 0 ? plan.Invested / plan.Capital * 100 : 0):0.0}%), cash {plan.Cash:N0}.\n" +
            $"Risk at stops INR {plan.TotalRiskAtStopInr:N0} of {plan.RiskBudgetInr:N0}; net gain if price returns to VWAP INR {plan.TotalNetGainAtVwapInr:N0}.";
        BindableLayout.SetItemsSource(PortfolioList, plan.Positions.Select(p => new Row(
            $"{p.Symbol}   x{p.Quantity}",
            $"Price {F(p.Price)} | notional INR {p.Notional:N0} ({p.WeightPercent:0.0}%)\nStop {F(p.Stop)} | risk INR {p.RiskAtStopInr:N0} | gain at VWAP INR {p.NetGainAtVwapInr:N0}")).ToList());
        NotesLabel.Text = string.Join("\n", plan.Notes);
    }

    private static string F(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);
}
