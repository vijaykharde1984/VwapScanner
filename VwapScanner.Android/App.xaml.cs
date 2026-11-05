namespace VwapScanner.Mobile;

public partial class App : Application
{
    private readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(services.GetRequiredService<MainPage>()));
}
