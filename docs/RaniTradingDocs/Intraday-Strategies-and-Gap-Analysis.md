# Intraday Strategies from the Rani Trading Docs, and Gap Analysis of VwapScanner.Android

Prepared 9 November 2026. Sources: the three Hindi PDFs in `RaniTradingDocs` (translated to English here) and the code in `VwapScanner.Android`.

## 1. Scope and reading notes

| Item | Detail |
|---|---|
| Documents | (A) *Zero to Trader 30 Days* (36 pp), (B) *Zero to Trader Complete Trading Guide, Hindi, 2026 edition* (201 pp, Satu Stock Learner), (C) *Candlestick Mastery Blueprint* (67 pp, Satu E-Book Store) |
| Format | All three are scanned page images with no text layer, so I read them visually and translated. Page numbers below are the printed page numbers. |
| Read in full | (A) all pages. (B) chapters 10-20 and 22 (breakout/retest, volume, supply zone, liquidity sweep, chart patterns, indicators incl. VWAP, BOS/CHoCH, SMC, entry/exit, risk, strategies, journal/backtesting/paper trading/trading plan). |
| Read in part | (B) chapters 1-9 and 21 (market basics, S/R, trend, psychology) were skimmed only; (A) covers the same ground. (C) contents page plus sample chapters (Hammer, Morning/Evening Star, Spinning Top, Shooting Star, Dark Cloud Cover, Bearish Kicker). The other pattern chapters follow the same template (what it is, how to spot it, entry / stop / target, mistakes to avoid). |
| App | Strategy, exit evaluator, portfolio optimizer, scanner, settings definitions, coordinator and Kite client read directly; the remaining files (UI pages, Android services) were searched by keyword for features such as RSI, short selling, order placement and daily limits. |
| Nature of the docs | Beginner education material. They give conventions (RSI 30/70, EMA 9/21, 1:2 reward-risk) and say themselves that nothing guarantees profit. They contain no tested statistics. Everything in section 3 is a candidate to backtest, not a proven edge. |

## 2. What the three documents teach

| Doc | Content that matters for intraday | Pages |
|---|---|---|
| A. 30 Days | Day-by-day plan: candlesticks, support/resistance, market structure (HH/HL/LH/LL), breakout and fake breakout, trendline, demand/supply, multi-timeframe, RSI, EMA 9/21, combo strategy, entry setup, stop loss, risk, backtesting, psychology, over-trading, paper trading, first trade, final system. | 6-35 |
| B. Complete Guide | VWAP (what it is, as support/resistance, practical checklist, full example); breakout / breakdown / retest; price-volume analysis; liquidity and liquidity sweep; chart patterns; MA, RSI, MACD; BOS / CHoCH; SMC (order block, FVG, displacement, mitigation); entry, exit, target, stop loss; position sizing; scalping vs intraday vs swing; journal, backtesting, paper trading, trading plan. | 70-81, 86-99, 100-137, 139-173, 180-199 |
| C. Candlestick Blueprint | 30 candlestick patterns, each with the same trade template: wait for a confirmation candle, stop beyond the pattern, target at the next level or 1:2 to 1:3. Time frame guide: scalping 1M-5M, intraday 5M-15M, swing 1H-daily. | 3-67 |

Rules that every document repeats, whatever the strategy:

