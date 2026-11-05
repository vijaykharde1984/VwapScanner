using System.Globalization;

namespace VwapScanner.Mobile;

public sealed record HistoryChunk(int Epoch, bool Reset, IReadOnlyList<VwapSignal> Signals,
    IReadOnlyList<(DateTimeOffset At, PortfolioPosition Position)> Positions, int SignalTotal, int PositionTotal);

/// <summary>Shows Android notifications (implemented in Platforms/Android).</summary>
public interface INotifier
{
    /// <summary>Asks for the notification permission where Android requires it (13+); true when notifications can be shown.</summary>
    Task<bool> EnsurePermissionAsync();
    void Notify(int id, string title, string text);
}

/// <summary>Keeps the app process alive while auto-refresh runs, with an ongoing notification (an Android foreground service).</summary>
/// <summary>Saves a text file where the user can find it (Android: Downloads\VwapScanner).</summary>
public interface IFileSaver
{
    /// <summary>The place it was saved to, or null when this Android version cannot save directly (the caller then shares the file).</summary>
    Task<string?> SaveCsvAsync(string fileName, string content);
}

public interface IKeepAlive
{
    void Start(string text);
    void Stop();
}

/// <summary>
/// Runs scans for the UI: a manual scan, or the auto-refresh loop that re-scans every N minutes between 09:15 and 15:30 IST.
/// It remembers which signals were already seen today, so each scan can mark the new ones and an auto-refresh can notify about them.
/// The page only displays what the coordinator holds, so the loop keeps going while the screen is off or another page is open.
/// </summary>
public sealed class ScanCoordinator(ApiState state, AppConfig config, INotifier notifier, IKeepAlive keepAlive)
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

    public RunResult? Last { get; private set; }
    public IReadOnlySet<string> NewKeys { get; private set; } = new HashSet<string>();
    public string Status { get; private set; } = "";
    public bool Busy { get; private set; }
    public bool AutoRunning => loopCts is not null;

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
        catch (ZerodhaAuthException e) { Update(e.Message); return false; }
        catch (ZerodhaException e) { Update(e.Message); return false; }
        catch (ConfigurationException e) { Update("Configuration: " + e.Message); return false; }
        catch (Exception e) { Update(e.GetType().Name + ": " + e.Message); return false; }
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
        lock (historyLock)
        {
            var reset = epoch != historyEpoch;
            if (reset) { signalsFrom = 0; positionsFrom = 0; }
            return new(historyEpoch, reset, signalHistory.Skip(signalsFrom).ToList(), positionHistory.Skip(positionsFrom).ToList(),
                signalHistory.Count, positionHistory.Count);
        }
    }

    public List<VwapSignal> ExportSignals() { lock (historyLock) return [.. signalHistory]; }
    public List<(DateTimeOffset At, PortfolioPosition Position)> ExportPositions() { lock (historyLock) return [.. positionHistory]; }

    /// <summary>Cancels the scan that is running now (an auto-refresh loop carries on with its next scan).</summary>
    public void CancelScan()
    {
        lock (sync) scanCts?.Cancel();
    }

    private void Apply(RunResult r, bool auto)
    {
        if (r.Scan.Date != seenDay)
        {
            seen.Clear(); seenDay = r.Scan.Date;
            lock (historyLock) { signalHistory.Clear(); positionHistory.Clear(); positionSeen.Clear(); historyEpoch++; }
        }
        var fresh = r.Scan.Signals.Where(s => seen.Add(Key(s))).ToList();
        lock (historyLock)
        {
            // Only results that are not already in the grid: a signal is identified by stock + candle, a position by stock + candle + quantity + price.
            signalHistory.AddRange(fresh.OrderBy(s => s.BarTime).ThenByDescending(s => s.Score));
            foreach (var p in r.Plan.Positions)
                if (positionSeen.Add($"{p.Symbol}|{p.BarTime.ToString("O", CultureInfo.InvariantCulture)}|{p.Quantity}|{p.Price.ToString(CultureInfo.InvariantCulture)}"))
                    positionHistory.Add((DateTimeOffset.Now, p));
        }
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

    public async Task<bool> StartAutoAsync()
    {
        if (AutoRunning) return true;
        if (!state.Connected) { Update("Log in to Zerodha before starting auto-refresh."); return false; }
        if (config.Notify) await notifier.EnsurePermissionAsync();
        var cts = new CancellationTokenSource();
        loopCts = cts;
        keepAlive.Start($"Scanning every {config.AutoRefreshMinutes} min until the market closes");
        Update($"Auto-refresh on: every {config.AutoRefreshMinutes} min until 15:30 IST.");
        _ = Task.Run(() => LoopAsync(cts));
        return true;
    }

    public void StopAuto() => loopCts?.Cancel();

    private async Task LoopAsync(CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string? ending = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!config.Replaying)
                {
                    var now = DateTimeOffset.UtcNow.ToOffset(Ist);
                    var weekday = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                    if (!weekday || now.TimeOfDay >= StopAfter) { ending = "Market closed - auto-refresh stopped."; break; }
                    if (now.TimeOfDay < MarketOpen)
                    {
                        Update($"Waiting for the market to open (09:15 IST, now {now:HH:mm}).");
                        await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        continue;
                    }
                }
                await ScanOnceAsync(auto: true, ct);
                if (!state.Connected)
                {
                    ending = "Zerodha session ended - log in again.";
                    if (config.Notify) notifier.Notify(2, "VWAP Scanner", "The Zerodha session ended. Open the app and log in again.");
                    break;
                }
                var minutes = config.AutoRefreshMinutes;
                Update($"Auto-refresh on. Next scan at {DateTime.Now.AddMinutes(minutes):HH:mm}.");
                await Task.Delay(TimeSpan.FromMinutes(minutes), ct);
            }
        }
        catch (OperationCanceledException) { ending = "Auto-refresh stopped."; }
        finally
        {
            keepAlive.Stop();
            if (ReferenceEquals(loopCts, cts)) loopCts = null;
            cts.Dispose();
            Update(ending);
        }
    }
}
