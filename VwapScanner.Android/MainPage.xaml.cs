using System.Globalization;
using static VwapScanner.GridCsv;

namespace VwapScanner.Mobile;

public partial class MainPage : ContentPage
{
    private readonly ApiState state;
    private readonly AppConfig config;
    private readonly ScanCoordinator coordinator;
    private readonly IServiceProvider services;
    private readonly IFileSaver saver;
    private readonly IReminderScheduler reminders;
    private int restoredRevision = -1;
    private bool updatingSwitch;
    private RunResult? shown;

    private readonly ResultTable signalsTable = new(
        SignalHeaders,
        [110, 110, 200, 90, 90, 100, 80, 120, 80, 90, 90, 90, 90, 90, 90, 90]
    );
    private readonly ResultTable portfolioTable = new(
        PositionHeaders,
        [
            90,
            110,
            110,
            200,
            90,
            90,
            90,
            90,
            80,
            100,
            120,
            130,
            140,
            160,
            80,
            90,
            90,
            90,
            90,
            90,
            130,
            120,
            70,
            80,
            130,
            260,
            80,
            70,
            80,
        ]
    );
    private int historyEpoch = -1,
        signalRows,
        positionRows;

    public MainPage(
        ApiState state,
        AppConfig config,
        ScanCoordinator coordinator,
        IFileSaver saver,
        IReminderScheduler reminders,
        IServiceProvider services
    )
    {
        InitializeComponent();
        this.state = state;
        this.config = config;
        this.coordinator = coordinator;
        this.services = services;
        this.saver = saver;
        this.reminders = reminders;
        SignalsGridHost.Content = signalsTable.View;
        PortfolioGridHost.Content = portfolioTable.View;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Guard.Run(this, "VWAP Scanner", AppearingCore);
    }

    private async Task AppearingCore()
    {
        coordinator.Changed += OnCoordinatorChanged;
        reminders.Schedule(); // keep the daily reminder alarm in place (alarms are lost after a reboot or an app update)
        if (restoredRevision != config.Revision)
        {
            restoredRevision = config.Revision;
            StatusLabel.Text = "Checking the saved Zerodha session...";
            try
            {
                await config.LoadSecretsAsync();
                await Task.Run(() => state.RestoreAsync(CancellationToken.None));
            }
            catch (ZerodhaException e)
            {
                AppLog.Error("Session", e);
                StatusLabel.Text = "Could not check the saved Zerodha token: " + e.Message;
                return;
            }
        }
        await coordinator.ResumeIfEnabledAsync(interactive: true); // auto-refresh was on when the app was closed or killed
        await coordinator.AutoStartIfDefaultAsync(); // otherwise start it by default (setting "Start auto-refresh automatically")
        await coordinator.FinalizeDayAsync(); // after the close: final open/high/low/close, then save today's CSV files if that has not happened yet
        Refresh();
    }

    protected override void OnDisappearing()
    {
        coordinator.Changed -= OnCoordinatorChanged;
        base.OnDisappearing();
    }

