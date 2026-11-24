using System.Net;
using NetStuff.Services;

namespace NetStuff;

/// <summary>Systembenachrichtigung (Windows-Toast, macOS-/iOS-Mitteilung).</summary>
public interface INotifier
{
    void Show(string title, string message);
}

/// <summary>MAC-Adresse zu einer IP im lokalen Subnetz (Windows: SendARP, macOS: ARP-Tabelle, iOS: nicht verfügbar).</summary>
public interface IArpResolver
{
    byte[] Resolve(IPAddress ip);

    /// <summary>Komplette ARP-Tabelle (IP → MAC), falls die Plattform sie auslesen kann; sonst null.</summary>
    IReadOnlyDictionary<string, byte[]> Table() => null;
}

/// <summary>Mitschnitt von LLDP/CDP-Rahmen (Windows: pktmon, macOS: BPF/tcpdump). Liefert rohe Ethernet-Rahmen.</summary>
public interface ILldpCaptureSource
{
    Task<List<byte[]>> CaptureFramesAsync(int seconds, CancellationToken ct);
}

/// <summary>
/// Paketmitschnitt mit BPF-Filter (macOS: tcpdump mit Administratorrechten). <c>onReady</c> wird aufgerufen,
/// sobald der Mitschnitt läuft (z. B. um dann eine Anfrage zu senden). Liefert Ethernet-Rahmen mit Zeitstempel in Sekunden.
/// </summary>
public interface IPacketCapture
{
    Task<List<(byte[] Frame, double Time)>> CaptureAsync(string filter, int seconds, string reason, Func<Task> onReady, CancellationToken ct);
}

/// <summary>
/// Plattformdienste. Die jeweilige App (Windows/WPF, macOS, iOS) setzt beim Start ihre Umsetzungen;
/// nicht gesetzte Dienste gelten als „auf dieser Plattform nicht verfügbar“.
/// </summary>
public static class Platform
{
    private static string _dataDir;

    /// <summary>Datenordner (Einstellungen, Projekte, Verlauf …). Standard: LocalApplicationData/Net-Stuff.</summary>
    public static string DataDir
    {
        get
        {
            if (_dataDir == null)
            {
                _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Net-Stuff");
                try { Directory.CreateDirectory(_dataDir); } catch { }
            }
            return _dataDir;
        }
        set
        {
            _dataDir = value;
            try { Directory.CreateDirectory(_dataDir); } catch { }
        }
    }

    /// <summary>ICMP-Echo (Standard: .NET-Ping; iOS: eigene Umsetzung).</summary>
    public static Services.IIcmpSender Icmp { get; set; }

    /// <summary>WLAN-Scan (Windows: wlanapi, macOS: CoreWLAN; iOS: nicht verfügbar).</summary>
    public static Services.IWlanScanner Wlan { get; set; }

    public static INotifier Notifier { get; set; }
    public static IArpResolver Arp { get; set; }
    public static ILldpCaptureSource Lldp { get; set; }

    /// <summary>Allgemeiner Paketmitschnitt (macOS: tcpdump) – z. B. für den DHCP-Test, wenn Port 68 belegt ist.</summary>
    public static IPacketCapture Capture { get; set; }

    /// <summary>Öffnet eine URL oder Datei (Standard: Shell; iOS: Safari bzw. Teilen-Dialog für Dateien).</summary>
    public static Action<string> UrlOpener { get; set; }

    public static bool HasArp => Arp != null;
    public static bool HasLldp => Lldp != null;
}

/// <summary>Benachrichtigungen unter Beachtung der Einstellung „Benachrichtigungen“.</summary>
public static class Notifier
{
    public static void Show(string title, string message)
    {
        if (!Infrastructure.AppSettings.Current.Notifications) return;
        try { Platform.Notifier?.Show(title, message); } catch { }
    }
}
