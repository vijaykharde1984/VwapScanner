using System.Globalization;

namespace VwapScanner.Mobile;

/// <summary>A calendar picker for the settings screen (optional: empty value = not set). Only past trading days up to today can be picked.</summary>
public sealed class DateInput : HorizontalStackLayout
{
    private readonly DatePicker picker = new()
    {
        Format = "yyyy-MM-dd",
        MinimumDate = new DateTime(2015, 1, 1),
        VerticalOptions = LayoutOptions.Center,
    };
    private readonly Button setButton = new() { Text = "Pick date" };
    private readonly Button clearButton = new() { Text = "Clear" };
    private bool hasValue;

    public event EventHandler? Changed;

    public DateInput()
    {
        Spacing = 12;
        picker.MaximumDate = DateTime.Today;
        picker.DateSelected += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        setButton.Clicked += (_, _) =>
        {
            Apply(LastWeekday(), true);
            picker.Focus();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        clearButton.Clicked += (_, _) =>
        {
            Apply(null, false);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Children.Add(picker);
        Children.Add(setButton);
        Children.Add(clearButton);
        Apply(null, false);
    }

    private static DateTime LastWeekday()
    {
        var d = DateTime.Today;
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            d = d.AddDays(-1);
        return d;
    }

    private void Apply(DateTime? date, bool has)
    {
        hasValue = has;
        picker.MaximumDate = DateTime.Today;
        if (date is { } d)
            picker.Date = d;
        picker.IsVisible = has;
        clearButton.IsVisible = has;
        setButton.IsVisible = !has;
    }

    /// <summary>"yyyy-MM-dd" or empty when not set.</summary>
    public string Value
    {
        get =>
            hasValue
                ? (picker.Date ?? DateTime.Today).ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture
                )
                : "";
        set
        {
            if (
                DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var d
                )
                && d <= DateTime.Today
                && d >= picker.MinimumDate
            )
                Apply(d, true);
            else
                Apply(null, false);
        }
    }
}
