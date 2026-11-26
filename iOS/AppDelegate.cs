using Avalonia;
using Avalonia.iOS;
using Foundation;
using NetStuff.Ui;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.iOS;

/// <summary>Start der App auf iPhone/iPad: Plattformdienste registrieren, gemeinsame Oberfläche (MainView) anzeigen.</summary>
[Register("AppDelegate")]
public partial class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        IosPlatform.Register();
        // Startoptionen (--page, --param, --run) nur für Tests im Simulator: xcrun simctl launch <Gerät> at.itstuff.netstuff --page dns
        Startup.Init(Foundation.NSProcessInfo.ProcessInfo.Arguments.Skip(1).ToArray());
        return base.CustomizeAppBuilder(builder).WithInterFont();
    }
}
