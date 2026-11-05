namespace VwapScanner;

/// <summary>Zerodha equity intraday (MIS) charges, the same profile as GptTrading's fees-zerodha.json. Verify against your contract note.</summary>
public sealed class FeeModel
{
    public decimal BrokeragePercent { get; set; } = 0.03m;
    public decimal BrokerageCapPerOrder { get; set; } = 20m;
    public decimal ExchangePercent { get; set; } = 0.00307m;
    public decimal SebiPercent { get; set; } = 0.0001m;
    public decimal IpftPercent { get; set; } = 0.0000001m;
    public decimal SellSttPercent { get; set; } = 0.025m;
    public decimal BuyStampPercent { get; set; } = 0.003m;
    public decimal GstPercent { get; set; } = 18m;

    public decimal Cost(decimal turnover, bool buy)
    {
        var brokerage = Math.Min(BrokerageCapPerOrder, turnover * BrokeragePercent / 100m);
        var exchange = turnover * ExchangePercent / 100m;
        var sebi = turnover * SebiPercent / 100m;
        var ipft = turnover * IpftPercent / 100m;
        var taxed = brokerage + exchange + sebi + ipft;
        return taxed + taxed * GstPercent / 100m + turnover * (buy ? BuyStampPercent : SellSttPercent) / 100m;
    }
}

/// <summary>Risk limits. The defaults are GptTrading's settings.json defaults.</summary>
public sealed class PortfolioOptions
{
    public decimal Capital { get; set; } = 100000m;
    public decimal RiskPerTradePercent { get; set; } = 0.15m;
    public decimal MaxPortfolioStopRiskPercent { get; set; } = 0.45m;
    public decimal MaxAssetAllocationPercent { get; set; } = 25m;
    public int MaxPositions { get; set; } = 3;
    public decimal MaxParticipationPercent { get; set; } = 1m;
    public decimal SlippageBpsPerSide { get; set; } = 5m;
    public FeeModel Fees { get; set; } = new();

    public void Validate()
    {
        if (Capital <= 0 || RiskPerTradePercent <= 0 || MaxPortfolioStopRiskPercent <= 0 || MaxPositions is < 1 or > 50 ||
            MaxAssetAllocationPercent is <= 0 or > 100 || MaxParticipationPercent <= 0 || SlippageBpsPerSide < 0)
            throw new ArgumentException("Portfolio limits must be positive (MaxPositions 1-50, MaxAssetAllocationPercent up to 100).");
    }
}

public sealed record PortfolioPosition(string Symbol, int InstrumentId, DateTimeOffset BarTime, decimal Price, decimal Stop, decimal Target,
    decimal VwapExit, int Quantity, decimal Notional, decimal WeightPercent, decimal RiskAtStopInr, decimal RiskSharePercent,
    decimal NetGainAtVwapInr, decimal Score);

public sealed record PortfolioPlan(decimal Capital, decimal Invested, decimal Cash, decimal RiskBudgetInr, decimal TotalRiskAtStopInr,
    decimal TotalNetGainAtVwapInr, IReadOnlyList<PortfolioPosition> Positions, IReadOnlyList<string> Notes);

