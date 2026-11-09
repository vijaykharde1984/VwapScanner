# VWAP Pullback: End-to-End Workflow

The application's `VwapPullback` strategy is a **long-only trend-continuation strategy**. It looks for a stock in an established intraday uptrend that has pulled back to VWAP and then shown a confirmed bounce, and plans a stop below the pullback and a target at a fixed multiple of the risk.

This document describes the current implementation in the Android app (`VwapScanner.Android`). The app is **read-only**: it finds signals, sizes a simulated portfolio and monitors exits, but it never places orders.

## 1. Select the strategy and its settings

The strategy is chosen in Settings > Scan > Strategy (`Scan:StrategyName`: `ScalpVwap` or `VwapPullback`). The choice applies to the next scan. Selecting it does **not** overwrite other settings; the pullback has its own section, "Pullback strategy" (`Scan:Pullback:*` in `appsettings.json`).

| Setting | Default |
|---|---|
| Candle interval | 1 minute (Kite Connect) |
| Entry window | 09:45–14:45 IST (the decision time is the end of the signal candle) |
| Trend window (`TrendBars`) | 30 candles |
| Minimum share of closes above VWAP | 70% |
| Minimum VWAP rise over the window | 0.5 × ATR |
| Swing size (`SwingBars`) | 3 candles on each side |
| Pullback look-back (`PullbackBars`) | 8 candles (also the cool-down after a signal) |
| VWAP touch zone | Low at most 0.3 × ATR above VWAP |
| Maximum break below VWAP | 0.5 × ATR |
| Stop buffer | 0.25 × ATR |
| Allowed stop distance | 0.5 to 2.5 × ATR |
| Reward : risk | 2.0 |
| Minimum room to the day's high | 1.5 × risk |
| Minimum target distance | 15 basis points |
| Minimum relative volume | 1.0 |
| Minimum candle turnover | ₹5 lakh |

Fixed in code (not settings): EMA 9 and EMA 20, ATR period 10, relative volume against the previous 20 candles.

Capital, fees, risk limits, universe and the benchmark filter are shared with `ScalpVwap` and stay configurable. A bad combination (for example, entry start not before last entry) is rejected when saving and again before a scan runs (`PullbackOptions.Validate()`).

Source: `PullbackStrategy.cs`, `Strategies.cs`.

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

Only the missing minutes are fetched on each refresh. The strategy evaluates **completed candles**, never the candle still forming. Past days can be scanned by giving a date and an "as of" time.

Source: `Scanner.cs`, `ApiState.cs`.

## 3. Update indicators

For every stock, a fresh `PullbackState` is created for the scan and fed the day's candles in order. Each candle updates:

- Session VWAP, using cumulative typical price × volume, with the whole VWAP series kept for look-backs.
- ATR (10), using the average of true range.
- EMA 9 and EMA 20.
- Relative volume: the candle's volume against the average of the previous 20 candles.

The VWAP calculation is:

```text
Typical price = (High + Low + Close) / 3
VWAP = Sum(Typical price × Volume) / Sum(Volume)
```

The state is **ready** after 31 candles with the defaults (the larger of the trend window, 21 and 2 × swing size + 5, plus one). With the market open at 09:15, the first possible decision is therefore about 09:46, just after the entry window opens.

Source: `PullbackStrategy.cs`.

## 4. Check whether the benchmark permits entries

If the NIFTY 50 benchmark filter is on, the scan builds a separate VWAP state for NIFTY 50 and marks every candle time where it was in a downtrend:

```text
Downtrend = the benchmark's existing downtrend rule in VwapStrategy.cs (unchanged, shared with ScalpVwap)
```

Stock candles at those times are skipped and counted as "NIFTY downtrend" in the diagnostics. A neutral or rising benchmark permits entries. The filter is a Scan setting in Settings and is shared with `ScalpVwap`.

## 5. Find qualifying stock signals

`PullbackState` evaluates each completed candle. All of the following must hold on the **signal candle**:

1. **Window:** the decision time (candle end) is within 09:45–14:45.
2. **Confirmation candle:** green (close above open), closes above the previous candle's high, has a positive range, closes in the upper half of its range, and closes above VWAP.
3. **Volume:** relative volume at least 1.0.
4. **Turnover:** close × volume at least ₹5 lakh.
5. **Uptrend regime** (all four):
   - at least 70% of the last 30 closes were above the VWAP of their own minute;
   - VWAP has risen at least 0.5 ATR over those 30 candles;
   - EMA 9 is above EMA 20;
   - structure: the latest swing high is above the one before it, and the latest swing low is above the one before it (a swing is a candle higher or lower than the 3 candles each side).
6. **VWAP touch:** within the last 8 candles, a candle's low reached VWAP (at most 0.3 ATR above it) without falling more than 0.5 ATR below it. A deeper dive is treated as a break, not a pullback.
7. **Stop distance:** the risk (close minus stop) is between 0.5 and 2.5 ATR.
8. **Room:** the day's high is at least 1.5 × risk above the close.
9. **Target distance:** the target is at least 15 bps above the close.

The main calculations are:

```text
Pullback low = lowest low from the first VWAP touch to the signal candle
Stop         = min(pullback low, VWAP) − 0.25 × ATR
Risk         = close − stop
Target       = close + 2.0 × risk
Room         = day high − close            (required: Room >= 1.5 × risk)
StretchAtr   = (VWAP − close) / ATR        (negative here: the close is above VWAP)
```

**One signal per pullback.** After a candle qualifies, any further qualifying candle in the next 8 candles is dropped (a cool-down), so a single pullback produces one signal regardless of how many candles a scan looks back over.

**The strategy does not require the stock to be below VWAP.** Unlike `ScalpVwap`, it buys strength after a dip *to* VWAP.

