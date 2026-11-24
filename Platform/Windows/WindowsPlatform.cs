using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using NetStuff.Services;

namespace NetStuff.Platforms.Windows;

/// <summary>Registriert die Windows-Umsetzungen der Plattformdienste.</summary>
public static class WindowsPlatform
{
    public static void Register()
    {
        Platform.Notifier = new ToastNotifier();
        Platform.Arp = new SendArpResolver();
        Platform.Lldp = new PktmonLldpSource();
        Platform.Wlan = new WindowsWlanScanner();
    }
}

/// <summary>Windows-Benachrichtigungen (Toasts).</summary>
public sealed class ToastNotifier : INotifier
{
    public void Show(string title, string message)
    {
        new CommunityToolkit.WinUI.Notifications.ToastContentBuilder()
            .AddText(title)
            .AddText(message)
            .Show();
    }
}
