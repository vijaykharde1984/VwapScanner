using System.Globalization;

namespace VwapScanner.Mobile;

public partial class SettingsPage : ContentPage
{
    private static readonly Color ErrorColor = Color.FromArgb("#C62828");

    private readonly AppConfig config;
    private readonly INotifier notifier;
    private readonly IReminderScheduler reminders;
    private readonly ScanCoordinator coordinator;
    private readonly Dictionary<SettingDef, View> inputs = [];
    private readonly Dictionary<SettingDef, Label> errors = [];
    private bool loading;

    public SettingsPage(AppConfig config, INotifier notifier, IReminderScheduler reminders, ScanCoordinator coordinator)
    {
        InitializeComponent();
        this.config = config;
        this.notifier = notifier;
        this.reminders = reminders;
        this.coordinator = coordinator;
        BuildFields();
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        BuildLabel.Text = $"VWAP Scanner {AppInfo.VersionString} - {build} build";
    }

    private void BuildFields()
    {
        string? section = null;
        foreach (var def in SettingDef.All)
        {
            if (def.Section != section)
            {
                section = def.Section;
                Fields.Add(new Label { Text = section, FontAttributes = FontAttributes.Bold, FontSize = 16, Margin = new Thickness(0, 14, 0, 0) });
            }
            Fields.Add(new Label { Text = def.Label, FontAttributes = FontAttributes.Bold });
            var input = CreateInput(def);
            inputs[def] = input;
            Fields.Add(input);
            var error = new Label { TextColor = ErrorColor, FontSize = 12, IsVisible = false };
            errors[def] = error;
            Fields.Add(error);
            if (def.Hint.Length > 0) Fields.Add(new Label { Text = def.Hint, FontSize = 12 });
        }
    }

    private View CreateInput(SettingDef def)
    {
        switch (def.Kind)
        {
            case SettingKind.Bool:
                var sw = new Switch { HorizontalOptions = LayoutOptions.Start };
                sw.Toggled += (_, _) => Revalidate();
                return sw;
            case SettingKind.Choice:
                var picker = new Picker { ItemsSource = def.Options };
                picker.SelectedIndexChanged += (_, _) => Revalidate();
                return picker;
            case SettingKind.ShortTime or SettingKind.Time:
                var time = new TimeInput(optional: def.Kind == SettingKind.ShortTime);
                time.Changed += (_, _) => Revalidate();
                return time;
            case SettingKind.Date:
                var date = new DateInput();
                date.Changed += (_, _) => Revalidate();
                return date;
            default:
                var integer = def.Kind == SettingKind.Integer;
                var entry = new Entry
                {
                    Keyboard = Keyboard.Numeric,
                    MaxLength = integer ? 6 : 12,
                    Placeholder = RangeText(def),
                    ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
                };
                entry.TextChanged += (_, e) =>
                {
                    // Only digits (and one dot for decimals) can be typed; anything else is dropped immediately.
                    var clean = Sanitize(e.NewTextValue, integer);
                    if (clean != (e.NewTextValue ?? "")) entry.Text = clean;
                    else Revalidate();
                };
                return entry;
        }
    }

    private static string Sanitize(string? text, bool integer)
    {
        var sb = new System.Text.StringBuilder();
        var dot = false;
        foreach (var ch in (text ?? "").Replace(',', '.'))
        {
            if (char.IsAsciiDigit(ch)) sb.Append(ch);
            else if (ch == '.' && !integer && !dot) { dot = true; sb.Append(ch); }
        }
        return sb.ToString();
    }

    private static string RangeText(SettingDef def) => (def.Min, def.Max) switch
    {
        ({ } a, { } b) => $"{a:0.##} to {b:0.##}",
        ({ } a, null) => $"at least {a:0.##}",
        _ => "",
    };

