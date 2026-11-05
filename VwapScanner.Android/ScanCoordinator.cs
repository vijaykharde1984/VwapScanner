using System.Globalization;
using System.Text.Json;

namespace VwapScanner.Mobile;

public sealed record HistoryChunk(int Epoch, bool Reset, IReadOnlyList<VwapSignal> Signals,
    IReadOnlyList<(DateTimeOffset At, PortfolioPosition Position)> Positions, int SignalTotal, int PositionTotal);

/// <summary>Totals shown under the portfolio grid (never written to CSV). Invested = sum of price x quantity of all rows; ProfitLoss = sum of (latest close - price) x quantity
/// over the rows whose stock has a known close (PricedRows); AsOf = the time of those closes (HH:mm, "15:30" once the day is final).</summary>
public sealed record PortfolioTotals(int Rows, decimal Invested, decimal ProfitLoss, int PricedRows, string? AsOf);

/// <summary>Shows Android notifications (implemented in Platforms/Android).</summary>
public interface INotifier
{
    /// <summary>Asks for the notification permission where Android requires it (13+); true when notifications can be shown.</summary>
    Task<bool> EnsurePermissionAsync();
    void Notify(int id, string title, string text);
    /// <summary>The daily reminder: a notification with or without sound and vibration, as chosen in Settings.</summary>
    void Remind(string title, string text, bool sound, bool vibrate);
}

/// <summary>Keeps the app process alive while auto-refresh runs, with an ongoing notification (an Android foreground service).</summary>
/// <summary>Saves a text file where the user can find it (Android: Downloads\VwapScanner).</summary>
public interface IFileSaver
{
    /// <summary>The place it was saved to, or null when this Android version cannot save directly (the caller then shares the file).</summary>
    Task<string?> SaveCsvAsync(string fileName, string content);
}

/// <summary>Schedules the daily reminder with Android's alarm service (works while the app is closed).</summary>
public interface IReminderScheduler
{
    /// <summary>(Re)schedules the next reminder from the current settings, or cancels it when no time is set.</summary>
    void Schedule();
}

public interface IKeepAlive
{
    void Start(string text);
    void Stop();
    /// <summary>Android wakes the app at this time and starts the service (and resumes auto-refresh) even if the app was closed meanwhile.</summary>
    void ScheduleStart(DateTimeOffset at);
    void CancelScheduledStart();
}

