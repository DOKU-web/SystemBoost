using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;

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
            if (Services.Eula.Accepted) StartOnline(main);
            else main.Opened += AskForAgreement;
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Asked over the main window, so a refusal closes through the normal exit path
    // and everything the app changed earlier still goes back.
    private async void AskForAgreement(object? sender, EventArgs e)
    {
        var main = (MainWindow)sender!;
        main.Opened -= AskForAgreement;
        if (!await new Pages.EulaDialog(consent: true).ShowDialog<bool>(main)) { main.Close(); return; }
        Services.Eula.Accept();
        StartOnline(main);
    }

    private static void StartOnline(MainWindow main)
    {
        _ = Services.License.RenewIfNeededAsync();   // revoked keys off; subscriptions: renewed key
        Services.License.WatchPending();              // an order paid while the app was closed
        Services.Updater.Start();                     // direct-download builds: check GitHub Releases
        Services.Telemetry.Start(main.UsageNow);      // usage statistics, unless turned off in Settings
    }
}
