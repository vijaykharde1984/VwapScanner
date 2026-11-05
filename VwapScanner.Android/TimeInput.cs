using System.Globalization;

namespace VwapScanner.Mobile;

/// <summary>A time picker for the settings screen. Optional ones can be cleared (empty value = feature off).</summary>
public sealed class TimeInput : HorizontalStackLayout
{
    private readonly TimePicker picker = new() { Format = "HH:mm", VerticalOptions = LayoutOptions.Center };
    private readonly Button setButton = new() { Text = "Set time" };
    private readonly Button clearButton = new() { Text = "Clear" };
    private readonly bool optional;
    private bool hasValue;

    public event EventHandler? Changed;

    public TimeInput(bool optional)
    {
        this.optional = optional;
        Spacing = 12;
        setButton.Clicked += (_, _) => { Apply(picker.Time ?? TimeSpan.Zero, true); picker.Focus(); Changed?.Invoke(this, EventArgs.Empty); };
        clearButton.Clicked += (_, _) => { Apply(null, false); Changed?.Invoke(this, EventArgs.Empty); };
        picker.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimePicker.Time)) Changed?.Invoke(this, EventArgs.Empty); };
        Children.Add(picker);
        Children.Add(setButton);
        Children.Add(clearButton);
        Apply(null, !optional);
    }

    private void Apply(TimeSpan? time, bool has)
    {
        hasValue = has;
        if (time is { } t) picker.Time = t;
        picker.IsVisible = has;
        clearButton.IsVisible = has && optional;
        setButton.IsVisible = !has;
    }

    /// <summary>"HH:mm:ss" or empty when not set.</summary>
    public string Value
    {
        get => hasValue ? (picker.Time ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "";
        set
        {
            if (TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                Apply(t.ToTimeSpan(), true);
            else
                Apply(null, !optional);
        }
    }
}