Each scan also records how many candles passed each test on its own (uptrend, touch, confirmation, volume, turnover, stop distance, room, target, window, cool-down). This text is shown in the scan summary to explain why few or no signals appeared.

Source: `PullbackStrategy.cs`.

## 5b. Indicators and optional filters

Every signal now also carries **RSI(14)**, the **SuperTrend** direction (period 10, multiplier 3) and the **EMA 9 / EMA 20** values. They are shown under each signal in the app and added to the signals CSV as `rsi`, `supertrend`, `ema_fast` and `ema_slow`.

Three optional filters can then drop a signal the strategy already found. They are applied after the strategy rules and are **all off by default**, so behavior is unchanged until you switch one on in Settings (section "Pullback filters"):

| Filter (setting) | Rule when on | Default |
|---|---|---|
| Require SuperTrend bullish (`UseSuperTrend`) | Price must be above the SuperTrend line | Off |
| Require close above fast EMA (`UseEma`) | The signal candle must close above EMA 9 | Off |
| Require RSI in range (`UseRsi`) | RSI must lie between Min RSI and Max RSI | Off, window 45 to 75 |

Indicator periods (Settings > Indicators): fast EMA 9, slow EMA 20, RSI period 14, SuperTrend period 10, SuperTrend multiplier 3.

VwapPullback buys a dip inside an uptrend, so the RSI window defaults to "not collapsing, not yet overbought". Its own uptrend test already uses EMA 9 above EMA 20 and rising VWAP, so the EMA and SuperTrend filters overlap with it.

The scan summary adds a line such as "Indicator filters: 4 strategy signal(s) checked, 3 rejected (RSI 2, SuperTrend 1, EMA 0)." when a filter is on.

Source: `Indicators.cs`.

## 6. Rank signals and build the portfolio

Qualifying signals are listed newest first, then by score:

```text
Score = 1 + min(RelativeVolume, 3) / 3 + min(VWAP slope in ATR, 2) / 2 + min(Room / Risk, 3) / 6
```

The score ranges from 1 to about 3. Higher relative volume, a steeper VWAP and more room to the day's high rank higher.

The signals use the same `VwapSignal` record as `ScalpVwap`, so the **portfolio optimizer works unchanged**:

- The portfolio's stop-risk budget is split across the best `MaxPositions` signals in proportion to score.
- Quantity is whole shares, sized from the loss at the stop including both legs' Zerodha intraday fees and slippage.
- Limits apply: risk per trade, total portfolio stop risk, asset allocation, volume participation and available cash.
- A row whose target would not leave a positive net gain after costs is not bought.

The plan is a **simulation of sizing**. It does not send anything to Zerodha.

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

**There is no "price reached VWAP" exit for this strategy.** VWAP lies *below* the entry, so the target is a fixed 2 × risk above it. Two consequences:

- Stop Reprieve is effectively inactive. It only delays a stop when VWAP is at least one stop-distance above the price, which is not the case here.
- The "net gain at VWAP" column is a **downside scenario** (what is left if price falls back to VWAP), not an expected gain.

If a fill is not exactly at the signal close in real trading, actual results will differ from the monitor's replay.

Source: `ExitEvaluator.cs`, `ScanCoordinator.cs`.

## 8. Results and what the app does not do

The app shows the signals grid, portfolio grid, per-row exit decisions, totals and diagnostics, and can save CSV files at the market close. The summary line names the strategy used. If you change the strategy in Settings, the screen shows the selected strategy and warns when the results below came from a different one; tap Scan now to refresh.

```text
Scan flow:
  Kite 1-minute candles -> Scanner -> PullbackState (per stock)
    -> signals -> Portfolio optimizer -> portfolio rows
    -> Exit Monitor (target / stop / close-out / Reversal Guard)
    -> grids, CSV, notifications

Not part of the app:
  order placement, live fills, broker order book
```

## Worked signal example

Assume the completed candle and indicators have these values:

| Input | Value |
|---|---|
| VWAP | ₹100.00 |
| ATR | ₹0.40 |
| Lowest low since the touch | ₹99.90 |
| Previous candle high | ₹100.30 |
| Current candle open / high / low / close | ₹100.20 / ₹100.55 / ₹100.15 / ₹100.50 |
| Day's high so far | ₹102.00 |
| Relative volume | 1.5 |
| VWAP rise over 30 candles | 0.8 ATR |

The candle is green, closes above the previous high (₹100.30), sits in the upper half of its range and above VWAP. The pullback low of ₹99.90 is within the touch zone (at most ₹100.12 and at least ₹99.80), so the stop and target are:

```text
Stop   = min(99.90, 100.00) − 0.25 × 0.40 = ₹99.80
Risk   = 100.50 − 99.80                   = ₹0.70   (0.5 to 2.5 ATR: 0.2 to 1.0 passes)
Target = 100.50 + 2.0 × 0.70              = ₹101.90 (139 bps, clears 15 bps)
Room   = 102.00 − 100.50                  = ₹1.50   (needs at least 1.5 × 0.70 = ₹1.05: passes)
StretchAtr = (100.00 − 100.50) / 0.40     = −1.25
Score  = 1 + 1.5/3 + 0.8/2 + (1.50/0.70)/6 ≈ 2.26
```

It becomes a signal only if the uptrend regime (70% above VWAP, rising VWAP, EMA 9 above EMA 20, higher swing highs and lows), the entry window, turnover and benchmark conditions also pass, and no other signal fired in the last 8 candles. The portfolio optimizer then decides whether it is bought and in what quantity.

These rules define behavior; they do not establish that a signal or the strategy will be profitable. The strategy has been tested on synthetic candles only and should be backtested on real 1-minute data before relying on it.
