# VwapScanner.Android

.NET MAUI (Android) port of the VwapScanner web API in the sibling folder `..\VwapScanner.WebApi`. The web project is not modified by this app.

Copied unchanged: `VwapStrategy.cs`, `Portfolio.cs`, `TokenStore.cs`. `Scanner.cs` and `Settings.cs` have diverged from the web project (strategy switch; `Settings.cs` also lacks the `Output` settings). `Reporter.cs` is not copied: this app never writes CSV files by itself.
Adapted copies: `KiteClient.cs` (one exception type fully qualified), `ApiState.cs` (typed `RunResult` instead of an anonymous JSON object, progress callback),
`Universe.cs` (reads the stock lists from the APK).
New: `AppConfig.cs`, `MauiProgram.cs`, `App`, `MainPage`, `LoginPage`, `SettingsPage`, `Platforms/Android`.

## Build / run
Needs the .NET 10 SDK with the MAUI Android workload (`dotnet workload install maui-android`), the Android SDK and a JDK (Visual Studio's ".NET Multi-platform App UI development" workload installs them).

    dotnet build VwapScanner.Android.csproj -f net10.0-android
    dotnet build VwapScanner.Android.csproj -f net10.0-android -t:Run     # on a connected device / emulator

Or open `VwapScanner.slnx` in Visual Studio, set **VwapScanner.Android** as the startup project and pick an Android device.

## Using it
1. Settings: enter the Kite Connect API key and secret (kept in Android SecureStorage, never in the APK). Below them you can edit the stock list, lookback, benchmark filter, replay date/time, all strategy thresholds, the portfolio limits and the auto-refresh options. Values are kept on the phone on top of `Resources/Raw/appsettings.json`; "Reset" restores the shipped ones.
2. Zerodha login: logs in inside the app. The Kite app's Redirect URL stays `http://127.0.0.1:9079/` - the in-app browser intercepts that redirect and finishes the login. Sessions end at 06:00 IST.
3. Scan now: runs the same ScalpVwap scan and portfolio optimization as the web API's `GET /` and lists the signals and the sized positions. Read-only: it never places orders. Signals not seen in an earlier scan today are marked NEW.
4. Auto-refresh switch (on by default: it starts when the app opens, signed in, on a market day before 15:35 IST; setting "Start auto-refresh automatically"; switching it off, or Stop in the notification, keeps it off for the rest of that day): re-scans every N minutes (default 5) between 09:15 and 15:30 IST, Monday to Friday, and stops by itself after the close. The lookback is raised to the interval so no candle between two scans is missed. While it runs, an ongoing notification and a foreground service keep the app alive with the screen off, and a notification announces each new signal (Android 13+ asks for the notification permission). If Android still pauses the app, exclude it from battery optimization. Android limits this kind of foreground service to about 6 hours.
5. Result grids (bottom of the main screen): a signals grid and a portfolio grid with the same columns as the web API's CSV files (the portfolio grid has an extra scanned_at column). After every refresh, manual or automatic, that finds something new, the new rows are appended at the bottom and tinted; refreshes with nothing new change nothing. A signal is identified by stock + candle, a position by stock + candle + quantity + price, so repeated scans do not duplicate rows. The grids start empty on each new trading day and show the newest 500 rows; scroll sideways for all columns.
6. Export CSV (button above each grid): writes the grid's data as a CSV file into the phone's `Download/VwapScanner` folder (Android 10+; older versions open the share sheet instead). The file holds every row found today, not only the newest 500 shown, with the same columns as the grid. This replaces the web API's automatic CSV writing: nothing is written until you tap Export.
7. Share CSV: sends both grids as CSV files through the Android share sheet (WhatsApp, e-mail, Drive...).

Automatic CSV at market close: when auto-refresh reaches the close it makes one last scan just after 15:30 IST, then saves the day's signals and portfolio CSV files (`vwap-signals-<date>-close.csv`, `vwap-portfolio-<date>-close.csv`, same content as the grids' Export CSV) to `Download/VwapScanner` and posts a notification. If the app was not running at the close, the files are saved the next time you open it that day (once per day). No file is written for a day without results. Setting: "Save CSV files at market close" (on by default). On Android 9 and older, which cannot save to Downloads directly, use Export CSV.

Daily reminder: a notification at the time set in Settings (default 09:10 IST, weekdays) reminds you to start the scan, with the sound and vibration switched on or off in Settings; empty time = no reminder. It uses Android's alarm service, so it fires even when the app is closed, and is skipped when auto-refresh is already running. Sound and vibration are fixed per Android notification channel, so the app keeps one channel for each combination. "Send test reminder" in Settings shows it at once. Alarms are re-set after a reboot or app update and whenever the app opens. For an exact time on Android 12+, allow "Alarms & reminders" for the app in Android settings (otherwise it can be a few minutes late). The notification sound is the phone's default notification sound; if the phone is on silent or Do Not Disturb, Android applies that.

