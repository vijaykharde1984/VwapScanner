namespace VwapScanner;

/// <summary>Stock lists shipped inside the APK (Resources\Raw\universes\*.txt, one NSE trading symbol per line).</summary>
public static class Universe
{
    public static readonly string[] Names = ["nifty50", "nifty100", "nifty200", "nifty500"];

    public static async Task<string[]> LoadAsync(IEnumerable<string>? symbols, string? universe)
    {
        var explicitList = (symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (explicitList.Length > 0) return explicitList;
        var name = string.IsNullOrWhiteSpace(universe) ? "nifty50" : universe.Trim();
        if (!name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw new ArgumentException($"Universe '{name}' is not valid.");
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"Universe '{name}' not found. Available: {string.Join(", ", Names)}.");
        string text;
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync($"universes/{name.ToLowerInvariant()}.txt");
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync();
        }
        catch (FileNotFoundException) { throw new ArgumentException($"Universe '{name}' is missing from the app package."); }
        return [.. text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => x.ToUpperInvariant()).Distinct()];
    }
}
