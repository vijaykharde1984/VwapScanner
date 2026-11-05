namespace VwapScanner.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddSingleton(_ => AppConfig.Load());
        // Same business logic as the web API: one ApiState holds the Zerodha session, the instrument master and the candle cache.
        builder.Services.AddSingleton(sp => new ApiState(sp.GetRequiredService<AppConfig>().Root, FileSystem.AppDataDirectory));
        builder.Services.AddSingleton<INotifier, AndroidNotifier>();
        builder.Services.AddSingleton<IKeepAlive, AndroidKeepAlive>();
        builder.Services.AddSingleton<IFileSaver, AndroidFileSaver>();
        builder.Services.AddSingleton<ScanCoordinator>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<SettingsPage>();

        return builder.Build();
    }
}
