# VwapScanner

Web API only, extracted from GptTrading's `ScalpVwap` strategy. One endpoint, configured entirely from `appsettings.json`.
It reads the configured stocks (nifty50 by default) through the official Kite Connect .NET client, keeps only the stocks that
signal on VWAP, and optimizes those signals into a position-sized portfolio. Read-only: it never places orders.

## Run
`run.cmd`, `dotnet run -c Release`, or F5 in Visual Studio. It listens on **http://127.0.0.1:9079** (`Urls` in `appsettings.json`;
this is also the Redirect URL to register for your Kite app). GptTrading uses the same port for its Zerodha login callback, so run one at a time.

## The endpoint
`GET /` - scans and optimizes with the current settings and returns JSON:

    { strategy, universe, date, asOf, scanned, failed, benchmarkFilter, message,
      signals:   [ symbol, instrumentId, barTime, close, vwap, stretchAtr, atr, relativeVolume, score, stop, target, barVolume ],
      portfolio: { capital, invested, cash, riskBudgetInr, totalRiskAtStopInr, totalNetGainAtVwapInr, positions:[...], notes:[...] } }

Not connected yet? The endpoint answers 401 with a `loginUrl`. Open it, log in to Zerodha, and Zerodha sends the browser back to `/`, which finishes the
login, saves `.kite_token.json` and shows the result. Zerodha sessions end at 06:00 IST, so the first call of each day asks for a login again.
A scan of nifty50 takes about 20 seconds (Kite allows 3 history requests a second); scans run one at a time.

## Settings (`appsettings.json`, re-read on every request)
| Section | Setting | Meaning |
|---|---|---|
| `Urls` | | address to listen on (restart to change) |
| `Zerodha` | `ApiKey`, `ApiSecret` | keep them in `appsettings.Local.json` (not committed) or env `Zerodha__ApiKey` / `Zerodha__ApiSecret` |
| | `TokenFile` | where the daily token is saved (default `.kite_token.json`) |
| `Scan` | `Universe` | `nifty50` (default), `nifty100`, `nifty200`, `nifty500` (files in `universes\`) |
| | `Symbols` | explicit list; when not empty it replaces the universe |
| | `Lookback` | also report signals from the last N one-minute candles |
| | `UseBenchmark` | skip signals while NIFTY 50 is in a downtrend |
| | `Date`, `At` | replay a past trading day (`yyyy-MM-dd`) and time (`HH:mm`); empty = now |
| `Scan:Strategy` | `MinStretchAtr`, `MinRelativeVolume`, `MinBarTurnover`, `MinTargetBps`, `StopAtrMultiple`, `RewardRiskRatio`, `EntryStart`, `LastEntry` | the ScalpVwap thresholds (GptTrading defaults) |
| `Portfolio` | `Capital`, `RiskPerTradePercent`, `MaxPortfolioStopRiskPercent`, `MaxAssetAllocationPercent`, `MaxPositions`, `MaxParticipationPercent`, `SlippageBpsPerSide` | risk limits (GptTrading defaults) |
| `Portfolio:Fees` | brokerage, exchange, SEBI, STT, stamp, GST | Zerodha equity intraday charges; verify against your contract note |

## Console and CSV output
Every run is also printed in the app's console (signals table and portfolio table) and saved as `output\vwap-signals-<date>-<time>.csv` and `output\vwap-portfolio-<date>-<time>.csv`
(each row carries the Zerodha instrument id). If there is no data, no file is written: the signals CSV only when there is at least one signal, the portfolio CSV only when at least one position is allocated. Switch either output off or change the folder in the `Output` section of `appsettings.json`.

## Business logic
**Signal (long mean reversion, 1-minute candles):** the latest completed candle closes at least `MinStretchAtr` ATRs below the session VWAP, closes above the previous close
and in the upper half of its range, with relative volume and turnover above the minimums, a target (`RewardRiskRatio` x `StopAtrMultiple` ATR) of at least `MinTargetBps`, a
decision time between `EntryStart` and `LastEntry`, and NIFTY 50 not in a downtrend. Indicators: EMA 5/13, ATR 10, 3-bar momentum, session VWAP, 20-bar relative volume
(same arithmetic as GptTrading). Stop = close - 1 ATR; the strategy's own exit is the return to VWAP.

**Portfolio optimization:** the best `MaxPositions` signals by score share the portfolio stop-risk budget (`Capital` x `MaxPortfolioStopRiskPercent`) in proportion to their
score; a position that reaches its own cap (per-trade risk, asset allocation, volume participation) hands its unused share to the others. Quantities are whole shares sized
with GptTrading's loss-at-stop formula (price move + both legs' fees + sell slippage), kept inside the cash and topped up share by share in score order. Per stock: quantity,
notional, weight, risk at stop, net gain if price returns to VWAP (a scenario, not a forecast). No sector limit (Zerodha's instrument list has no sector). Gap checks
(0.3 %) are left to execution.