    private void LoadValues()
    {
        loading = true;
        foreach (var (def, input) in inputs)
        {
            var value = config.Value(def.Key);
            switch (input)
            {
                case Switch s: s.IsToggled = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case TimeInput t: t.Value = value; break;
                case DateInput d: d.Value = value; break;
                case Picker p: p.SelectedItem = def.Options?.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase)); break;
                case Entry e: e.Text = value; break;
            }
        }
        loading = false;
        Revalidate();
    }

    private static string ReadValue(View input) => input switch
    {
        Switch s => s.IsToggled ? "true" : "false",
        TimeInput t => t.Value,
        DateInput d => d.Value,
        Picker p => p.SelectedItem as string ?? "",
        Entry e => e.Text ?? "",
        _ => "",
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Guard.Run(this, "Settings", AppearingCore);
    }

    private async Task AppearingCore()
    {
        await config.LoadSecretsAsync();
        loading = true;
        KeyEntry.Text = config.ApiKey;
        SecretEntry.Text = config.ApiSecret;
        loading = false;
        LoadValues();
    }

    private void OnFieldChanged(object? sender, TextChangedEventArgs e) => Revalidate();

    private static void Show(Label label, string? message)
    {
        label.Text = message ?? "";
        label.IsVisible = message is not null;
    }

    /// <summary>Checks every field (and the rules that span several fields), shows the messages under the fields and enables Save only when all is valid.</summary>
    private bool Revalidate(List<(SettingDef Def, string Value)>? good = null)
    {
        if (loading) return false;
        var ok = true;
        var values = new Dictionary<string, string>();
        foreach (var (def, input) in inputs)
        {
            string? message = null;
            if (def.TryNormalize(ReadValue(input), out var value, out var error)) values[def.Key] = value;
            else message = error.StartsWith(def.Label + ": ") ? error[(def.Label.Length + 2)..] : error;
            Show(errors[def], message);
            ok &= message is null;
        }

        // Rules that involve more than one field.
        if (ok)
        {
            if (Dec(values, "Portfolio:RiskPerTradePercent") > Dec(values, "Portfolio:MaxPortfolioStopRiskPercent"))
            {
                Show(Error("Portfolio:RiskPerTradePercent"), "Cannot be more than the max portfolio risk at stops.");
                ok = false;
            }
            if (Tm(values, "Scan:Strategy:EntryStart") >= Tm(values, "Scan:Strategy:LastEntry"))
            {
                Show(Error("Scan:Strategy:LastEntry"), "Must be later than the first entry time.");
                ok = false;
            }
            if (values["Scan:At"].Length > 0 && values["Scan:Date"].Length == 0)
            {
                Show(Error("Scan:At"), "Pick a replay date as well, or clear the replay time.");
                ok = false;
            }
        }

        var key = (KeyEntry.Text ?? "").Trim();
        var secret = (SecretEntry.Text ?? "").Trim();
        var keyError = !AppConfig.IsValidCredential(key) ? "Use 6-64 letters and digits only (no spaces)."
            : key.Length == 0 && secret.Length > 0 ? "Enter the API key as well, or clear the secret to use the bundled defaults." : null;
        var secretError = !AppConfig.IsValidCredential(secret) ? "Use 6-64 letters and digits only (no spaces)."
            : secret.Length == 0 && key.Length > 0 ? "Enter the API secret as well, or clear the key to use the bundled defaults." : null;
        Show(KeyError, keyError);
        Show(SecretError, secretError);
        ok &= keyError is null && secretError is null;

        SaveButton.IsEnabled = ok;
        if (ok && good is not null)
            foreach (var def in SettingDef.All) good.Add((def, values[def.Key]));
        return ok;
    }

    private Label Error(string key) => errors.First(kv => kv.Key.Key == key).Value;
    private static decimal Dec(Dictionary<string, string> v, string key) => decimal.Parse(v[key], CultureInfo.InvariantCulture);
    private static TimeOnly Tm(Dictionary<string, string> v, string key) => TimeOnly.ParseExact(v[key], ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture);

    private async void OnSave(object? sender, EventArgs e) => await Guard.Run(this, "Settings", () => OnSaveCore(sender, e));

    private async Task OnSaveCore(object? sender, EventArgs e)
    {
        var values = new List<(SettingDef Def, string Value)>();
        if (!Revalidate(values))
        {
            var first = errors.Values.Concat([KeyError, SecretError]).FirstOrDefault(l => l.IsVisible);
            if (first is not null) await Scroller.ScrollToAsync(first, ScrollToPosition.Center, true);
            await DisplayAlertAsync("Settings", "Please correct the fields marked in red.", "OK");
            return;
        }
        if ((KeyEntry.Text ?? "").Trim() != config.ApiKey || (SecretEntry.Text ?? "").Trim() != config.ApiSecret)
            await config.SaveCredentialsAsync(KeyEntry.Text ?? "", SecretEntry.Text ?? "");
        foreach (var (def, value) in values) config.Set(def, value);
        reminders.Schedule();
        if (config.ReminderTime is not null) await notifier.EnsurePermissionAsync(); // Android 13+ asks before any notification can be shown
        await Navigation.PopAsync();
    }

    private bool Toggled(string key) => inputs.FirstOrDefault(kv => kv.Key.Key == key).Value is Switch s && s.IsToggled;

    /// <summary>Shows the reminder now with the sound and vibration currently selected on this screen (saved or not).</summary>
    private async void OnTestReminder(object? sender, EventArgs e) => await Guard.Run(this, "Settings", () => OnTestReminderCore(sender, e));

    private async Task OnTestReminderCore(object? sender, EventArgs e)
    {
        await notifier.EnsurePermissionAsync();
        notifier.Remind("Start your VWAP scan (test)", "This is how the daily reminder looks and sounds. Tap to open the app.", Toggled("App:ReminderSound"), Toggled("App:ReminderVibrate"));
    }

    /// <summary>Saves the rows currently in the grids the same way the market-close save does, so the feature can be tried without waiting for 15:30.</summary>
    private async void OnTestSaveCsv(object? sender, EventArgs e) => await Guard.Run(this, "Settings", () => OnTestSaveCsvCore(sender, e));

    private async Task OnTestSaveCsvCore(object? sender, EventArgs e)
    {
        await coordinator.SaveDailyCsvIfDueAsync(force: true);
        await DisplayAlertAsync("Test save", coordinator.LastSaveMessage, "OK");
    }

    /// <summary>Sends today's log file (Zerodha/Kite errors and key events) through the share sheet.</summary>
    private async void OnShareLog(object? sender, EventArgs e) => await Guard.Run(this, "Settings", async () =>
    {
        var file = AppLog.TodayFile;
        if (!File.Exists(file)) { await DisplayAlertAsync("Log file", "Nothing has been logged today - no errors.", "OK"); return; }
        var copy = Path.Combine(FileSystem.CacheDirectory, Path.GetFileName(file)); // share from the cache folder, never from the live log
        File.Copy(file, copy, overwrite: true);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = Path.GetFileName(file), File = new ShareFile(copy, "text/plain") });
    });

    private async void OnReset(object? sender, EventArgs e) => await Guard.Run(this, "Settings", () => OnResetCore(sender, e));

    private async Task OnResetCore(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Settings", "Reset strategy, portfolio and auto-refresh settings to the values shipped with the app? The API key and secret are kept.", "Reset", "Cancel")) return;
        config.ResetSettings();
        LoadValues();
        reminders.Schedule();
    }
}
