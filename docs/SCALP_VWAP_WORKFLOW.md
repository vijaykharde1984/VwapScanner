# Scalp VWAP: End-to-End Workflow

The application's `ScalpVwap` strategy is a **long-only mean-reversion strategy**. It looks for a stock trading at least one ATR below VWAP that has started to recover, then plans a stop one ATR below the entry and a target 1.5 times that distance above it. The scan also shows the VWAP level as the "recovery" reference for each signal.

This document describes the current implementation in the Android app (`VwapScanner.Android`), where `ScalpVwap` is the default strategy. The app is **read-only**: it finds signals, sizes a simulated portfolio and monitors exits, but it never places orders.

## 1. Select the strategy and its settings

The strategy is chosen in Settings > Scan > Strategy (`Scan:StrategyName`: `ScalpVwap` or `VwapPullback`). `ScalpVwap` is the default. The choice applies to the next scan. Selecting a strategy does **not** apply a preset or overwrite other settings; each strategy has its own section (`Scan:Strategy:*` for `ScalpVwap`, `Scan:Pullback:*` for `VwapPullback`), so these values come from `appsettings.json` or your saved Settings.

| Setting | Default |
|---|---|
| Candle interval | 1 minute (Kite Connect) |
| Entry window | 09:30–15:00 IST (the decision time is the end of the signal candle) |
| Minimum stretch below VWAP | 1.0 × ATR |
| Minimum relative volume | 1.0 |
| Minimum candle turnover | ₹5 lakh |
| Minimum target distance | 15 basis points |
| Stop distance | 1.0 × ATR |
| Reward : risk | 1.5 |

Fixed in code (not settings): EMA 5 and EMA 13, ATR period 10, three-candle momentum, relative volume against the previous 20 candles.

Capital, fees, risk limits, universe and the benchmark filter are shared with `VwapPullback` and stay configurable.

Source: `VwapStrategy.cs` (unchanged from the web project), `Strategies.cs`.

## 2. Receive completed candles

The app reads candles by polling, not by streaming ticks:

```text
Scan now / auto-refresh (every N minutes)
  -> ScanCoordinator
  -> ApiState.RunAsync
  -> Scanner.ScanAsync
  -> Kite Connect historical 1-minute candles (cached per instrument and day)
  -> only completed candles (candle end time <= "as of" time)
```

Only the missing minutes are fetched on each refresh. The strategy evaluates **completed candles**, never the candle still forming. Past days can be scanned by giving a date and an "as of" time. The scan reports signals from the latest candle and from the last N candles (`Lookback`, default set in Settings).

Source: `Scanner.cs`, `ApiState.cs`.

## 3. Update indicators

For every stock, a fresh `VwapState` is created for the scan and fed the day's candles in order. Each candle updates:

- Session VWAP, using cumulative typical price × volume.
- ATR (10), using the average of true range.
- EMA 5 and EMA 13.
- Three-candle momentum (close minus the close three candles earlier).
- Relative volume: the candle's volume against the average of the previous 20 candles.

The VWAP calculation is:

```text
Typical price = (High + Low + Close) / 3
VWAP = Sum(Typical price × Volume) / Sum(Volume)
```

The state is **ready** after 21 candles. With the market open at 09:15, the first possible decision is about 09:36, which is later than the 09:30 entry start, so readiness is the real limit early in the day.

Source: `VwapStrategy.cs`.

## 4. Check whether the benchmark permits entries

If the NIFTY 50 benchmark filter is on, the scan builds a separate `VwapState` for NIFTY 50 and marks every candle time where it was in a downtrend:

```text
Downtrend = Ready AND EMA5 < EMA13 AND three-candle momentum < 0
```

Stock candles at those times are skipped and counted as "NIFTY downtrend" in the diagnostics. A neutral benchmark can permit entries; a strong benchmark uptrend is not required.

The gate is applied by the scan, not inside the stock's own signal test: `VwapState.Candidate()` does **not** check the stock's own trend.

Source: `VwapStrategy.cs`, `Scanner.cs`.

## 5. Find qualifying stock signals

`VwapState.Candidate()` requires all of the following on the **signal candle**:

1. Indicators are ready (21 candles), ATR is positive, and at least two candles exist.
2. The stock closes at least one ATR below VWAP.
3. Its close is higher than the previous candle's close, the candle has a positive range, and it closes in the upper half of that range.
4. Relative volume meets the configured minimum.
5. Candle turnover meets the configured minimum.
6. The calculated target distance meets `MinTargetBps`.
7. The decision time (candle end) is within the entry window, 09:30–15:00.

The main calculations are:

```text
Stretch = (VWAP − candle close) / ATR
Required: Stretch >= 1

Recovery: current close > previous close
Upper-half close: close >= low + (high − low) / 2
Turnover: close × volume >= MinBarTurnover

Target distance in basis points =
  StopAtrMultiple × ATR × RewardRiskRatio / close × 10,000
```

