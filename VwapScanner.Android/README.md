# VwapScanner.Android

.NET MAUI (Android) port of the VwapScanner web API in the sibling folder `..\VwapScanner.WebApi`. The web project is not modified by this app.

Copied unchanged: `VwapStrategy.cs`, `Portfolio.cs`, `Scanner.cs`, `TokenStore.cs`. `Settings.cs` is a copy without the `Output` settings, and `Reporter.cs` is not copied: this app never writes CSV files by itself.
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
4. Auto-refresh switch: re-scans every N minutes (default 5) between 09:15 and 15:30 IST, Monday to Friday, and stops by itself after the close. The lookback is raised to the interval so no candle between two scans is missed. While it runs, an ongoing notification and a foreground service keep the app alive with the screen off, and a notification announces each new signal (Android 13+ asks for the notification permission). If Android still pauses the app, exclude it from battery optimization. Android limits this kind of foreground service to about 6 hours.
5. Result grids (bottom of the main screen): a signals grid and a portfolio grid with the same columns as the web API's CSV files (the portfolio grid has an extra scanned_at column). After every refresh, manual or automatic, that finds something new, the new rows are appended at the bottom and tinted; refreshes with nothing new change nothing. A signal is identified by stock + candle, a position by stock + candle + quantity + price, so repeated scans do not duplicate rows. The grids start empty on each new trading day and show the newest 500 rows; scroll sideways for all columns.
6. Export CSV (button above each grid): writes the grid's data as a CSV file into the phone's `Download/VwapScanner` folder (Android 10+; older versions open the share sheet instead). The file holds every row found today, not only the newest 500 shown, with the same columns as the grid. This replaces the web API's automatic CSV writing: nothing is written until you tap Export.
7. Share CSV: sends both grids as CSV files through the Android share sheet (WhatsApp, e-mail, Drive...).

Surviving a closed app: while auto-refresh is on, the app records "on for today" on the phone. The foreground service is sticky and the notification has a **Stop auto-refresh** button. If Android stops the app's process (for example after you swipe it away), it restarts the service, which logs back in with the saved Zerodha token and carries on; opening the app does the same. Today's grid rows are saved to a file in the app's private storage and reloaded after a restart. Not possible: a force stop (or an Android "sleeping apps" battery limit) ends everything until you open the app again, and an expired Zerodha session (after 06:00 IST) needs a new login; a notification says so.

Other settings (fees, redirect URL) are in `Resources/Raw/appsettings.json`, same names as the web API.
The API key/secret and saved Zerodha token of the web project are intentionally not copied.
