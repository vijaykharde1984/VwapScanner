using System.Globalization;

namespace VwapScanner.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ApiState state;
    private readonly AppConfig config;
    private readonly ScanCoordinator coordinator;
    private readonly IServiceProvider services;
    private readonly IFileSaver saver;
    private int restoredRevision = -1;
    private bool updatingSwitch;
    private RunResult? shown;

    // Same columns as vwap-signals-*.csv and vwap-portfolio-*.csv (the portfolio grid has a leading scan-time column).
    private static readonly string[] SignalHeaders =
        ["symbol", "instrument_id", "bar_time", "close", "vwap", "stretch_atr", "atr", "relative_volume", "score", "stop", "target"];
    private static readonly string[] PositionHeaders =
        ["scanned_at", "symbol", "instrument_id", "bar_time", "price", "stop", "target", "vwap_exit", "quantity", "notional", "weight_percent",
         "risk_at_stop_inr", "risk_share_percent", "net_gain_at_vwap_inr", "score"];
    private readonly ResultTable signalsTable = new(SignalHeaders, [110, 110, 200, 90, 90, 100, 80, 120, 80, 90, 90]);
    private readonly ResultTable portfolioTable = new(PositionHeaders, [90, 110, 110, 200, 90, 90, 90, 90, 80, 100, 120, 130, 140, 160, 80]);
    private int historyEpoch = -1, signalRows, positionRows;

    public MainPage(ApiState state, AppConfig config, ScanCoordinator coordinator, IFileSaver saver, IServiceProvider services)
    {
        InitializeComponent();
        this.state = state;
        this.config = config;
        this.coordinator = coordinator;
        this.services = services;
        this.saver = saver;
        SignalsGridHost.Content = signalsTable.View;
        PortfolioGridHost.Content = portfolioTable.View;
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
        SyncTables();
    }

    /// <summary>Appends the rows found since the last refresh to the two grids (nothing happens when a refresh found nothing new).</summary>
    private void SyncTables()
    {
        var chunk = coordinator.TakeHistory(historyEpoch, signalRows, positionRows);
        if (chunk.Reset) { signalsTable.Clear(); portfolioTable.Clear(); }
        historyEpoch = chunk.Epoch;
        if (chunk.Signals.Count > 0) signalsTable.Append(chunk.Signals.Select(SignalCells));
        if (chunk.Positions.Count > 0) portfolioTable.Append(chunk.Positions.Select(x => PositionCells(x.At, x.Position)));
        signalRows = chunk.SignalTotal;
        positionRows = chunk.PositionTotal;
        SignalsGridHeader.Text = $"Signals grid ({signalRows} row(s) today)";
        PortfolioGridHeader.Text = $"Portfolio grid ({positionRows} row(s) today)";
        ShareButton.IsVisible = signalRows > 0 || positionRows > 0;
        ExportSignalsButton.IsEnabled = signalRows > 0;
        ExportPortfolioButton.IsEnabled = positionRows > 0;
    }

    private static string N(decimal x) => x.ToString("0.####", CultureInfo.InvariantCulture);
    private static string T(DateTimeOffset x) => x.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string[] SignalCells(VwapSignal s) =>
        [s.Symbol, s.InstrumentId.ToString(CultureInfo.InvariantCulture), T(s.BarTime), N(s.Close), N(s.Vwap), N(s.StretchAtr), N(s.Atr), N(s.RelativeVolume), N(s.Score), N(s.Stop), N(s.Target)];

    private static string[] PositionCells(DateTimeOffset at, PortfolioPosition p) =>
        [at.ToString("HH:mm:ss", CultureInfo.InvariantCulture), p.Symbol, p.InstrumentId.ToString(CultureInfo.InvariantCulture), T(p.BarTime), N(p.Price), N(p.Stop), N(p.Target), N(p.VwapExit),
         p.Quantity.ToString(CultureInfo.InvariantCulture), N(p.Notional), N(p.WeightPercent), N(p.RiskAtStopInr), N(p.RiskSharePercent), N(p.NetGainAtVwapInr), N(p.Score)];

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

    // ---- CSV export of the grids (the files hold every row found today, not only the newest 500 shown) ----

    private static string ToCsv(string[] headers, IEnumerable<string[]> rows) =>
        string.Join('\n', rows.Select(r => string.Join(',', r)).Prepend(string.Join(',', headers))) + "\n";

    private (string Name, string Csv, int Rows)? SignalsFile(string stamp)
    {
        var rows = coordinator.ExportSignals().Select(SignalCells).ToList();
        return rows.Count == 0 ? null : ($"vwap-signals-{stamp}.csv", ToCsv(SignalHeaders, rows), rows.Count);
    }

    private (string Name, string Csv, int Rows)? PortfolioFile(string stamp)
    {
        var rows = coordinator.ExportPositions().Select(x => PositionCells(x.At, x.Position)).ToList();
        return rows.Count == 0 ? null : ($"vwap-portfolio-{stamp}.csv", ToCsv(PositionHeaders, rows), rows.Count);
    }

    private string Stamp() => $"{coordinator.Last?.Scan.Date.Replace("-", "") ?? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{DateTime.Now:HHmmss}";

    private async void OnExportSignals(object? sender, EventArgs e) => await ExportAsync(SignalsFile(Stamp()));

    private async void OnExportPortfolio(object? sender, EventArgs e) => await ExportAsync(PortfolioFile(Stamp()));

    /// <summary>Saves the file to Downloads\VwapScanner; where Android cannot do that, offers it through the share sheet instead.</summary>
    private async Task ExportAsync((string Name, string Csv, int Rows)? file)
    {
        if (file is not { } f) { await DisplayAlertAsync("Export CSV", "The grid is empty - nothing to export.", "OK"); return; }
        try
        {
            if (await saver.SaveCsvAsync(f.Name, f.Csv) is { } saved)
            {
                await DisplayAlertAsync("Export CSV", $"Saved {f.Rows} row(s) to {saved}", "OK");
                return;
            }
            await Share.Default.RequestAsync(new ShareFileRequest { Title = f.Name, File = Write(f.Name, f.Csv) });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Export CSV", "Could not save the file: " + ex.Message, "OK");
        }
    }

    /// <summary>Both grids as CSV files through the Android share sheet (WhatsApp, e-mail, Drive...).</summary>
    private async void OnShare(object? sender, EventArgs e)
    {
        try
        {
            var stamp = Stamp();
            var files = new[] { SignalsFile(stamp), PortfolioFile(stamp) }.OfType<(string Name, string Csv, int Rows)>().Select(f => Write(f.Name, f.Csv)).ToList();
            if (files.Count == 0) { await DisplayAlertAsync("Share CSV", "No signals or positions to share.", "OK"); return; }
            await Share.Default.RequestAsync(new ShareMultipleFilesRequest { Title = "VWAP scan results", Files = files });
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