/// <summary>
/// Runs scans for the UI: a manual scan, or the auto-refresh loop that re-scans every N minutes between 09:15 and 15:30 IST.
/// It remembers which signals were already seen today, so each scan can mark the new ones and an auto-refresh can notify about them.
/// The page only displays what the coordinator holds, so the loop keeps going while the screen is off or another page is open.
/// </summary>
public sealed class ScanCoordinator(ApiState state, AppConfig config, INotifier notifier, IKeepAlive keepAlive, IFileSaver saver)
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly TimeSpan MarketOpen = new(9, 15, 0), MarketClose = new(15, 30, 0), StopAfter = new(15, 35, 0);

    private readonly object sync = new();
    private readonly HashSet<string> seen = [];
    private string seenDay = "";
    private CancellationTokenSource? loopCts, scanCts;

    // Everything found today, appended scan after scan: the data behind the result grids and the CSV export.
    private readonly object historyLock = new();
    private readonly List<VwapSignal> signalHistory = [];
    private readonly List<(DateTimeOffset At, PortfolioPosition Position)> positionHistory = [];
    private readonly HashSet<string> positionSeen = [];
    private int historyEpoch;
    private bool historyLoaded;
    private const string AutoKey = "auto_date", StoppedKey = "auto_stopped_date", CsvSavedKey = "csv_saved_date";
    private const string MarketClosedText = "Market closed - auto-refresh stopped.";
    private static string HistoryPath => Path.Combine(FileSystem.AppDataDirectory, "history.json");
    private static string IstToday() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(Ist).DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record HistoryPosition(DateTimeOffset At, PortfolioPosition Position);
    private sealed record HistoryFile(string Day, List<VwapSignal> Signals, List<HistoryPosition> Positions, List<string> Seen, List<string> PositionSeen,
        Dictionary<string, DayOhlc>? Ohlc = null);

    // Day open/high/low/close of each row (signal key / position key), as of the scan that found it; replaced by the final values after the close.
    private readonly Dictionary<string, DayOhlc> ohlcByRow = [];
    private const string OhlcFinalKey = "ohlc_final_date";

    private static string PositionKey(PortfolioPosition p) =>
        $"{p.Symbol}|{p.BarTime.ToString("O", CultureInfo.InvariantCulture)}|{p.Quantity}|{p.Price.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Totals of every portfolio row of today. Profit/loss is gross (no brokerage, taxes or slippage) and marks each position to the latest close known for its stock,
    /// so rows found at different times are valued at the same price; after the market closes that price is the day's close.
    /// </summary>
    public PortfolioTotals PortfolioTotals()
    {
        EnsureHistory();
        lock (historyLock)
        {
            var latest = new Dictionary<string, DayOhlc>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, p) in positionHistory)
                if (ohlcByRow.GetValueOrDefault(PositionKey(p)) is { } o
                    && (!latest.TryGetValue(p.Symbol, out var best) || string.CompareOrdinal(o.AsOf, best.AsOf) > 0))
                    latest[p.Symbol] = o;
            decimal invested = 0, pnl = 0; var priced = 0;
            foreach (var (_, p) in positionHistory)
            {
                invested += p.Price * p.Quantity;
                if (!latest.TryGetValue(p.Symbol, out var o)) continue;
                pnl += (o.Close - p.Price) * p.Quantity;
                priced++;
            }
            return new(positionHistory.Count, invested, pnl, priced, latest.Count == 0 ? null : latest.Values.Max(x => x.AsOf));
        }
    }

    public DayOhlc? OhlcFor(VwapSignal s) { lock (historyLock) return ohlcByRow.GetValueOrDefault(Key(s)); }
    public DayOhlc? OhlcFor(PortfolioPosition p) { lock (historyLock) return ohlcByRow.GetValueOrDefault(PositionKey(p)); }

    /// <summary>Loads today's grid rows saved by an earlier run of the app (once), so a restart does not lose them.</summary>
    private void EnsureHistory()
    {
        lock (historyLock)
        {
            if (historyLoaded) return;
            historyLoaded = true;
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var file = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(HistoryPath));
                if (file is null || (file.Day != IstToday() && file.Day != config.Value("Scan:Date"))) return;
                seenDay = file.Day;
                seen.UnionWith(file.Seen);
                positionSeen.UnionWith(file.PositionSeen);
                signalHistory.AddRange(file.Signals);
                positionHistory.AddRange(file.Positions.Select(p => (p.At, p.Position)));
                if (file.Ohlc is not null) foreach (var (k, v) in file.Ohlc) ohlcByRow[k] = v;
                historyEpoch++;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* start with empty grids */ }
        }
    }

    private void SaveHistory()
    {
        HistoryFile snapshot;
        lock (historyLock)
            snapshot = new(seenDay, [.. signalHistory], [.. positionHistory.Select(x => new HistoryPosition(x.At, x.Position))], [.. seen], [.. positionSeen],
                new Dictionary<string, DayOhlc>(ohlcByRow));
        try
        {
            var temp = HistoryPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot));
            File.Move(temp, HistoryPath, overwrite: true);
        }
        catch (Exception) { /* saving is best effort: the grids still work from memory */ }
    }

    public RunResult? Last { get; private set; }
    public IReadOnlySet<string> NewKeys { get; private set; } = new HashSet<string>();
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool AutoRunning => loopCts is not null;
    /// <summary>Auto-refresh is running, or was on today and is about to resume: no reminder is needed.</summary>
    public bool IsAutoActiveToday => AutoRunning || Preferences.Default.Get(AutoKey, "") == IstToday();

    /// <summary>Raised from any thread whenever the status, the busy flag or the result changes.</summary>
    public event Action? Changed;

    public static string Key(VwapSignal s) => $"{s.Symbol}|{s.BarTime.ToString("O", CultureInfo.InvariantCulture)}";

    private void Update(string? status = null)
    {
        if (status is not null) Status = status;
        Changed?.Invoke();
    }

    /// <summary>One scan with the current settings. False when it did not produce a result (busy, cancelled or failed; see Status).</summary>
    public async Task<bool> ScanOnceAsync(bool auto, CancellationToken ct = default)
    {
        CancellationTokenSource mine;
        lock (sync)
        {
            if (Busy) return false;
            Busy = true;
            mine = scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        Update("Scanning... (about 20 seconds per 50 stocks; Kite allows 3 requests a second)");
        try
        {
            await config.LoadSecretsAsync();
            // Between two auto scans there are as many new candles as minutes, so the lookback must cover them.
            var minLookback = auto ? config.AutoRefreshMinutes + 1 : 0;
            var result = await Task.Run(() => state.RunAsync(mine.Token, msg => Update("Scanning... " + msg.Trim()), minLookback), mine.Token);
            Apply(result, auto);
            Update($"Last scan at {DateTime.Now:HH:mm:ss} (data as of {result.Scan.AsOf} IST).");
            return true;
        }
        catch (OperationCanceledException) { Update(ct.IsCancellationRequested ? "Stopped." : "Scan cancelled."); return false; }
        catch (ZerodhaAuthException e) { AppLog.Error("Scan", e); Update(e.Message); return false; }
        catch (ZerodhaException e) { AppLog.Error("Scan", e); Update(e.Message); return false; }
        catch (ConfigurationException e) { AppLog.Error("Scan", e); Update("Configuration: " + e.Message); return false; }
        catch (Exception e) { AppLog.Error("Scan", e); Update(e.GetType().Name + ": " + e.Message); return false; }
        finally
        {
            lock (sync) { Busy = false; scanCts = null; }
            mine.Dispose();
            Update();
        }
    }

    /// <summary>The rows added since the caller last looked. <c>Reset</c> means a new trading day started, so the caller must clear its grids first.</summary>
    public HistoryChunk TakeHistory(int epoch, int signalsFrom, int positionsFrom)
    {
        EnsureHistory();
        lock (historyLock)
        {
            var reset = epoch != historyEpoch;
            if (reset) { signalsFrom = 0; positionsFrom = 0; }
            return new(historyEpoch, reset, signalHistory.Skip(signalsFrom).ToList(), positionHistory.Skip(positionsFrom).ToList(),
                signalHistory.Count, positionHistory.Count);
        }
    }

    public List<VwapSignal> ExportSignals() { EnsureHistory(); lock (historyLock) return [.. signalHistory]; }
    public List<(DateTimeOffset At, PortfolioPosition Position)> ExportPositions() { EnsureHistory(); lock (historyLock) return [.. positionHistory]; }

    /// <summary>Cancels the scan that is running now (an auto-refresh loop carries on with its next scan).</summary>
    public void CancelScan()
    {
        lock (sync) scanCts?.Cancel();
    }

    private void Apply(RunResult r, bool auto)
    {
        EnsureHistory();
        if (r.Scan.Date != seenDay)
        {
            seen.Clear(); seenDay = r.Scan.Date;
            lock (historyLock) { signalHistory.Clear(); positionHistory.Clear(); positionSeen.Clear(); ohlcByRow.Clear(); historyEpoch++; }
        }
        var fresh = r.Scan.Signals.Where(s => seen.Add(Key(s))).ToList();
        lock (historyLock)
        {
            // Only results that are not already in the grid: a signal is identified by stock + candle, a position by stock + candle + quantity + price.
            signalHistory.AddRange(fresh.OrderBy(s => s.BarTime).ThenByDescending(s => s.Score));
            foreach (var s in fresh)
                if (r.Ohlc.TryGetValue(s.Symbol, out var o)) ohlcByRow[Key(s)] = o;
            foreach (var p in r.Plan.Positions)
                if (positionSeen.Add(PositionKey(p)))
                {
                    positionHistory.Add((DateTimeOffset.Now, p));
                    if (r.Ohlc.TryGetValue(p.Symbol, out var o)) ohlcByRow[PositionKey(p)] = o;
                }
        }
        SaveHistory();
        NewKeys = fresh.Select(Key).ToHashSet();
        Last = r;
        if (auto && fresh.Count > 0 && config.Notify)
        {
            var top = fresh.OrderByDescending(s => s.Score).Take(3)
                .Select(s => $"{s.Symbol} {s.Close.ToString("0.00", CultureInfo.InvariantCulture)} ({s.StretchAtr.ToString("0.0", CultureInfo.InvariantCulture)} ATR below VWAP)");
            var more = fresh.Count > 3 ? $" +{fresh.Count - 3} more" : "";
            var positions = r.Plan.Positions.Count;
            notifier.Notify(1, $"VWAP: {fresh.Count} new signal(s)",
                string.Join(", ", top) + more + (positions > 0 ? $". Portfolio: {positions} position(s)." : "."));
        }
    }

    // ---- auto-refresh ----

    /// <param name="interactive">False when started by the background service (no screen to ask for the notification permission on).</param>
    public async Task<bool> StartAutoAsync(bool interactive = true)
    {
        if (AutoRunning) return true;
        if (!state.Connected) { Update("Log in to Zerodha before starting auto-refresh."); return false; }
        if (interactive && config.Notify) await notifier.EnsurePermissionAsync();
        var cts = new CancellationTokenSource();
        loopCts = cts;
        Preferences.Default.Remove(StoppedKey);
        Preferences.Default.Set(AutoKey, IstToday()); // "auto-refresh is on today": lets a restarted app or service carry on
        serviceStarted = false;
        startScheduled = false;
        EnsureService();
        Update($"Auto-refresh on: every {config.AutoRefreshMinutes} min until 15:30 IST.");
        _ = Task.Run(() => LoopAsync(cts));
        return true;
    }

    private bool serviceStarted, startScheduled;

    /// <summary>
    /// Starts the background (foreground-service) notification, but not hours before the market opens: Android allows such a service only about
    /// 6 hours, so it starts 5 minutes before 09:15 IST. Until then the loop just waits inside the app.
    /// </summary>
    private void EnsureService()
    {
        if (serviceStarted) return;
        if (!config.Replaying)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(Ist);
            var startAt = MarketOpen - TimeSpan.FromMinutes(5);
            if (now.TimeOfDay < startAt)
            {
                // Too early for the service. An alarm starts it at 09:10 even if the app is closed (or was closed by Android) until then.
                if (!startScheduled)
                {
                    startScheduled = true;
                    keepAlive.ScheduleStart(new DateTimeOffset(DateOnly.FromDateTime(now.DateTime).ToDateTime(TimeOnly.FromTimeSpan(startAt)), Ist));
                }
                return;
            }
        }
        serviceStarted = true;
        keepAlive.Start($"Scanning every {config.AutoRefreshMinutes} min until the market closes");
    }

    public void StopAuto()
    {
        Preferences.Default.Remove(AutoKey);
        Preferences.Default.Set(StoppedKey, IstToday()); // the user stopped it: do not start it again automatically today
        loopCts?.Cancel();
        keepAlive.CancelScheduledStart();
    }

    /// <summary>
    /// Default behaviour: start auto-refresh when the app opens. Only when the setting is on, Zerodha is connected, it is a weekday before 15:35 IST,
    /// no past day is being replayed, and the user has not stopped it today.
    /// </summary>
    public async Task<bool> AutoStartIfDefaultAsync()
    {
        if (!config.AutoStart || AutoRunning || Busy || config.Replaying || !state.Connected) return false;
        if (Preferences.Default.Get(StoppedKey, "") == IstToday()) return false;
        var now = DateTimeOffset.UtcNow.ToOffset(Ist);
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || now.TimeOfDay >= StopAfter) return false;
        return await StartAutoAsync(interactive: true);
    }

    /// <summary>
    /// Carries on with auto-refresh after the app or its process was stopped (swiped away, killed by Android, reopened) while it was on today.
    /// False when it was not on, or could not resume (then the reason is shown and a notification is posted).
    /// </summary>
    public async Task<bool> ResumeIfEnabledAsync(bool interactive)
    {
        if (AutoRunning) return true;
        var day = Preferences.Default.Get(AutoKey, "");
        if (day.Length == 0) return false;
        if (day != IstToday()) { Preferences.Default.Remove(AutoKey); return false; }
        try
        {
            await config.LoadSecretsAsync();
            if (!state.Connected) await Task.Run(() => state.RestoreAsync(CancellationToken.None));
        }
        catch (ZerodhaException e) { AppLog.Error("Resume", e); Update("Could not resume auto-refresh: " + e.Message); return false; }
        if (!state.Connected)
        {
            Preferences.Default.Remove(AutoKey);
            Update("Auto-refresh could not resume: log in to Zerodha again.");
            if (config.Notify) notifier.Notify(2, "VWAP Scanner", "Auto-refresh stopped: the Zerodha session is not valid. Open the app and log in again.");
            return false;
        }
        return await StartAutoAsync(interactive);
    }

    private async Task LoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string? ending = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var closing = false;
                EnsureService();
                if (!config.Replaying)
                {
                    var now = DateTimeOffset.UtcNow.ToOffset(Ist);
                    var weekday = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                    if (!weekday || now.TimeOfDay >= StopAfter) { ending = MarketClosedText; break; }
                    if (now.TimeOfDay < MarketOpen)
                    {
                        Update($"Waiting for the market to open (09:15 IST, now {now:HH:mm}).");
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        continue;
                    }
                    closing = now.TimeOfDay >= MarketClose; // this is the closing scan: it sees the last candle of the day
                }
                await ScanOnceAsync(auto: true, ct);
                if (!state.Connected)
                {
                    ending = "Zerodha session ended - log in again.";
                    AppLog.Warn("Auto", "Zerodha session ended during auto-refresh.");
                    if (config.Notify) notifier.Notify(2, "VWAP Scanner", "The Zerodha session ended. Open the app and log in again.");
                    break;
                }
                if (closing) { ending = MarketClosedText; break; }
                var wait = TimeSpan.FromMinutes(config.AutoRefreshMinutes);
                if (!config.Replaying)
                {
                    // Wake up just after 15:30 so the closing scan is not delayed by a long interval.
                    var untilClose = MarketClose + TimeSpan.FromSeconds(20) - DateTimeOffset.UtcNow.ToOffset(Ist).TimeOfDay;
                    if (untilClose > TimeSpan.Zero && untilClose < wait) wait = untilClose;
                }
                Update($"Auto-refresh on. Next scan at {DateTime.Now.Add(wait):HH:mm}.");
                await Task.Delay(wait, ct);
            }
        }
        catch (OperationCanceledException) { ending = "Auto-refresh stopped."; }
        catch (Exception e) { AppLog.Error("Auto", e); ending = "Auto-refresh stopped by an unexpected error: " + e.Message; } // the loop must never end silently or take the app down
        finally
        {
            if (ReferenceEquals(loopCts, cts)) { loopCts = null; Preferences.Default.Remove(AutoKey); } // ended on purpose: do not resume
            cts.Dispose();
            AppLog.Info("Auto", "Auto-refresh ended: " + ending);
            Update(ending);
            if (ending == MarketClosedText) await FinalizeDayAsync(); // before the keep-alive service is released
            serviceStarted = false;
            startScheduled = false;
            keepAlive.CancelScheduledStart();
            keepAlive.Stop();
        }
    }

    /// <summary>
    /// After the close, saves today's signals and portfolio rows as CSV files (Download/VwapScanner), once per day. Called when auto-refresh ends at the close,
    /// and when the app is opened after the close (in case it was not running then). Nothing is saved for a day without results.
    /// </summary>
    /// <summary>After the close: updates open/high/low/close of every row of today with the day's final values (grids and CSV), then saves the CSV files.</summary>
    public async Task FinalizeDayAsync()
    {
        await UpdateFinalOhlcAsync();
        await SaveDailyCsvIfDueAsync();
    }

    /// <summary>Replaces the open/high/low/close stored with today's rows by the final values of the day. Once per day; retried at the next call when Zerodha could not be reached.</summary>
    public async Task<bool> UpdateFinalOhlcAsync()
    {
        if (config.Replaying) return false;
        var now = DateTimeOffset.UtcNow.ToOffset(Ist);
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || now.TimeOfDay < MarketClose) return false;
        var today = IstToday();
        if (Preferences.Default.Get(OhlcFinalKey, "") == today || !state.Connected) return false;
        EnsureHistory();
        string[] symbols;
        lock (historyLock)
        {
            if (seenDay != today) return false;
            symbols = [.. signalHistory.Select(s => s.Symbol).Concat(positionHistory.Select(p => p.Position.Symbol)).Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        if (symbols.Length == 0) return false;
        try
        {
            var final = await state.FinalOhlcAsync(symbols, DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture), CancellationToken.None);
            lock (historyLock)
            {
                foreach (var s in signalHistory) if (final.TryGetValue(s.Symbol, out var o)) ohlcByRow[Key(s)] = o;
                foreach (var (_, p) in positionHistory) if (final.TryGetValue(p.Symbol, out var o)) ohlcByRow[PositionKey(p)] = o;
                historyEpoch++; // the grids redraw all rows with the final values
            }
            SaveHistory();
            Preferences.Default.Set(OhlcFinalKey, today);
            AppLog.Info("Ohlc", $"Final open/high/low/close of {final.Count} stock(s) stored after the close.");
            Update();
            return true;
        }
        catch (Exception e)
        {
            AppLog.Error("Ohlc", e, "Final open/high/low/close after the close");
            return false;
        }
    }

    /// <summary>What the last save attempt did, in words (shown by the Settings "test" button).</summary>
    public string LastSaveMessage { get; private set; } = "";

    /// <param name="force">Test run: ignores the time of day, the weekend, replay mode and the saved-today flag, and names the files "-test".</param>
    public async Task<bool> SaveDailyCsvIfDueAsync(bool force = false)
    {
        if (!force)
        {
            if (!config.SaveCsvAtClose || config.Replaying) return false;
            var now = DateTimeOffset.UtcNow.ToOffset(Ist);
            if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || now.TimeOfDay < MarketClose) return false;
        }
        var today = IstToday();
        if (!force && Preferences.Default.Get(CsvSavedKey, "") == today) return false;
        EnsureHistory();
        List<VwapSignal> signals; List<(DateTimeOffset At, PortfolioPosition Position)> positions;
        lock (historyLock)
        {
            if (!force && seenDay != today) return false;
            signals = [.. signalHistory];
            positions = [.. positionHistory];
        }
        if (signals.Count == 0 && positions.Count == 0) { LastSaveMessage = "There are no rows in the grids yet, so there is nothing to save. Run a scan that finds signals first."; return false; }
        try
        {
            var stamp = today.Replace("-", "") + (force ? "-test" : "-close");
            var saved = false;
            if (signals.Count > 0) saved |= await saver.SaveCsvAsync($"vwap-signals-{stamp}.csv", GridCsv.SignalsCsv(signals, OhlcFor)) is not null;
            if (positions.Count > 0) saved |= await saver.SaveCsvAsync($"vwap-portfolio-{stamp}.csv", GridCsv.PortfolioCsv(positions, x => OhlcFor(x))) is not null;
            if (!saved) { LastSaveMessage = "This Android version cannot save the CSV files automatically - use Export CSV."; if (!force) Update("Market closed. " + LastSaveMessage); return false; }
            if (!force) Preferences.Default.Set(CsvSavedKey, today);
            var message = $"{(force ? "Test: saved" : "Market closed. Saved")} {signals.Count} signal row(s) and {positions.Count} portfolio row(s) to Download/VwapScanner.";
            LastSaveMessage = message;
            AppLog.Info("Csv", message);
            if (!force) { Update(message); if (config.Notify) notifier.Notify(4, "VWAP Scanner", message); }
            return true;
        }
        catch (Exception e)
        {
            AppLog.Error("Csv", e);
            LastSaveMessage = "Could not save the CSV files: " + e.Message;
            if (!force) Update("Market closed. " + LastSaveMessage);
            return false;
        }
    }
}