    // This runs on the UI thread outside any handler guard, so an exception here would close the whole app: it is logged instead.
    private void OnCoordinatorChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Refresh();
            }
            catch (Exception e)
            {
                AppLog.Error("Ui", e, "Refresh");
            }
        });

    /// <summary>Redraws everything from the connection state and the coordinator (which keeps scanning while this page is not shown).</summary>
    private string? shownStrategy; // strategy of the result currently on screen

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
        StatusLabel.Text = string.Join(
            "\n",
            new[] { coordinator.Status, help }
                .Where(x => x.Length > 0)
                .DefaultIfEmpty("Connected to Zerodha.")
        );

        if (coordinator.Last is { } last && !ReferenceEquals(last, shown))
        {
            shown = last;
            Show(last);
        }
        var selected = config.Value("Scan:StrategyName");
        if (selected.Length == 0)
            selected = StrategyCatalog.ScalpVwap;
        StrategyLabel.Text =
            $"Strategy: {selected}"
            + (shownStrategy is not null && !shownStrategy.Equals(selected, StringComparison.OrdinalIgnoreCase)
                ? $" (results below are from {shownStrategy}; tap Scan now to use {selected})"
                : "");
        SyncTables();
    }

    /// <summary>Appends the rows found since the last refresh to the two grids (nothing happens when a refresh found nothing new).</summary>
    private void SyncTables()
    {
        var chunk = coordinator.TakeHistory(historyEpoch, signalRows, positionRows);
        if (chunk.Reset)
        {
            signalsTable.Clear();
            portfolioTable.Clear();
        }
        historyEpoch = chunk.Epoch;
        if (chunk.Signals.Count > 0)
            signalsTable.Append(chunk.Signals.Select(x => SignalCells(x, coordinator.OhlcFor(x))));
        if (chunk.Positions.Count > 0)
            portfolioTable.Append(
                chunk.Positions.Select(x =>
                    PositionCells(
                        x.At,
                        x.Position,
                        coordinator.OhlcFor(x.Position),
                        coordinator.ExitFor(x.Position)
                    )
                )
            );
        signalRows = chunk.SignalTotal;
        positionRows = chunk.PositionTotal;
        SignalsGridHeader.Text = $"Signals grid ({signalRows} row(s) today)";
        PortfolioGridHeader.Text = $"Portfolio grid ({positionRows} row(s) today)";
        ShowTotals();
        ShareButton.IsVisible = signalRows > 0 || positionRows > 0;
        ExportSignalsButton.IsEnabled = signalRows > 0;
        ExportPortfolioButton.IsEnabled = positionRows > 0;
    }

    /// <summary>The totals bar under the portfolio grid: total investment (sum of price x quantity) and total profit / loss. Screen only, never in the CSV files.</summary>
    private void ShowTotals()
    {
        var t = coordinator.PortfolioTotals();
        PortfolioTotalsBar.IsVisible = t.Rows > 0;
        if (t.Rows == 0)
            return;
        CapitalTotalLabel.IsVisible = t.CapitalText is not null;
        CapitalTotalLabel.Text = t.CapitalText ?? "";
        TrackingTotalLabel.IsVisible = t.TrackingText is not null;
        TrackingTotalLabel.Text = t.TrackingText is null
            ? ""
            : t.TrackingText + (t.TrackingStatus is null ? "" : "\n" + t.TrackingStatus);
        InvestmentTotalLabel.Text =
            $"Total investment (price x quantity): INR {t.Invested.ToString("N2", CultureInfo.InvariantCulture)}  ({t.Rows} row(s))";
        if (t.PricedRows == 0)
        {
            ProfitLossTotalLabel.Text = "Total profit / loss: not available yet";
            ProfitLossTotalLabel.TextColor = null;
            TotalsNoteLabel.Text = "Prices for the profit / loss appear after the next scan.";
            return;
        }
        var sign = t.ProfitLoss > 0 ? "+" : "";
        ProfitLossTotalLabel.Text =
            $"Total profit / loss: INR {sign}{t.ProfitLoss.ToString("N2", CultureInfo.InvariantCulture)}";
        ProfitLossTotalLabel.TextColor =
            t.ProfitLoss > 0 ? Colors.Green
            : t.ProfitLoss < 0 ? Colors.Red
            : null;
        TotalsNoteLabel.Text =
            (
                t.AsOf == "15:30"
                    ? "At the day's close (15:30)"
                    : $"Marked to the latest close ({t.AsOf})"
            )
            + ": sum of (close - price) x quantity, before brokerage, taxes and slippage."
            + (
                t.PricedRows < t.Rows
                    ? $" {t.Rows - t.PricedRows} row(s) without a close are left out."
                    : ""
            );
    }

    private async void OnSettings(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnSettingsCore(sender, e));

    private async Task OnSettingsCore(object? sender, EventArgs e) =>
        await Navigation.PushAsync(services.GetRequiredService<SettingsPage>());

    private async void OnLogin(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnLoginCore(sender, e));

    private async Task OnLoginCore(object? sender, EventArgs e)
    {
        try
        {
            await config.LoadSecretsAsync();
            var login = services.GetRequiredService<LoginPage>();
            login.Start(); // throws when the API key is not set
            await Navigation.PushAsync(login);
        }
        catch (ZerodhaException ex)
        {
            AppLog.Error("Login", ex);
            await DisplayAlertAsync(
                "Zerodha login",
                ex.Message + "\nOpen Settings to enter the key.",
                "OK"
            );
        }
    }

    private async void OnScan(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnScanCore(sender, e));

    private async Task OnScanCore(object? sender, EventArgs e)
    {
        if (coordinator.Busy)
        {
            coordinator.CancelScan();
            return;
        } // second tap cancels the running scan
        await coordinator.ScanOnceAsync(auto: false);
    }

    private async void OnAutoToggled(object? sender, ToggledEventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnAutoToggledCore(sender, e));

    private async Task OnAutoToggledCore(object? sender, ToggledEventArgs e)
    {
        if (updatingSwitch)
            return;
        if (e.Value)
        {
            if (!await coordinator.StartAutoAsync())
                Refresh();
        }
        else
            coordinator.StopAuto();
    }

    // ---- CSV export of the grids (the files hold every row found today, not only the newest 500 shown) ----

    private (string Name, string Csv, int Rows)? SignalsFile(string stamp)
    {
        var rows = coordinator
            .ExportSignals()
            .Select(x => SignalCells(x, coordinator.OhlcFor(x)))
            .ToList();
        return rows.Count == 0
            ? null
            : ($"vwap-signals-{stamp}.csv", ToCsv(SignalHeaders, rows), rows.Count);
    }

    private (string Name, string Csv, int Rows)? PortfolioFile(string stamp)
    {
        var rows = coordinator
            .ExportPositions()
            .Select(x =>
                PositionCells(
                    x.At,
                    x.Position,
                    coordinator.OhlcFor(x.Position),
                    coordinator.ExitFor(x.Position)
                )
            )
            .ToList();
        return rows.Count == 0
            ? null
            : ($"vwap-portfolio-{stamp}.csv", ToCsv(PositionHeaders, rows), rows.Count);
    }

    private string Stamp() =>
        $"{coordinator.Last?.Scan.Date.Replace("-", "") ?? DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-{DateTime.Now:HHmmss}";

    private async void OnExportSignals(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnExportSignalsCore(sender, e));

    private async Task OnExportSignalsCore(object? sender, EventArgs e) =>
        await ExportAsync(SignalsFile(Stamp()));

    private async void OnExportPortfolio(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnExportPortfolioCore(sender, e));

    private async Task OnExportPortfolioCore(object? sender, EventArgs e) =>
        await ExportAsync(PortfolioFile(Stamp()));

    /// <summary>Saves the file to Downloads\VwapScanner; where Android cannot do that, offers it through the share sheet instead.</summary>
    private async Task ExportAsync((string Name, string Csv, int Rows)? file)
    {
        if (file is not { } f)
        {
            await DisplayAlertAsync("Export CSV", "The grid is empty - nothing to export.", "OK");
            return;
        }
        try
        {
            if (await saver.SaveCsvAsync(f.Name, f.Csv) is { } saved)
            {
                await DisplayAlertAsync("Export CSV", $"Saved {f.Rows} row(s) to {saved}", "OK");
                return;
            }
            await Share.Default.RequestAsync(
                new ShareFileRequest { Title = f.Name, File = Write(f.Name, f.Csv) }
            );
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Export CSV", "Could not save the file: " + ex.Message, "OK");
        }
    }

    /// <summary>Both grids as CSV files through the Android share sheet (WhatsApp, e-mail, Drive...).</summary>
    private async void OnShare(object? sender, EventArgs e) =>
        await Guard.Run(this, "VWAP Scanner", () => OnShareCore(sender, e));

    private async Task OnShareCore(object? sender, EventArgs e)
    {
        try
        {
            var stamp = Stamp();
            var files = new[] { SignalsFile(stamp), PortfolioFile(stamp) }
                .OfType<(string Name, string Csv, int Rows)>()
                .Select(f => Write(f.Name, f.Csv))
                .ToList();
            if (files.Count == 0)
            {
                await DisplayAlertAsync("Share CSV", "No signals or positions to share.", "OK");
                return;
            }
            await Share.Default.RequestAsync(
                new ShareMultipleFilesRequest { Title = "VWAP scan results", Files = files }
            );
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or FeatureNotSupportedException)
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

    /// <summary>"RSI 52.3 | SuperTrend up | EMA 101.20 above 100.80" for a signal, or nothing when the values are not known.</summary>
    private static string IndicatorLine(VwapSignal s)
    {
        var parts = new List<string>();
        if (s.Rsi is { } rsi)
            parts.Add($"RSI {F(rsi)}");
        if (s.SuperTrendUp is { } up)
            parts.Add($"SuperTrend {(up ? "up" : "down")}");
        if (s.EmaFast is { } fast && s.EmaSlow is { } slow)
            parts.Add($"EMA {F(fast)} {(fast >= slow ? "above" : "below")} {F(slow)}");
        return parts.Count == 0 ? "" : "\n" + string.Join(" | ", parts);
    }

    private void Show(RunResult r)
    {
        var scan = r.Scan;
        var plan = r.Plan;
        shownStrategy = r.Strategy;
        var summary =
            $"{scan.Date} as of {scan.AsOf} IST - {r.Strategy}: {scan.Signals.Count} signal(s) in {scan.Scanned} stocks ({r.Universe})"
            + (scan.Failed > 0 ? $", {scan.Failed} failed" : "")
            + (
                scan.BenchmarkFilter ? ", NIFTY 50 downtrend filter on." : ", benchmark filter off."
            );
        if (scan.Message is not null)
            summary += "\n" + scan.Message;
        if (scan.DiagnosticsText is { } dt)
            summary += "\n" + dt;
        else if (scan.Diagnostics is { } d)
            summary +=
                $"\nCandles checked {d.Evaluated} (not ready {d.NotReady}, NIFTY downtrend {d.BenchmarkBlocked}). Each test alone: below VWAP {d.BelowVwap}, turn-up {d.TurnUp}, "
                + $"rel. volume {d.RelativeVolumeOk}, turnover {d.TurnoverOk}, target clears costs {d.TargetClearsCosts}, entry window {d.InEntryWindow}.";
        if (scan.FilterText is { } ft)
            summary += "\n" + ft;
        SummaryLabel.Text = summary;

        SignalsHeader.IsVisible = true;
        SignalsHeader.Text =
            scan.Signals.Count == 0
                ? "Signals - none right now"
                : $"Signals ({scan.Signals.Count})";
        BindableLayout.SetItemsSource(
            SignalsList,
            scan.Signals.Select(s => new Row(
                    $"{s.Symbol}   {s.BarTime:HH:mm}   score {F(s.Score)}",
                    $"Close {F(s.Close)} | VWAP {F(s.Vwap)} | stretch {F(s.StretchAtr)} ATR | rel. vol {F(s.RelativeVolume)}\nStop {F(s.Stop)} | Target {F(s.Target)}" + IndicatorLine(s),
                    coordinator.NewKeys.Contains(ScanCoordinator.Key(s))
                ))
                .ToList()
        );

        PortfolioHeader.IsVisible = PortfolioSummary.IsVisible = true;
        PortfolioSummary.Text =
            $"Capital INR {plan.Capital:N0}, invested {plan.Invested:N0} ({(plan.Capital > 0 ? plan.Invested / plan.Capital * 100 : 0):0.0}%), cash {plan.Cash:N0}.\n"
            + $"Risk at stops INR {plan.TotalRiskAtStopInr:N0} of {plan.RiskBudgetInr:N0}; net gain if price returns to VWAP INR {plan.TotalNetGainAtVwapInr:N0}.";
        BindableLayout.SetItemsSource(
            PortfolioList,
            plan.Positions.Select(p => new Row(
                    $"{p.Symbol}   x{p.Quantity}",
                    $"Price {F(p.Price)} | notional INR {p.Notional:N0} ({p.WeightPercent:0.0}%)\nStop {F(p.Stop)} | risk INR {p.RiskAtStopInr:N0} | gain at VWAP INR {p.NetGainAtVwapInr:N0}"
                ))
                .ToList()
        );
        NotesLabel.Text = string.Join("\n", plan.Notes);
    }

    private static string F(decimal x) => x.ToString("0.00", CultureInfo.InvariantCulture);
}
