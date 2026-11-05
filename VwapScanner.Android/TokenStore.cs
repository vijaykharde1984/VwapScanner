using System.Text.Json;

namespace VwapScanner;

public sealed record Session(string ApiKey, string AccessToken);

/// <summary>The saved Zerodha session (.kite_token.json, same format as GptTrading): kept for one API key and valid until the next 06:00 IST.</summary>
public static class TokenStore
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    public static bool Fresh(DateTimeOffset saved, DateTimeOffset now)
    {
        var s = saved.ToOffset(Ist);
        var expiry = new DateTimeOffset(s.Date.AddHours(6), Ist);
        if (s >= expiry) expiry = expiry.AddDays(1);
        return now < expiry;
    }

    /// <summary>The saved session when it is for this API key, not past 06:00 IST and still accepted by Zerodha; otherwise null.</summary>
    public static async Task<Session?> LoadAsync(string path, string apiKey, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        string? key, token; DateTimeOffset? saved;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            key = r.TryGetProperty("api_key", out var k) ? k.GetString() : null;
            token = r.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            saved = r.TryGetProperty("saved_utc", out var s) && s.TryGetDateTimeOffset(out var when) ? when : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
        if (string.IsNullOrWhiteSpace(token) || key != apiKey) return null;
        if (saved is not null && !Fresh(saved.Value, DateTimeOffset.UtcNow)) return null;
        using var probe = new KiteClient(apiKey, token);
        try { await probe.ProfileAsync(ct); }
        catch (ZerodhaAuthException) { return null; }
        return new(apiKey, token);
    }

    public static void Save(string path, Session session) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { access_token = session.AccessToken, api_key = session.ApiKey, saved_utc = DateTime.UtcNow }));
}
