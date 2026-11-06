# VWAP Scanner: end to end flow

How the Android app scans, buys, watches and sells during one trading day, as of 6 November 2026.

## 1. A trading day, start to finish

Two loops run side by side while the market is open. The scan finds and buys. The Exit Monitor decides when to sell. Both write to the same saved history.

```mermaid
flowchart TD
  A["09:10 reminder or alarm<br/>starts the foreground service"] --> B["Open the app and log in to Zerodha Kite<br/>the session ends at 06:00 IST each day"]
  B --> C["Auto-refresh starts<br/>weekdays 09:15 to 15:30 IST"]
  C --> D["Scan loop<br/>every 5 minutes"]
  C --> E["Exit Monitor<br/>every 15 seconds"]
  D --> F[("history.json<br/>signals, positions, exits")]
  E --> F
  D --> G["Grids, totals bar and notifications"]
  E --> G
  E --> H["15:15 close-out<br/>sell whatever is still held"]
  H --> I["15:30 market close"]
  I --> J["Final day open, high, low, close<br/>added to every row"]
  J --> K["Two CSV files saved to<br/>Download/VwapScanner"]
  K --> L["Foreground service stops"]
```

## 2. The scan loop: finding and buying

The capital balance is worked out first, so a scan can never spend more than what is free.

```mermaid
flowchart TD
  S1["Every 5 minutes, or the Scan button"] --> S2["Free capital =<br/>capital + net gain of sold rows - money held in open rows<br/>plus the list of signals already bought"]
  S2 --> S3["Load the stock list and 1-minute candles from Kite<br/>paced at 3 requests per second"]
  S3 --> S4["VWAP strategy finds signals<br/>price stretched below VWAP, volume, score"]
  S4 --> S5["Drop signals that already have a position"]
  S5 --> S6{"Free capital at least 1 INR?"}
  S6 -- no --> S7["Buy nothing"]
  S6 -- yes --> S8["Portfolio optimizer sizes the buys against the free capital<br/>quantity, stop, target"]
  S8 --> S9["Add the new rows and save history.json"]
  S7 --> S9
  S9 --> S10["Notify new signals and refresh the grids"]
  S9 --> S11["Wake the Exit Monitor"]
```

## 3. The Exit Monitor loop: watching and selling

It runs apart from the scan, so a slow scan never delays an exit. Both share one Kite connection and one request pacer.

```mermaid
flowchart TD
  M1["Every 15 seconds<br/>from 09:15 to the close-out time"] --> M2["Rows still held"]
  M2 --> M3{"Any left?"}
  M3 -- no --> M4["Monitor stops"]
  M3 -- yes --> M5["One request per stock:<br/>the day's 1-minute candles from 09:15<br/>including the minute still forming"]
  M5 --> M6["Exit evaluator replays the candles after the buy"]
  M6 --> M7{"Exit decided?"}
  M7 -- yes --> M8["Record the exit with its price and net gain<br/>after fees and slippage<br/>the money returns to the free capital"]
  M8 --> M9["Save, send a SELL notification, refresh the grid"]
  M7 -- no --> M10["Keep holding<br/>state shown: WATCH, ARMED, FADING or REPRIEVE"]
  M9 --> M11["Wait 15 seconds"]
  M10 --> M11
  M11 --> M2
```

## 4. How one row is judged, candle by candle

The evaluator replays every 1-minute candle after the buy in order, so a restart gives the same answer as running all day.

```mermaid
flowchart TD
  C0["Next 1-minute candle after the buy"] --> C1{"Stop Reprieve wait running?"}
  C1 -- yes --> W1{"High reaches the target?"}
  W1 -- yes --> X1["EXIT_TARGET"]
  W1 -- no --> W2{"Completed candle closes<br/>at or below the stop?"}
  W2 -- yes --> X2["EXIT_STOP at that close"]
  W2 -- no --> W3{"3 lower closes in a row,<br/>or no gain by the half-way point?"}
  W3 -- yes --> X3["Wait cancelled<br/>EXIT_STOP at that close"]
  W3 -- no --> W4["Keep waiting<br/>normal rules return when the wait ends"]
  C1 -- no --> L1{"Low reaches the stop?<br/>after the guard armed: the breakeven level"}
  L1 -- yes --> R1{"Plain stop, reprieve on and unused,<br/>price back above the stop,<br/>VWAP at least one stop distance above?"}
  R1 -- yes --> R2["Start the 15 minute wait<br/>state REPRIEVE"]
  R1 -- no --> R3{"Reversal Guard armed?"}
  R3 -- yes --> X4["EXIT_BREAKEVEN"]
  R3 -- no --> X5["EXIT_STOP"]
  L1 -- no --> T1{"High reaches the target?"}
  T1 -- yes --> X6["EXIT_TARGET"]
  T1 -- no --> G1["Update the peak<br/>arm the guard at 50% of the way to the target"]
  G1 --> G2{"Guard armed and candle completed?"}
  G2 -- no --> N["Next candle<br/>after the last one: the close-out time gives EXIT_CLOSE, otherwise HOLD"]
  G2 -- yes --> G3{"Close at breakeven, 40% of the gain given back,<br/>3 lower closes below VWAP,<br/>or the trend projects below breakeven?"}
  G3 -- yes --> X7["EXIT_REVERSAL or EXIT_BREAKEVEN"]
  G3 -- no --> N
```

## Exit statuses

| Status | Meaning |
|---|---|
| `HOLD` | Keep the position. Guard state: WATCH, ARMED, FADING, or REPRIEVE while a stop wait runs. |
| `EXIT_TARGET` | The high reached the target. Sell at the target. |
| `EXIT_STOP` | The stop was reached, or a stop wait ended badly. The reason column says which. |
| `EXIT_BREAKEVEN` | The price came back to the buy price after the guard armed. |
| `EXIT_REVERSAL` | A rise is fading. Sell before it turns into a loss. |
| `EXIT_CLOSE` | Still held at the close-out time (15:15 by default). Sell at the market. |

## What is saved where

| Item | Content |
|---|---|
| history.json | Signals, portfolio rows, exits and day OHLC for today. Read again after an app restart. |
| CSV files | vwap-signals and vwap-portfolio, saved to Download/VwapScanner at the close. |
| Daily log | Kite and app errors with secrets removed. Share it from Settings. |
| Settings | Capital and risk limits, auto-refresh, Exit Monitor, Reversal Guard, Stop Reprieve. |
