namespace VwapScanner.Mobile;

/// <summary>
/// A simple scrollable table (header row + data rows) for the result grids. Rows are only ever appended; the rows added by the latest refresh are tinted
/// so new results stand out. The newest <see cref="MaxRows"/> rows are kept on screen; the full history stays in ScanCoordinator for the CSV export.
/// </summary>
public sealed class ResultTable
{
    public const int MaxRows = 500;
    private static readonly Color HeaderTint = Color.FromRgba(128, 128, 128, 64);
    private static readonly Color NewTint = Color.FromRgba(255, 165, 0, 56);

    private readonly double[] widths;
    private readonly VerticalStackLayout rows = new() { Spacing = 0 };
    private readonly List<Grid> latest = [];

    public ScrollView View { get; }
    public int Count => rows.Children.Count;

    public ResultTable(string[] headers, double[] widths)
    {
        this.widths = widths;
        View = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = new VerticalStackLayout { Spacing = 0, Children = { MakeRow(headers, header: true), rows } },
        };
    }

    public void Clear()
    {
        rows.Children.Clear();
        latest.Clear();
    }

    /// <summary>Adds rows at the bottom and tints them; the rows of the previous refresh lose their tint.</summary>
    public void Append(IEnumerable<string[]> items)
    {
        var added = new List<Grid>();
        foreach (var cells in items)
        {
            var row = MakeRow(cells, header: false);
            row.BackgroundColor = NewTint;
            rows.Children.Add(row);
            added.Add(row);
        }
        if (added.Count == 0) return;
        foreach (var old in latest) old.BackgroundColor = Colors.Transparent;
        latest.Clear();
        latest.AddRange(added);
        while (rows.Children.Count > MaxRows) rows.Children.RemoveAt(0);
    }

    private Grid MakeRow(string[] cells, bool header)
    {
        var grid = new Grid { ColumnSpacing = 0, BackgroundColor = header ? HeaderTint : Colors.Transparent };
        for (var i = 0; i < cells.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(widths[i])));
            var label = new Label
            {
                Text = cells[i],
                FontSize = 12,
                FontAttributes = header ? FontAttributes.Bold : FontAttributes.None,
                LineBreakMode = LineBreakMode.NoWrap,
                Padding = new Thickness(6, 5),
            };
            Grid.SetColumn(label, i);
            grid.Children.Add(label);
        }
        return grid;
    }
}
