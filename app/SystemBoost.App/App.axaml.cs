using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SystemBoost.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow();
            desktop.MainWindow = main;
            desktop.ShutdownRequested += (_, _) => main.Shutdown();
            _ = Services.License.RenewIfNeededAsync();   // revoked keys off; subscriptions: renewed key
            Services.License.WatchPending();              // an order paid while the app was closed
            Services.Updater.Start();                     // direct-download builds: check GitHub Releases
        }
        base.OnFrameworkInitializationCompleted();
    }
}