/// <summary>
/// Portfolio optimization over the VWAP signals. Candidates are ranked by the ScalpVwap score. The portfolio's stop-risk budget is then split
/// across the best MaxPositions candidates in proportion to their score (water-filling: a position that hits its own cap - per-trade risk,
/// asset allocation or volume participation - passes its unused share to the others), converted to whole shares with GptTrading's loss-at-stop
/// formula (price move plus both legs' fees and sell slippage), kept inside the cash, and finally topped up one share at a time in score order.
/// It sizes risk; it does not forecast returns.
/// </summary>
public static class PortfolioOptimizer
{
    public static PortfolioPlan Optimize(IReadOnlyList<VwapSignal> signals, PortfolioOptions o)
    {
        var notes = new List<string>();
        var slip = o.SlippageBpsPerSide / 10000m;
        var budget = o.Capital * o.MaxPortfolioStopRiskPercent / 100m;
        var perTrade = o.Capital * o.RiskPerTradePercent / 100m;
        var notionalCap = o.Capital * o.MaxAssetAllocationPercent / 100m;

        decimal Loss(VwapSignal s, int q) => q <= 0 ? 0m :
            q * (s.Close - s.Stop * (1 - slip)) + o.Fees.Cost(q * s.Close, true) + o.Fees.Cost(q * s.Stop * (1 - slip), false);

        int MaxQty(VwapSignal s)
        {
            var upper = Math.Min(Math.Min(Math.Floor(notionalCap / s.Close), Math.Floor(o.Capital / s.Close)),
                s.BarVolume > 0 ? Math.Floor(s.BarVolume * o.MaxParticipationPercent / 100m) : decimal.MaxValue);
            int lo = 0, hi = (int)Math.Min(upper, int.MaxValue / 2);
            while (lo < hi) // largest quantity whose loss at the stop stays inside the per-trade risk
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (Loss(s, mid) <= perTrade) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        // One candidate per stock, best score first.
        var ranked = signals.GroupBy(s => s.Symbol).Select(g => g.OrderByDescending(x => x.BarTime).ThenByDescending(x => x.Score).First())
            .OrderByDescending(s => s.Score).ToList();
        var chosen = new List<(VwapSignal S, int Max)>();
        foreach (var s in ranked)
        {
            if (chosen.Count >= o.MaxPositions) break;
            var max = MaxQty(s);
            if (max >= 1 && s.Close > s.Stop) chosen.Add((s, max));
            else notes.Add($"{s.Symbol}: one share already breaks a limit, skipped.");
        }
        if (ranked.Count > chosen.Count && chosen.Count >= o.MaxPositions)
            notes.Add($"{ranked.Count - chosen.Count} lower-scoring signal(s) left out (MaxPositions = {o.MaxPositions}).");
        if (chosen.Count == 0)
        {
            notes.Insert(0, signals.Count == 0 ? "No VWAP signals, so nothing to allocate." : "No signal fits the limits.");
            return new(o.Capital, 0m, o.Capital, budget, 0m, 0m, [], notes);
        }

        // Water-filling of the risk budget in proportion to score.
        var cap = chosen.Select(c => Loss(c.S, c.Max)).ToArray();
        var risk = new decimal[chosen.Count];
        var active = Enumerable.Range(0, chosen.Count).ToList();
        var remaining = budget;
        while (active.Count > 0 && remaining > 0.005m)
        {
            var weight = active.Sum(i => chosen[i].S.Score);
            var capped = active.Where(i => risk[i] + remaining * chosen[i].S.Score / weight >= cap[i]).ToList();
            if (capped.Count == 0)
            {
                foreach (var i in active) risk[i] += remaining * chosen[i].S.Score / weight;
                remaining = 0m;
                break;
            }
            foreach (var i in capped) { remaining -= cap[i] - risk[i]; risk[i] = cap[i]; active.Remove(i); }
        }

        var qty = new int[chosen.Count];
        for (var i = 0; i < chosen.Count; i++)
        {
            int lo = 0, hi = chosen[i].Max;
            while (lo < hi) { var mid = lo + (hi - lo + 1) / 2; if (Loss(chosen[i].S, mid) <= risk[i]) lo = mid; else hi = mid - 1; }
            qty[i] = lo;
        }

        decimal Outlay(int i, int q) => q * chosen[i].S.Close + o.Fees.Cost(q * chosen[i].S.Close, true);
        decimal TotalOutlay() => Enumerable.Range(0, chosen.Count).Sum(i => Outlay(i, qty[i]));
        decimal TotalLoss() => Enumerable.Range(0, chosen.Count).Sum(i => Loss(chosen[i].S, qty[i]));

        if (TotalOutlay() > o.Capital)
        {
            var f = o.Capital / TotalOutlay();
            for (var i = 0; i < qty.Length; i++) qty[i] = (int)Math.Floor(qty[i] * f);
            while (TotalOutlay() > o.Capital && qty.Any(q => q > 0)) qty[Array.IndexOf(qty, qty.Max())]--;
            notes.Add("Cash was the binding limit, so quantities were scaled down to fit the capital.");
        }

        // Top-up: add single shares in score order while every limit still holds.
        for (var progress = true; progress;)
        {
            progress = false;
            for (var i = 0; i < chosen.Count; i++)
            {
                var q = qty[i] + 1;
                if (q > chosen[i].Max || Loss(chosen[i].S, q) > perTrade) continue;
                qty[i] = q;
                if (TotalLoss() <= budget && TotalOutlay() <= o.Capital) progress = true; else qty[i]--;
            }
        }

        var positions = new List<PortfolioPosition>();
        var totalRisk = TotalLoss();
        for (var i = 0; i < chosen.Count; i++)
        {
            if (qty[i] <= 0) { notes.Add($"{chosen[i].S.Symbol}: no whole share fits after splitting the risk budget."); continue; }
            var s = chosen[i].S;
            var loss = Loss(s, qty[i]);
            var exit = s.Vwap * (1 - slip);
            var gain = qty[i] * (exit - s.Close) - o.Fees.Cost(qty[i] * s.Close, true) - o.Fees.Cost(qty[i] * exit, false);
            positions.Add(new(s.Symbol, s.InstrumentId, s.BarTime, s.Close, s.Stop, s.Target, s.Vwap, qty[i], qty[i] * s.Close,
                qty[i] * s.Close / o.Capital * 100m, loss, totalRisk > 0 ? loss / totalRisk * 100m : 0m, gain, s.Score));
        }
        var invested = positions.Sum(p => p.Notional);
        notes.Add("Net gain at VWAP is what the position earns if price returns to VWAP (the strategy's exit), after fees and sell slippage - a scenario, not a forecast. Risk at stop is the loss if the stop fills.");
        notes.Add("No sector limit is applied (Zerodha's instrument list has no sector).");
        return new(o.Capital, invested, o.Capital - invested, budget, totalRisk, positions.Sum(p => p.NetGainAtVwapInr), positions, notes);
    }
}
