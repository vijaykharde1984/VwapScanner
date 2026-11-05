using System.Globalization;
using System.Text;

namespace VwapScanner;

/// <summary>Writes each run to the console and to CSV files (vwap-signals-*.csv and vwap-portfolio-*.csv), as configured in the "Output" section.</summary>
public static class Reporter
{
    private static string N(decimal x) => x.ToString("0.####", CultureInfo.InvariantCulture);

    public static void Report(OutputSettings o, ScanResult r, PortfolioPlan plan, string contentRoot)
    {
        if (o.Console) Print(r, plan);
        // No data, no file: a CSV is written only when it would have rows.
        var writeSignals = o.Csv && r.Signals.Count > 0;
        var writePortfolio = o.Csv && plan.Positions.Count > 0;
        if (!writeSignals && !writePortfolio)
        {
            if (o.Console && o.Csv) Console.WriteLine("No signals, so no CSV file was written.");
            return;
        }
        try
        {
            var folder = Path.IsPathRooted(o.Folder) ? o.Folder : Path.Combine(contentRoot, o.Folder);
            Directory.CreateDirectory(folder);
            var stamp = $"{r.Date.Replace("-", "")}-{r.AsOf.Replace(":", "")}";
            var signals = Path.Combine(folder, $"vwap-signals-{stamp}.csv");
            var portfolio = Path.Combine(folder, $"vwap-portfolio-{stamp}.csv");
            if (writeSignals) { File.WriteAllText(signals, SignalsCsv(r.Signals)); if (o.Console) Console.WriteLine($"Saved {signals}"); }
            if (writePortfolio) { File.WriteAllText(portfolio, PortfolioCsv(plan)); if (o.Console) Console.WriteLine($"Saved {portfolio}"); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not write the CSV files (is one open in Excel?): " + e.Message);
        }
    }

    public static string SignalsCsv(IEnumerable<VwapSignal> signals)
    {
        var csv = new StringBuilder("symbol,instrument_id,bar_time,close,vwap,stretch_atr,atr,relative_volume,score,stop,target\n");
        foreach (var s in signals)
            csv.Append(string.Join(',', s.Symbol, s.InstrumentId, s.BarTime.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                N(s.Close), N(s.Vwap), N(s.StretchAtr), N(s.Atr), N(s.RelativeVolume), N(s.Score), N(s.Stop), N(s.Target))).Append('\n');
        return csv.ToString();
    }

    public static string PortfolioCsv(PortfolioPlan plan)
    {
        var csv = new StringBuilder("symbol,instrument_id,bar_time,price,stop,target,vwap_exit,quantity,notional,weight_percent,risk_at_stop_inr,risk_share_percent,net_gain_at_vwap_inr,score\n");
        foreach (var p in plan.Positions)
            csv.Append(string.Join(',', p.Symbol, p.InstrumentId, p.BarTime.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                N(p.Price), N(p.Stop), N(p.Target), N(p.VwapExit), p.Quantity, N(p.Notional), N(p.WeightPercent), N(p.RiskAtStopInr),
                N(p.RiskSharePercent), N(p.NetGainAtVwapInr), N(p.Score))).Append('\n');
        return csv.ToString();
    }

    public static void Print(ScanResult r, PortfolioPlan plan)
    {
        Console.WriteLine();
        Console.WriteLine($"VWAP scan {r.Date} as of {r.AsOf} IST: {r.Signals.Count} signal(s) in {r.Scanned} stocks" +
            (r.Failed > 0 ? $" ({r.Failed} failed)" : "") + (r.BenchmarkFilter ? ", NIFTY 50 downtrend filter on" : ", benchmark filter off") + ".");
        if (r.Message is not null) Console.WriteLine(r.Message);
        if (r.Diagnostics is { } d)
            Console.WriteLine($"Candles checked: {d.Evaluated} (not ready {d.NotReady}, NIFTY downtrend {d.BenchmarkBlocked}). Passing each test alone: below VWAP {d.BelowVwap}, " +
                $"turn-up {d.TurnUp}, rel. volume {d.RelativeVolumeOk}, turnover {d.TurnoverOk}, target clears costs {d.TargetClearsCosts}, entry window {d.InEntryWindow}.");
        if (r.Signals.Count == 0) Console.WriteLine("No stock is stretched below VWAP with a turn-up right now.");
        else
        {
            Console.WriteLine($"{"Symbol",-14}{"InstrumentId",-13}{"Bar",-7}{"Close",10}{"VWAP",10}{"Stretch",9}{"RelVol",8}{"Stop",10}{"Target",10}{"Score",8}");
            foreach (var s in r.Signals)
                Console.WriteLine($"{s.Symbol,-14}{s.InstrumentId,-13}{s.BarTime:HH:mm}  {s.Close,10:0.00}{s.Vwap,10:0.00}{s.StretchAtr,9:0.00}{s.RelativeVolume,8:0.00}{s.Stop,10:0.00}{s.Target,10:0.00}{s.Score,8:0.00}");
        }

        Console.WriteLine();
        Console.WriteLine($"Portfolio: capital INR {plan.Capital:N0}, invested {plan.Invested:N0} ({(plan.Capital > 0 ? plan.Invested / plan.Capital * 100 : 0):0.0}%), cash {plan.Cash:N0}; " +
            $"risk at stops INR {plan.TotalRiskAtStopInr:N0} of {plan.RiskBudgetInr:N0}.");
        if (plan.Positions.Count > 0)
        {
            Console.WriteLine($"{"Symbol",-14}{"InstrumentId",-13}{"Qty",6}{"Price",10}{"Notional",11}{"Weight%",9}{"Stop",10}{"Risk",9}{"GainAtVWAP",12}");
            foreach (var p in plan.Positions)
                Console.WriteLine($"{p.Symbol,-14}{p.InstrumentId,-13}{p.Quantity,6}{p.Price,10:0.00}{p.Notional,11:N0}{p.WeightPercent,9:0.0}{p.Stop,10:0.00}{p.RiskAtStopInr,9:N0}{p.NetGainAtVwapInr,12:N0}");
            Console.WriteLine($"{"Total",-27}{"",6}{"",10}{plan.Invested,11:N0}{"",9}{"",10}{plan.TotalRiskAtStopInr,9:N0}{plan.TotalNetGainAtVwapInr,12:N0}");
        }
        foreach (var n in plan.Notes) Console.WriteLine("  " + n);
    }
}
