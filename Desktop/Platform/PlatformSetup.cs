using NetStuff.Desktop.Platforms.Mac;

namespace NetStuff.Desktop.Platforms;

/// <summary>Registriert die Plattformdienste des Betriebssystems, auf dem die Desktop-App läuft.</summary>
internal static class PlatformSetup
{
    public static void Register()
    {
        if (OperatingSystem.IsMacOS())
        {
            NetStuff.Platform.Arp = new MacArpResolver();
            NetStuff.Platform.Wlan = new MacWlanScanner();
            NetStuff.Platform.Notifier = new MacNotifier();
        }
        else if (OperatingSystem.IsWindows())
        {
            // dieselben Umsetzungen wie in der WPF-App (Dateien aus Platform\Windows verlinkt)
            NetStuff.Platform.Arp = new NetStuff.Platforms.Windows.SendArpResolver();
            NetStuff.Platform.Wlan = new NetStuff.Platforms.Windows.WindowsWlanScanner();
        }
    }
}
