namespace VwapScanner;

/// <summary>"Zerodha" section of appsettings.json (secrets belong in appsettings.Local.json or environment variables Zerodha__ApiKey / Zerodha__ApiSecret).</summary>
public sealed class ZerodhaSettings
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    /// <summary>Where the daily access token is saved; relative paths are under the application folder.</summary>
    public string TokenFile { get; set; } = ".kite_token.json";
}

/// <summary>"Scan" section: which stocks, which candle, and the ScalpVwap thresholds. Re-read on every request, so edits apply without a restart.</summary>
public sealed class ScanSettings
{
    public string Universe { get; set; } = "nifty50";

    /// <summary>Explicit symbols; when not empty they replace the universe.</summary>
    public List<string> Symbols { get; set; } = [];

    /// <summary>Also report signals from the last N candles.</summary>
    public int Lookback { get; set; } = 1;
    public bool UseBenchmark { get; set; } = true;

    /// <summary>yyyy-MM-dd to replay a past trading day; empty means today.</summary>
    public string? Date { get; set; }

    /// <summary>HH:mm to evaluate as of that time; empty means now.</summary>
    public string? At { get; set; }
    public VwapOptions Strategy { get; set; } = new();

    /// <summary>Which strategy runs: ScalpVwap (thresholds in <see cref="Strategy"/>) or VwapPullback (thresholds in <see cref="Pullback"/>). See StrategyCatalog.</summary>
    public string StrategyName { get; set; } = StrategyCatalog.VwapPullback;

    public PullbackOptions Pullback { get; set; } = new();

    /// <summary>Periods of the EMA, RSI and SuperTrend values shown on every signal.</summary>
    public IndicatorOptions Indicators { get; set; } = new();

    /// <summary>Optional RSI / SuperTrend / EMA filters, one set per strategy (all off by default).</summary>
    public SignalFilterOptions ScalpFilters { get; set; } = SignalFilterOptions.ForScalp();

    public SignalFilterOptions PullbackFilters { get; set; } = SignalFilterOptions.ForPullback();
}