Surviving a closed app: while auto-refresh is on, the app records "on for today" on the phone. The foreground service is sticky and the notification has a **Stop auto-refresh** button. If Android stops the app's process (for example after you swipe it away), it restarts the service, which logs back in with the saved Zerodha token and carries on; opening the app does the same. Today's grid rows are saved to a file in the app's private storage and reloaded after a restart. Not possible: a force stop (or an Android "sleeping apps" battery limit) ends everything until you open the app again, and an expired Zerodha session (after 06:00 IST) needs a new login; a notification says so.

Other settings (fees, redirect URL) are in `Resources/Raw/appsettings.json`, same names as the web API.
The API key/secret and saved Zerodha token of the web project are intentionally not copied.

## Exit Monitor and Reversal Guard

A separate loop (independent of the 5-minute scan) decides when each portfolio row should be sold. Every `App:ExitCheckSeconds` (default 15) it downloads the day's live 1-minute candles, including the minute still forming, and replays the candles after the buy:

- `EXIT_TARGET`: a candle's high reaches the target.
- `EXIT_STOP`: a candle's low reaches the stop. If one candle touches both, the stop wins (the candle does not say which came first).
- `EXIT_CLOSE`: still held at the close-out time (`App:ExitCloseTime`, default 15:15): sell at the market.
- Reversal Guard, for a position that has risen and is now fading. It arms when the peak has covered 50% of the way to the target. From then on the stop moves up to the buy price plus a 5 bps buffer (`EXIT_BREAKEVEN`), and after a completed candle the row is sold as `EXIT_REVERSAL` when 40% of the peak gain is given back, or 3 lower closes in a row happen below the day's VWAP, or the trend of the last closes, projected one refresh interval ahead, ends at or below the breakeven level.

Guard state while holding: `WATCH`, `ARMED`, `FADING`. Grid and CSV columns: `exit_status`, `exit_time`, `exit_price`, `net_gain_at_exit_inr`, `exit_reason`, `peak_price`, `peak_time`, `guard_state`. A notification is posted for each sell decision. It detects the exit; it does not place orders. It runs while the app process is alive: the foreground service keeps it alive during auto-refresh.

## Stop Reprieve

The stop column stays the one and only stop loss. When a dip touches the stop (a plain `EXIT_STOP`, not after the Reversal Guard armed) but the price is already back above it, and the day's VWAP is at least one buy-to-stop distance above the price, the Exit Monitor waits `App:StopReprieveMinutes` (default 15) instead of selling. While waiting, a completed 1-minute candle that closes at or below the stop sells at that close, and the target sells at once. The wait is also cancelled (sold at that close) when the price makes no progress: after as many lower closes in a row as set under Reversal Guard (default 3), or when by the half-way point the close is not above the price at the start of the wait. When the wait ends the normal rules apply again. One reprieve per position. A dip whose price is still at or below the stop, or with no room to the VWAP, sells at the stop immediately. Guard state while waiting: `REPRIEVE`; a notification is posted when it starts. Switch it off with `App:StopReprieveEnabled`.

## Strategy switch
Settings > Scan > Strategy chooses which strategy the scan runs (the Settings page lists the options; the change applies to the next scan):

- **ScalpVwap**: the original mean-reversion VWAP scalp (`VwapStrategy.cs`, untouched).
- **VwapPullback** (default): long-only VWAP trend pullback (`PullbackStrategy.cs`). It needs an established uptrend (most closes above VWAP, rising VWAP, EMA9 > EMA20, higher highs and lows), a pullback that touches VWAP, then a green confirmation candle. The stop sits below the pullback low or VWAP, and the target is 2R by default. Its options are under "Pullback strategy" in Settings (`Scan:Pullback:*` in `appsettings.json`).

Notes:
- Both strategies produce the same `VwapSignal`, so the optimizer, Exit Monitor, grids and CSV work unchanged. The scan summary is labelled with the strategy name.
- For VwapPullback, `StretchAtr` is negative (the close is above VWAP) and `net_gain_at_vwap_inr` describes a downside scenario, since VWAP lies below the entry. Stop Reprieve is effectively inactive for it.
- New strategies plug in through `StrategyCatalog` in `Strategies.cs` (implement `ISignalState`).
- The signal logic was compiled and tested outside MAUI. Backtest before relying on it.

## Indicators and optional filters
Every signal carries RSI(14), SuperTrend (10, 3) and EMA 9 / 20 (`Indicators.cs`). They are shown under each signal and added to the signals CSV as `rsi`, `supertrend`, `ema_fast`, `ema_slow`.
Settings > Indicators sets the periods. Settings > "ScalpVwap filters" and "Pullback filters" switch three optional filters on per strategy: SuperTrend bullish, close above the fast EMA, and RSI within a min/max window. All filters are **off by default**, so behavior is unchanged until you turn one on. A filter drops a signal the strategy already found; the scan summary then shows how many were rejected.
`VwapSignal` (in `VwapStrategy.cs`) gained four optional trailing fields for these values; `VwapPullback` now uses EMA 9 / 20 for its uptrend test. The filters are not backtested: check them on real data before relying on them.

