# VwapScanner.Android

.NET MAUI (Android) port of the VwapScanner web API in the sibling folder `..\VwapScanner.WebApi`. The web project is not modified by this app.

Copied unchanged: `VwapStrategy.cs`, `Portfolio.cs`, `Scanner.cs`, `TokenStore.cs`, `Settings.cs`, `Reporter.cs`.
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
5. Share CSV: sends `vwap-signals-*.csv` and `vwap-portfolio-*.csv` (same columns as the web API's files) through the Android share sheet.

Other settings (fees, redirect URL) are in `Resources/Raw/appsettings.json`, same names as the web API.
The API key/secret and saved Zerodha token of the web project are intentionally not copied.
