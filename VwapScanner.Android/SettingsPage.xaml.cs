namespace VwapScanner.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly AppConfig config;
    private readonly Dictionary<SettingDef, View> inputs = [];

    public SettingsPage(AppConfig config)
    {
        InitializeComponent();
        this.config = config;
        BuildFields();
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
            View input = def.Kind switch
            {
                SettingKind.Bool => new Switch { HorizontalOptions = LayoutOptions.Start },
                SettingKind.Choice => new Picker { ItemsSource = def.Options },
                SettingKind.Integer or SettingKind.Decimal => new Entry { Keyboard = Keyboard.Numeric },
                _ => new Entry { Keyboard = Keyboard.Plain },
            };
            inputs[def] = input;
            Fields.Add(input);
            if (def.Hint.Length > 0) Fields.Add(new Label { Text = def.Hint, FontSize = 12 });
        }
    }

    private void LoadValues()
    {
        foreach (var (def, input) in inputs)
        {
            var value = config.Value(def.Key);
            switch (input)
            {
                case Switch s: s.IsToggled = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                case Picker p: p.SelectedItem = def.Options?.FirstOrDefault(o => o.Equals(value, StringComparison.OrdinalIgnoreCase)); break;
                case Entry e: e.Text = value; break;
            }
        }
    }

    private static string ReadValue(View input) => input switch
    {
        Switch s => s.IsToggled ? "true" : "false",
        Picker p => p.SelectedItem as string ?? "",
        Entry e => e.Text ?? "",
        _ => "",
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await config.LoadSecretsAsync();
        KeyEntry.Text = config.ApiKey;
        SecretEntry.Text = config.ApiSecret;
        LoadValues();
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        var values = new List<(SettingDef Def, string Value)>();
        foreach (var (def, input) in inputs)
        {
            if (!def.TryNormalize(ReadValue(input), out var value, out var error))
            {
                await DisplayAlertAsync("Settings", error, "OK");
                return;
            }
            values.Add((def, value));
        }
        if ((KeyEntry.Text ?? "").Trim() != config.ApiKey || (SecretEntry.Text ?? "").Trim() != config.ApiSecret)
            await config.SaveCredentialsAsync(KeyEntry.Text ?? "", SecretEntry.Text ?? "");
        foreach (var (def, value) in values) config.Set(def, value);
        await Navigation.PopAsync();
    }

    private async void OnReset(object? sender, EventArgs e)
    {
        if (!await DisplayAlertAsync("Settings", "Reset strategy, portfolio and auto-refresh settings to the values shipped with the app? The API key and secret are kept.", "Reset", "Cancel")) return;
        config.ResetSettings();
        LoadValues();
    }
}