| # | Universal rule | Where |
|---|---|---|
| 1 | Risk at most 1-2% of capital per trade. | A Day 3, Day 20; B p.147-153 |
| 2 | Position size = maximum risk / (entry minus stop). | A Day 19-20; B p.148, 153 |
| 3 | Every trade has a stop loss, placed where the trade idea is proven wrong, not at an arbitrary rupee distance. Never remove or widen it. | A Day 19, Day 27; B p.142-144 |
| 4 | Minimum reward-risk of 1:2. | A Day 17-20; B p.143, 146 |
| 5 | Trade with the trend. Higher time frame gives direction, lower time frame gives entry (example: 1H trend, 15M entry). | A Day 8, Day 13; B p.172 |
| 6 | Never enter on one signal. Wait for confirmation (confirmation candle, volume, level). | A Day 17-18, Day 30 extra; B p.73-81, 128 |
| 7 | Quality over quantity: about 1-3 good setups a day, no revenge trading, a daily loss limit (the guide's example is 2% of capital). | A Day 23-24; B p.178-179 |
| 8 | Book profit by plan: part at the first target, trail the rest. | A Day 25 |
| 9 | Journal every trade, review weekly, backtest the rules (the guide warns that 5-10 trades prove nothing), then paper trade before real money. | A Day 21, 28; B p.180-199 |

## 3. Intraday strategies extracted from the documents

"Basis" says whether the rule is explicit in the docs or whether I filled in parameters. For VWAP the docs describe how to read the line and give a checklist, but they do not give VWAP-specific stop or target rules. There, the general rules from the table above apply and the stop placement in S1 and S2 is my suggestion.

| # | Strategy | Direction | Setup and entry trigger | Stop loss | Target | Best time frame | Basis | Source |
|---|---|---|---|---|---|---|---|---|
| S1 | **VWAP trend pullback (bounce)** | Long | Price above VWAP and making HH/HL. Price pulls back to the VWAP area, buyers react there (bullish candle), volume at or above normal. Enter above the confirmation candle's high. | Below the pullback low or just below VWAP (suggested) | Next resistance or prior high; at least 1:2 | 5M-15M | Docs principle + suggested stop | B p.118-120 |
| S2 | **VWAP rejection** | Short | Price below VWAP making LH/LL. Price rallies up to VWAP, sellers reject it (bearish candle). Enter below the confirmation candle's low. | Above the rejection high or just above VWAP (suggested) | Next support or prior low; at least 1:2 | 5M-15M | Docs principle + suggested stop | B p.118-120 |
| S3 | **VWAP mean reversion** (what the app does) | Long | Close at least 1 ATR below VWAP, candle closes up in the upper half of its range, volume at or above the 20-bar average, bar turnover at least INR 5 lakh. | 1 ATR below entry | 1.5 x risk | 1M | App design, not a docs strategy. Nearest docs idea: oversold at a level plus confirmation (A Day 15, 17). The docs also warn that price below VWAP with LH/LL signals weakness (B p.119). | `VwapStrategy.cs` |
| S4 | **Breakout (or breakdown) and retest** | Both | A level touched several times. A strong candle closes beyond it on higher volume. Price returns to the level, holds, and a confirmation appears: enter there. | Beyond the level that was broken (guide example: entry 505, stop 485) | Next level; the guide example is 545 for 1:2 | 5M-15M | Docs explicit | A Day 9-10; B p.70-75, 146 |
| S5 | **False breakout / liquidity sweep reversal** | Both | Price takes out a prior high or low, then closes back inside the range, then structure changes (lower high after a sweep up, higher low after a sweep down). Do not buy the spike. | Beyond the sweep extreme | Opposite side of the range or next level | 5M-15M | Docs explicit (checklist of 4 steps) | A Day 10; B p.87-93 |
| S6 | **Support/Resistance + RSI + EMA 9/21 combo** (the guide's signature setup) | Both | Long: price at support, RSI(14) at or under 30 and turning up, price above the 9 EMA with 9 crossing above 21, bullish candle. Short is the mirror at resistance with RSI above 70. All conditions together; the more present, the higher the probability. | Below support / swing low (above resistance for shorts) | Next opposite level; reward-risk 1:2 or better | Intraday 15M; swing 1H-4H | Docs explicit | A Day 15-18, p.22-23 |
| S7 | **Demand/supply zone with multi-timeframe** | Both | 1H chart gives the trend (uptrend means buy only). Mark the demand (or supply) zone on 15M. Enter when a confirmation candle forms inside the zone. | Outside the zone | Next zone or level | 1H trend, 15M entry | Docs explicit | A Day 12-13; B p.82-86 |
| S8 | **Candlestick reversal at a level** | Both | A reversal pattern (Hammer, Inverted Hammer, Morning Star, Engulfing, Piercing, Tweezer, Pin Bar, Kicker; mirror patterns for shorts) at support or resistance, after a trend, ideally on high volume. Wait for the next candle to confirm, then enter above its high (long) or below its low (short). Skip in sideways markets. | Beyond the pattern's low (long) or high (short) | Next level, or 1:2 to 1:3 | 5M-15M | Docs explicit | C p.6-67 |
| S9 | **Smart-money sequence (SMC)** | Both | Liquidity sweep, then CHoCH / BOS, then a strong displacement move, then price returns to the order block or fair value gap, then a reaction. Enter on the retest reaction. The author states this is a framework, not a fixed rule. | Beyond the order block / sweep | Prior swing or next liquidity pool | 5M-15M | Docs explicit but subjective | B p.121-138 |
| S10 | **Chart-pattern breakout** | Both | Flag or pennant after a strong move, ascending / descending / symmetrical triangle, double top / bottom, head and shoulders. Check where the pattern formed, the trend, nearby S/R, whether the break happened, and volume. | Beyond the pattern | Pattern height or next level | 5M-15M | Docs explicit | B p.94-99 |
| S11 | **RSI divergence and MACD momentum** | Filters | Price makes a new high while RSI makes a lower high (bearish divergence), or the reverse. MACD line / signal crossover and histogram growth confirm momentum. Never traded alone. | n/a | n/a | any | Docs explicit | B p.105-115 |
| S12 | **Scalping** | Both | Short-lived moves on 1M-5M charts, tight but logical stop, liquid stocks, fast execution. The docs list the risks: over-trading, charges, false signals, emotional pressure. No specific setup is given. | Tight | Small | 1M-5M | Docs principle only | B p.164-165 |

The VWAP practical checklist from the guide (p.119), which S1 to S3 should all pass:

| Question | S1 / S2 expectation |
|---|---|
| Is price above or below VWAP? | Above for longs, below for shorts |
| What is the trend? | With the trade direction |
| What is the market structure? | HH/HL for longs, LH/LL for shorts |
| How is volume? | Above normal on the reaction candle |
| Where are support and resistance? | Room to the next level of at least 2x the risk |

## 4. What the app does today

| Area | Current behaviour (from code) |
|---|---|
| Strategy | One strategy only, long only: VWAP mean reversion (S3). Signals come from 1-minute candles. Indicators: session VWAP (high+low+close)/3 weighted by volume, EMA 5/13, ATR 10, 3-bar momentum, relative volume vs the prior 20 bars. |
| Entry conditions | Close at least 1.0 ATR below VWAP; close above the previous close and in the upper half of its own range; relative volume at least 1.0; bar turnover at least INR 5 lakh; target distance at least 15 bps; decision time between 09:30 and 15:00. |
| Market filter | A signal is skipped when NIFTY 50 is in a 1-minute downtrend (EMA5 below EMA13 and 3-bar momentum negative). The same downtrend test exists for individual stocks but is only applied to NIFTY. |
| Stop / target | Stop 1 ATR below the signal close; target 1.5 x that distance (reward-risk 1.5). |
| Sizing | Risk per trade 0.15% of capital, portfolio stop risk 0.45%, up to 3 positions, 25% per stock, 1% of the signal bar's volume, Zerodha fee model and 5 bps slippage per side. Risk budget split by score. |
| Exits | Exit Monitor every 15 s on live 1-minute candles: target, stop, close-out at 15:15; Reversal Guard (arms at 50% of the way to target, then breakeven stop, 40% give-back, 3 lower closes below VWAP, or falling trend projection); Stop Reprieve (waits up to 15 min after a stop touch if price is back above it and VWAP is at least one stop-distance above). |
| Execution | Read-only. It never places orders. Notifications, grids, CSV export, daily reminder. |
| Process | Today's rows saved in `history.json`; two CSV files at the close; replay of a past date/time via `Scan:Date` and `Scan:At`. |
| Universe | NIFTY 50 / 100 / 200 / 500 lists (default NIFTY 100). |

## 5. Gap analysis

Status key: **Covered**, **Partial**, **Missing**, **By design**. Priority: H, M, L (my judgment of value against effort, explained in section 7).

### 5.1 Signals and strategies

| # | Capability in the docs | App today | Status | Priority |
|---|---|---|---|---|
| 1 | Session VWAP line | Computed from 1-minute candles | Covered | - |
| 2 | VWAP mean reversion (S3) | Implemented (it is the whole app) | Covered (not in docs) | - |
| 3 | VWAP trend pullback long (S1) | Not present | Missing | H |
| 4 | VWAP rejection short (S2) | Not present; the app is long only | Missing | H |
| 5 | VWAP checklist: price vs VWAP | Yes, measured as stretch in ATRs | Covered | - |
| 6 | VWAP checklist: trend | Index-level only (NIFTY 1-minute). The stock's own trend is not checked even though the code can compute it. | Partial | H |
| 7 | VWAP checklist: market structure (HH/HL, LH/LL) | No swing detection | Missing | M |
| 8 | VWAP checklist: volume | Relative volume and turnover filters | Covered (the default 1.0x only means "at least average") | - |
| 9 | VWAP checklist: support/resistance | No levels at all | Missing | H |
| 10 | Multi-timeframe (1H trend, 15M entry) | 1-minute only for signal and exit; no higher-time-frame view | Missing | H |
| 11 | Support / resistance, prior-day high/low, trendlines | None | Missing | H |
| 12 | Demand / supply zones (S7) | None | Missing | M |
| 13 | Breakout / breakdown + retest (S4) | None | Missing | H |
| 14 | False breakout / liquidity sweep (S5) | None | Missing | M |
| 15 | EMA 9/21 trend and crossover (S6) | EMA 5/13 used internally, only for the NIFTY filter | Partial | M |
| 16 | RSI (S6, S11) | None | Missing | M |
| 17 | MACD (S11) | None | Missing | L |
| 18 | Candlestick patterns (S8) | Only a generic "closed up in the upper half" rule; no named patterns | Partial | M |
| 19 | Chart patterns (S10) | None | Missing | L |
| 20 | BOS / CHoCH / order block / FVG (S9) | None | Missing | L |
| 21 | Short side (all bearish setups) | Long only | Missing | M |
| 22 | Liquid-stock selection | Turnover filter, 1% volume participation cap, NIFTY lists; no bid-ask spread check | Partial | L |
| 23 | Trading window avoiding the open and close | 09:30 to 15:00 entries, 15:15 close-out | Covered | - |

### 5.2 Entry, exit and trade management

| # | Capability in the docs | App today | Status | Priority |
|---|---|---|---|---|
| 24 | Enter after a confirmation candle | Buys at the close of the signal candle itself. That candle is a turn-up candle, but there is no further confirmation. | Partial | M |
| 25 | Stop at the level that invalidates the idea (support, swing low) | Fixed 1 ATR below entry (volatility-based, not structure-based) | Partial | M |
| 26 | Target at the next S/R, reward-risk at least 1:2 | Fixed 1.5 x risk, with no level check | Partial. Default sits below the docs' 1:2 minimum. | H |
| 27 | Partial profit at target 1, trail the rest | All-or-nothing exit. The Reversal Guard (breakeven stop and fade exits) does part of this job. | Partial | M |
| 28 | Trailing stop | Stop moves to breakeven plus 5 bps once armed; it does not trail upward after that | Partial | M |
| 29 | Never remove or widen the stop | Stop Reprieve waits up to 15 minutes after a stop touch and sells on a later close at or below the stop. The exit can then fill below the planned stop, so the loss can exceed the planned risk. | Deviation from the docs. It is a deliberate design choice and needs data to justify it. | H |
| 30 | Square off before close | 15:15 close-out, 15-second monitoring | Covered | - |
| 31 | Count charges and slippage | Zerodha fee model and 5 bps per side in sizing and net gain | Covered (ahead of docs) | - |

### 5.3 Risk and discipline

| # | Capability in the docs | App today | Status | Priority |
|---|---|---|---|---|
| 32 | Risk 1-2% per trade | 0.15% per trade, 0.45% total | Covered (more conservative) | - |
| 33 | Position size = risk / per-share risk | Same formula, plus fees, slippage, allocation and volume caps | Covered (ahead of docs) | - |
| 34 | Limit the number of trades per day (1-3 quality trades) | Only a cap of 3 open positions. No setting limits trades per day, and money returned by an exit is free to buy again on later signals. | Missing | H |
| 35 | Daily loss limit, stop trading when hit | No such setting. Realised losses only shrink the free capital. | Missing | H |
| 36 | No revenge trading / cool-down after a loss | None | Missing | M |
| 37 | Pre-trade checklist (all answers yes) | The signal gate is an automated checklist of six conditions | Covered for technical items only | - |

### 5.4 Process: journal, backtest, paper trade, review

| # | Capability in the docs | App today | Status | Priority |
|---|---|---|---|---|
| 38 | Journal: entry, stop, target, exit, P&L, reason | Grids and CSV carry entry, stop, target, exit price, net gain, exit reason, peak. No place for the trader's own note, mistake or lesson. | Partial | L |
| 39 | Performance review: trades, wins, losses, average win/loss, repeat mistakes, weekly review | Totals bar shows invested amount and gross P&L only. No win rate, average win/loss, expectancy or drawdown, and `history.json` holds only the current day. | Missing | H |
| 40 | Backtesting over many trades, rules fixed | Single-day replay through `Scan:Date` / `Scan:At`. No multi-day run, no aggregate result. | Partial | H |
| 41 | Paper trading before real money | The app never places orders, so it is a paper trader in practice. It has no scorecard against the guide's graduation criteria (about 55% win rate, consistent profit over months). | Covered de facto; needs scoring | M |
| 42 | Daily routine (pre-market analysis, plan, review) | 09:10 reminder notification; no pre-market checklist or end-of-day review | Partial | L |
| 43 | Placing the entry / stop / target orders | Not done | By design (read-only) | - |

## 6. Where the app is ahead of the documents

| Area | Detail |
|---|---|
| Cost realism | The docs mention slippage and charges only in words. The app models Zerodha intraday fees and slippage in sizing, net gain and the 15 bps minimum target. |
| Portfolio sizing | Risk budget split across several signals by score, with allocation and volume-participation caps. The docs size one trade at a time. |
| Live exit logic | A 15-second Exit Monitor that replays candles deterministically, so a restart gives the same answer. The docs describe exits only as plans. |
| Operations | Foreground service, daily reminder, notifications, CSV at close, restart recovery. |

## 7. Recommended order of work

Effort is my estimate: S = a day or less, M = a few days, L = a week or more. "Where" names the code that would change.

| Step | Change | Gap rows | Where | Effort |
|---|---|---|---|---|
| 1 | Add a daily trade cap and a daily loss lock (new settings, checked before buying in each scan) | 34, 35, 36 | `ScanCoordinator.cs`, `SettingDef.cs`, `appsettings.json` | S |
| 2 | Apply the existing stock-level downtrend test to each stock, not only NIFTY, behind a setting | 6 | `VwapStrategy.cs` (`Candidate`) | S |
| 3 | Performance scorecard: trades, win rate, average win/loss, realised R, expectancy, by day and week; keep a rolling multi-day history | 38, 39, 41 | `ScanCoordinator.cs`, `GridCsv.cs`, new page | M |
| 4 | Multi-day backtest harness using the same strategy and `ExitEvaluator` on Kite historical candles. Use it to test the reward-risk setting (1.5 vs 2) and Stop Reprieve on versus off. | 26, 29, 40 | new project file reusing `VwapState`, `ExitEvaluator` | M |
| 5 | Build 5-minute and 15-minute candles by aggregating the 1-minute candles already downloaded (no extra API calls), then add a higher-time-frame trend filter | 10 | `Scanner.cs`, `VwapStrategy.cs` | M |
| 6 | Add prior-day high/low, day high/low and simple swing levels. Use them for S/R-aware targets and stops, and as a filter ("room to next level at least 2x risk"). | 9, 11, 25, 26 | new `Levels.cs`, `VwapStrategy.cs`, `ExitEvaluator.cs` | M |
| 7 | Second VWAP strategy: trend pullback (S1) and rejection (S2), including a short side in `Portfolio.cs` and `ExitEvaluator.cs` | 3, 4, 7, 21 | new strategy class, `Portfolio.cs`, `ExitEvaluator.cs` | L |
| 8 | Breakout + retest (S4) and false-breakout (S5) on the new levels | 13, 14 | new strategy class | M |
| 9 | EMA 9/21, RSI, and a small candlestick library (engulfing, hammer, morning/evening star, pin bar) as optional confirmations; optional confirmation-candle entry mode | 15, 16, 18, 24 | `VwapState`, new `Patterns.cs` | M |
| 10 | Partial booking at target 1 with a trailing runner | 27, 28 | `ExitEvaluator.cs`, `Portfolio.cs` | M |
| 11 | SMC (sweep, CHoCH, order block, FVG), chart patterns, MACD | 17, 19, 20 | new modules | L, and the most subjective; do last and only if backtests justify it |

## 8. Cautions

| Point | Detail |
|---|---|
| Not advice | This is a comparison of educational material against a code base, not investment or trading advice. |
| No edge shown | The documents give no win rates or results, and say that no setup guarantees profit. Each strategy in section 3 needs backtesting on the app's own data before it influences a decision. |
| Mean reversion vs trend | S3 (the app) buys weakness below VWAP. The documents' VWAP examples are trend-following (S1, S2). The two can disagree on the same chart, so test them separately before combining. |
| Reading accuracy | The PDFs are images read visually. Figures were read from page images, so a misread of small print is possible; check any number you plan to hard-code against the page cited. |