**The strategy does not require VWAP itself to rise, or the stock's trend to be up.** It checks for an improving closing price below VWAP.

`MinTargetBps` is a distance threshold, not a full fee calculation. The net-of-cost check happens when the portfolio is built.

**There is no one-signal-per-setup cool-down.** If several consecutive candles qualify, each can produce a signal when the scan looks back over more than one candle.

Each scan also records how many candles passed each test on its own (below VWAP, turn-up, volume, turnover, target, window). This text is shown in the scan summary to explain why few or no signals appeared.

Source: `VwapStrategy.cs`.

## 6. Rank signals and build the portfolio

Signals are listed newest first, then by score:

```text
Score = Stretch + min(RelativeVolume, 3) / 10
```

The signals feed the portfolio optimizer:

- The portfolio's stop-risk budget is split across the best `MaxPositions` signals in proportion to score.
- Quantity is whole shares, sized from the loss at the stop including both legs' Zerodha intraday fees and slippage.
- Limits apply: risk per trade, total portfolio stop risk, asset allocation, volume participation and available cash.
- A row whose target would not leave a positive net gain after costs is not bought.
- Each row also shows the net gain if price recovers to VWAP.

The plan is a **simulation of sizing**. It does not send anything to Zerodha.

For a signal, the initial levels are:

```text
Entry  = signal candle's close
Stop   = close − 1.0 × ATR
Target = close + 1.5 × (1.0 × ATR)
```

Source: `Portfolio.cs`.

## 7. Monitor the position and exit

The Exit Monitor treats each portfolio row as bought at the **close of its signal candle**. The next candle is the first one that counts. It replays the day's candles, so the answer is the same whether it runs every 15 seconds or once after an app restart.

| Condition | Result |
|---|---|
| A candle's high reaches the target | `EXIT_TARGET` at the target |
| A candle's low reaches the stop | `EXIT_STOP` at the stop (if one candle touches both, the stop wins) |
| Still held at the close-out time (15:15 by default) | `EXIT_CLOSE` at the last close |
| Reversal Guard armed, then the stop moves to breakeven and is touched | `EXIT_BREAKEVEN` |
| Reversal Guard armed, then a giveback, repeated closes below VWAP or a fading trend | `EXIT_REVERSAL` |

Reversal Guard (on by default): once the position has covered 50% of the way from entry to target, the stop rises to the buy price plus a small cost buffer. After that, giving back 40% of the peak gain, several lower closes below VWAP, or a projected fade ends the trade.

Stop Reprieve (on by default, 15 minutes): when a candle dips to the stop but the price is already back above it, and VWAP is at least one buy-to-stop distance above the price, the stop exit can be put off for a set number of minutes, judged on completed 1-minute closes. This is meaningful for `ScalpVwap` because the stock is bought below VWAP.

**The fixed target and VWAP are different things.** The target is calculated from the entry and ATR. VWAP is shown as a reference and used by Stop Reprieve and the Reversal Guard, but reaching VWAP does **not** by itself close the position in this app. If real trading fills differ from the signal close, results will differ from the monitor's replay.

Source: `ExitEvaluator.cs`, `ScanCoordinator.cs`.

## 8. Results and what the app does not do

The app shows the signals grid, portfolio grid, per-row exit decisions, totals and diagnostics, and can save CSV files at the market close. The summary line names the strategy used. If you change the strategy in Settings, the screen shows the selected strategy and warns when the results below came from a different one; tap Scan now to refresh.

```text
Scan flow:
  Kite 1-minute candles -> Scanner -> VwapState (per stock)
    -> signals -> Portfolio optimizer -> portfolio rows
    -> Exit Monitor (target / stop / close-out / Reversal Guard / Stop Reprieve)
    -> grids, CSV, notifications

Not part of the app:
  order placement, live fills, broker order book
```

## Worked signal example

Assume the completed candle and indicators have these values:

| Input | Value |
|---|---|
| VWAP | ₹100.00 |
| ATR | ₹0.50 |
| Previous candle close | ₹99.30 |
| Current candle close | ₹99.40 |
| Current candle high / low | ₹99.50 / ₹99.10 |
| Relative volume | 1.5 |

The stretch is:

```text
(100.00 − 99.40) / 0.50 = 1.2 ATR
```

The candle passes the stretch check, closes above the previous close, and closes above its ₹99.30 range midpoint. The levels and score are:

```text
Stop   = 99.40 − 0.50       = ₹98.90
Target = 99.40 + 1.5 × 0.50 = ₹100.15  (75 bps, clears 15 bps)
Score  = 1.2 + 1.5/10       = 1.35
```

It becomes a signal only if turnover, the entry window and the benchmark gate also pass. The portfolio optimizer then decides whether it is bought and in what quantity. A stop, target, close-out, Reversal Guard or Stop Reprieve can end the position.

These rules define behavior; they do not establish that a signal or the strategy will be profitable.
