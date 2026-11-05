using VwapScanner;

// VwapScanner web API: one endpoint, GET /, configured entirely from appsettings.json.
// It runs only the ScalpVwap strategy over the configured stock list and returns the VWAP signals together with the optimized portfolio.
// Read-only: it never places orders.
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
if (string.IsNullOrWhiteSpace(builder.Configuration["Urls"])) builder.WebHost.UseUrls("http://127.0.0.1:9079");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var app = builder.Build();
var state = new ApiState(app.Configuration, app.Environment.ContentRootPath);

app.MapGet("/", async (HttpContext ctx) =>
{
    // Zerodha redirects the browser here after login (?request_token=...&status=success); finish the login, then show the result.
    if (ctx.Request.Query.ContainsKey("request_token") || ctx.Request.Query.ContainsKey("status"))
    {
        try
        {
            await state.CompleteLoginAsync(ctx.Request.QueryString.Value ?? "", ctx.RequestAborted);
            return Results.Redirect("/");
        }
        catch (ZerodhaException e) { return Results.Json(new { error = e.Message, loginUrl = SafeLoginUrl() }, statusCode: 400); }
        catch (Exception e) when (e is not OperationCanceledException) { return Results.Json(new { error = e.Message, loginUrl = SafeLoginUrl() }, statusCode: 500); }
    }
    try { return Results.Json(await state.RunAsync(ctx.RequestAborted)); }
    catch (ZerodhaAuthException e) { return Results.Json(new { error = e.Message, loginUrl = SafeLoginUrl() }, statusCode: 401); }
    catch (ZerodhaException e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
    catch (ConfigurationException e) { return Results.Json(new { error = "Configuration: " + e.Message }, statusCode: 500); }
    catch (Exception e) when (e is not OperationCanceledException) { return Results.Json(new { error = e.GetType().Name + ": " + e.Message }, statusCode: 500); }
});

string? SafeLoginUrl() { try { return state.LoginUrl(); } catch (ZerodhaException) { return null; } }

try { await state.RestoreAsync(CancellationToken.None); }
catch (ZerodhaException e) { Console.WriteLine("Could not check the saved Zerodha token: " + e.Message); }
var listening = app.Configuration["Urls"] ?? "http://127.0.0.1:9079";
Console.WriteLine(state.Connected ? "Connected to Zerodha with the saved token." : $"Not connected to Zerodha yet: open {listening} and follow loginUrl.");
Console.WriteLine($"VwapScanner listening on {listening} (Ctrl+C to stop)");

try { await app.RunAsync(); return 0; }
catch (Exception e) when (e is IOException || e.InnerException is IOException || e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"Cannot listen on {listening}: {e.Message}\nThe port is probably used by GptTrading's Zerodha login callback. Stop GptTrading, or change \"Urls\" in appsettings.json.");
    return 2;
}
