using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace VwapScanner;

/// <summary>
/// The Android replacement for the ASP.NET host's configuration: appsettings.json from the APK, with the user's own values on top.
/// The API key and secret are typed in the app and kept in Android's SecureStorage (never in the APK); every other editable setting
/// (see <see cref="SettingDef.All"/>) is kept in Preferences. The same IConfiguration is handed to ApiState, which re-reads it on every scan,
/// so edits apply immediately.
/// </summary>
public sealed class AppConfig
{
    private const string KeyApi = "zerodha_api_key", KeySecret = "zerodha_api_secret", OverridePrefix = "cfg:";
    private readonly Dictionary<string, string?> defaults = [];
    private bool secretsLoaded;
    private string defaultApiKey = "", defaultApiSecret = "";

    public IConfigurationRoot Root { get; }

    /// <summary>Increases whenever the credentials change, so the app knows to look for a saved session again.</summary>
    public int Revision { get; private set; }

    private AppConfig(IConfigurationRoot root) => Root = root;

    public static AppConfig Load()
    {
        var builder = new ConfigurationBuilder();
        using var stream = Task.Run(() => FileSystem.OpenAppPackageFileAsync("appsettings.json")).GetAwaiter().GetResult();
        builder.AddJsonStream(stream);
        // Optional, git-ignored Resources\Raw\appsettings.Local.json (same as the web API's): supplies the default API key and secret.
        Stream? local = null;
        try
        {
            local = Task.Run(() => FileSystem.OpenAppPackageFileAsync("appsettings.Local.json")).GetAwaiter().GetResult();
            builder.AddJsonStream(local);
        }
        catch (Exception) { local?.Dispose(); local = null; /* no local file: the user types the key in Settings */ }
        var root = builder.Build();
        local?.Dispose();
        var config = new AppConfig(root) { defaultApiKey = root["Zerodha:ApiKey"] ?? "", defaultApiSecret = root["Zerodha:ApiSecret"] ?? "" };
        foreach (var def in SettingDef.All)
        {
            config.defaults[def.Key] = root[def.Key];
            var saved = OverridePrefix + def.Key;
            if (Preferences.Default.ContainsKey(saved))
            {
                // A stored value that no longer passes validation (older version, edited by hand) is dropped in favour of the default.
                if (def.TryNormalize(Preferences.Default.Get(saved, ""), out var ok, out _)) root[def.Key] = ok;
                else Preferences.Default.Remove(saved);
            }
        }
        return config;
    }

    public string ApiKey => Root["Zerodha:ApiKey"] ?? "";
    public string ApiSecret => Root["Zerodha:ApiSecret"] ?? "";
    public string RedirectUrl => Root["Zerodha:RedirectUrl"] ?? "http://127.0.0.1:9079/";
    public string Value(string key) => Root[key] ?? "";

    public int AutoRefreshMinutes => int.TryParse(Root["App:AutoRefreshMinutes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? Math.Clamp(m, 1, 120) : 5;
    /// <summary>Auto-refresh starts by itself when the app opens (signed in, market day, before 15:35 IST).</summary>
    public bool AutoStart => !bool.TryParse(Root["App:AutoStart"], out var a) || a;
    /// <summary>Daily reminder time (IST, weekdays); null when the setting is empty (no reminder).</summary>
    public TimeOnly? ReminderTime => TimeOnly.TryParseExact(Root["App:ReminderTime"], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
    public bool ReminderSound => !bool.TryParse(Root["App:ReminderSound"], out var s) || s;
    public bool ReminderVibrate => !bool.TryParse(Root["App:ReminderVibrate"], out var v) || v;
    /// <summary>Save the day's CSV files automatically when the market closes.</summary>
    public bool SaveCsvAtClose => !bool.TryParse(Root["App:SaveCsvAtClose"], out var c) || c;
    public bool Notify => !bool.TryParse(Root["App:Notify"], out var n) || n;
    /// <summary>True when a past day is being replayed, so the market-hours checks of auto-refresh do not apply.</summary>
    public bool Replaying => !string.IsNullOrWhiteSpace(Root["Scan:Date"]);

    public async Task LoadSecretsAsync()
    {
        if (secretsLoaded) return;
        try
        {
            // Values typed in Settings win over the bundled defaults.
            if (await SecureStorage.Default.GetAsync(KeyApi) is { Length: > 0 } key) Root["Zerodha:ApiKey"] = key;
            if (await SecureStorage.Default.GetAsync(KeySecret) is { Length: > 0 } secret) Root["Zerodha:ApiSecret"] = secret;
        }
        catch (Exception) { /* a broken keystore just means the user types the key again */ }
        secretsLoaded = true;
    }

    /// <summary>Empty (use the bundled default) or 6-64 letters and digits.</summary>
    public static bool IsValidCredential(string value) => value.Length == 0 || (value.Length is >= 6 and <= 64 && value.All(char.IsAsciiLetterOrDigit));

    public async Task SaveCredentialsAsync(string apiKey, string apiSecret)
    {
        apiKey = apiKey.Trim(); apiSecret = apiSecret.Trim();
        if (!IsValidCredential(apiKey) || !IsValidCredential(apiSecret) || (apiKey.Length == 0) != (apiSecret.Length == 0))
            throw new ArgumentException("API key and secret must both be given, using letters and digits only.");
        if (apiKey.Length == 0) SecureStorage.Default.Remove(KeyApi); else await SecureStorage.Default.SetAsync(KeyApi, apiKey);
        if (apiSecret.Length == 0) SecureStorage.Default.Remove(KeySecret); else await SecureStorage.Default.SetAsync(KeySecret, apiSecret);
        Root["Zerodha:ApiKey"] = apiKey.Length > 0 ? apiKey : defaultApiKey;
        Root["Zerodha:ApiSecret"] = apiSecret.Length > 0 ? apiSecret : defaultApiSecret;
        secretsLoaded = true;
        Revision++;
    }

    /// <summary>Saves an already normalized value (see <see cref="SettingDef.TryNormalize"/>).</summary>
    public void Set(SettingDef def, string value)
    {
        Preferences.Default.Set(OverridePrefix + def.Key, value);
        Root[def.Key] = value;
    }

    /// <summary>Back to the values shipped in appsettings.json (the API key and secret are kept).</summary>
    public void ResetSettings()
    {
        foreach (var def in SettingDef.All)
        {
            Preferences.Default.Remove(OverridePrefix + def.Key);
            Root[def.Key] = defaults.GetValueOrDefault(def.Key);
        }
    }
}
