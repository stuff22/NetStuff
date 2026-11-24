using Avalonia;
using NetStuff.Ui;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Platforms.PlatformSetup.Register();
        if (args.Contains("--diag")) { Platforms.NetDiag.Run(); return; }
        Startup.Init(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Wird auch vom Avalonia-Designer verwendet.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
