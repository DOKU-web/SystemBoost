using Avalonia;
using System;

namespace SystemBoost.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install / update / uninstall hooks of the Velopack installer and exits
        // early when it was started for one of them. Does nothing in Steam or dev builds.
        Velopack.VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
