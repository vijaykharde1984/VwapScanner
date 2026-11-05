namespace VwapScanner;

/// <summary>Stock lists shipped with the app (universes\*.txt, one NSE trading symbol per line).</summary>
public static class Universe
{
    public static string[] Load(IEnumerable<string>? symbols, string? universe)
    {
        var explicitList = (symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (explicitList.Length > 0) return explicitList;
        var name = string.IsNullOrWhiteSpace(universe) ? "nifty50" : universe.Trim();
        if (!name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')) throw new ArgumentException($"Universe '{name}' is not valid.");
        var dir = Path.Combine(AppContext.BaseDirectory, "universes");
        var path = Path.Combine(dir, name + ".txt");
        if (!File.Exists(path))
        {
            var known = Directory.Exists(dir) ? string.Join(", ", Directory.GetFiles(dir, "*.txt").Select(Path.GetFileNameWithoutExtension)) : "none";
            throw new ArgumentException($"Universe '{name}' not found. Available: {known}.");
        }
        return [.. File.ReadAllLines(path).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).Select(x => x.ToUpperInvariant()).Distinct()];
    }
}
