using System.Net.NetworkInformation;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== Plattform-Schnittstelle */

public record WlanInterfaceInfo(string Id, string Description)
{
    public override string ToString() => Description;
}

public record WlanConnection(string Profile, string Ssid, string Bssid, int SignalQuality, int RxMbps, int TxMbps);

/// <summary>
/// WLAN-Scan der Plattform (Windows: wlanapi.dll, macOS: CoreWLAN). iOS erlaubt keinen Scan – dort ist <see cref="Platform.Wlan"/> null.
/// Fehlende Standortberechtigung wird als <see cref="WlanException"/> mit LocationDenied gemeldet.
/// </summary>
public interface IWlanScanner
{
    IReadOnlyList<WlanInterfaceInfo> Interfaces();

    /// <summary>Löst einen Scan aus, wartet <paramref name="settleMs"/> und liefert die sichtbaren Access Points.</summary>
    Task<List<WlanBss>> ScanAsync(string interfaceId, int settleMs, CancellationToken ct);

    /// <summary>Aktuelle Verbindung (oder null, wenn nicht verbunden).</summary>
    WlanConnection CurrentConnection(string interfaceId);

    /// <summary>Plattformspezifischer Hinweis, wie die Standortberechtigung erteilt wird (oder null).</summary>
    string PermissionHint { get; }
}

/* ====================================================================== Auswertung */

public static class WlanAnalysis
{
    public static IWlanScanner Scanner => Platform.Wlan ?? throw new PlatformNotSupportedException(L.T("WLAN-Scan ist auf dieser Plattform nicht verfügbar."));

    /// <summary>Doppelte BSSIDs zusammenfassen (stärkstes Signal) und Hersteller ergänzen.</summary>
    public static List<WlanBss> Prepare(IEnumerable<WlanBss> list)
    {
        var result = list.GroupBy(b => b.Bssid).Select(g => g.OrderByDescending(x => x.Rssi).First()).ToList();
        foreach (var b in result) b.Vendor = VendorOf(b.Bssid);
        return result;
    }

    /// <summary>Virtuelle BSSIDs (mehrere SSIDs pro AP) haben das "lokal verwaltet"-Bit gesetzt – ohne dieses Bit findet man meist den echten Hersteller.</summary>
    public static string VendorOf(string bssid)
    {
        if (string.IsNullOrEmpty(bssid) || bssid.Length < 2) return "";
        if (!OuiDatabase.IsRandomized(bssid)) return OuiDatabase.Lookup(bssid);
        var first = Convert.ToByte(bssid[..2], 16) & ~0x02;
        var real = OuiDatabase.Lookup(first.ToString("X2") + bssid[2..]);
        return real != "" && !real.StartsWith("Private") ? real + L.T(" (virtuell)") : L.T("virtuelle BSSID");
    }

    public static string Summary(IReadOnlyCollection<WlanBss> list) =>
        L.F("{0} Access Points · {1} Netze · aktualisiert {2:HH:mm:ss}", list.Count, list.Select(b => b.Ssid).Distinct().Count(), DateTime.Now);

    /// <summary>Die am wenigsten belasteten Kanäle eines Bandes (Belastung aus Signalstärke und Überlappung).</summary>
    public static List<int> BestChannels(IEnumerable<WlanBss> all, string band, int count = 3)
    {
        var nets = all.Where(b => b.Band == band).ToList();
        IEnumerable<int> candidates = band switch
        {
            "2,4 GHz" => new[] { 1, 6, 11 },
            "5 GHz" => new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140 },
            _ => Enumerable.Range(0, 59).Select(i => 1 + i * 4),
        };
        return candidates.Select(ch =>
        {
            double c = WlanParser.ChannelToMhz(ch, band), load = 0;
            foreach (var n in nets)
            {
                double overlap = (n.WidthMhz + 20) / 2.0 - Math.Abs(n.CenterMhz - c);
                if (overlap > 0) load += Math.Pow(10, (n.Rssi + 100) / 20.0) * (overlap / ((n.WidthMhz + 20) / 2.0));
            }
            return (ch, load);
        }).OrderBy(x => x.load).Take(count).Select(x => x.ch).ToList();
    }

    /// <summary>Kanalempfehlung als Text (leer, wenn im Band keine Netze sichtbar sind).</summary>
    public static string ChannelAdvice(IEnumerable<WlanBss> all, string band)
    {
        var nets = all.Where(b => b.Band == band).ToList();
        if (nets.Count == 0) return "";
        var mine = nets.FirstOrDefault(n => n.Connected);
        return L.F("Empfohlene Kanäle ({0}): {1}", band, string.Join(", ", BestChannels(nets, band)))
               + (mine != null ? L.F("   ·   Dein AP „{0}“ funkt auf Kanal {1} ({2} MHz, {3} dBm)", mine.DisplaySsid, mine.Channel, mine.WidthMhz, mine.Rssi) : "")
               + (band == "5 GHz" ? L.T("   ·   Kanäle 52–140 sind DFS-Kanäle (Radar-Erkennung).") : "");
    }
}

/* ====================================================================== WLAN-Ausleuchtung */

public class SurveyPoint
{
    public string Room { get; set; }
    public string Note { get; set; }
    public int Rssi { get; set; }
    public string RssiText => Rssi == 0 ? "–" : $"{Rssi} dBm";
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public string Channel { get; set; }
    public string Rate { get; set; }
    public string Ping { get; set; }
    public string Speed { get; set; }
    public string SameSsid { get; set; }
    public string Time { get; set; }
    public Level Level => Rssi == 0 ? Level.Bad : Rssi >= -60 ? Level.Ok : Rssi >= -70 ? Level.Info : Rssi >= -80 ? Level.Warn : Level.Bad;
    public string Rating => L.T(Rssi == 0 ? "keine Verbindung" : Rssi >= -60 ? "Gut" : Rssi >= -70 ? "Mittel" : Rssi >= -80 ? "Schwach" : "Kritisch");
}

public static class WlanSurvey
{
    public static List<SurveyPoint> Load() => JsonStore.LoadList<SurveyPoint>("survey.json");

    public static void Save(IEnumerable<SurveyPoint> points) => JsonStore.SaveList("survey.json", points);

    /// <summary>
    /// Ein Messpunkt: Signal/AP/Kanal/Datenrate der aktuellen Verbindung, andere APs desselben Netzes,
    /// 5 Pings zum WLAN-Gateway und optional ein kurzer Durchsatztest.
    /// </summary>
    public static async Task<SurveyPoint> MeasureAsync(string room, string note, bool speedTest, Action<string> status, CancellationToken ct = default)
    {
        var scanner = WlanAnalysis.Scanner;
        status?.Invoke(L.T("Scanne WLAN …"));
        var iface = scanner.Interfaces().FirstOrDefault() ?? throw new Exception(L.T("Kein WLAN-Adapter gefunden."));
        var conn = scanner.CurrentConnection(iface.Id);
        List<WlanBss> bss;
        try { bss = await scanner.ScanAsync(iface.Id, 3500, ct); }
        catch (WlanException ex) when (ex.LocationDenied) { throw new Exception(L.T("Standortzugriff fehlt – siehe WLAN-Scanner.")); }

        var p = new SurveyPoint { Room = room, Note = note, Time = DateTime.Now.ToString("HH:mm"), Ping = "–", Speed = "–" };
        if (conn == null)
        {
            p.Ssid = L.T("(nicht verbunden)");
            p.SameSsid = bss.Count.ToString();
            return p;
        }

        var me = bss.FirstOrDefault(b => b.Bssid == conn.Bssid);
        p.Ssid = conn.Ssid;
        p.Bssid = conn.Bssid;
        p.Rssi = me?.Rssi ?? (int)(conn.SignalQuality / 2.0 - 100);
        p.Channel = me != null ? $"{me.Channel} · {me.Band} · {me.WidthMhz} MHz" : "";
        p.Rate = L.F("↓{0} / ↑{1} Mbit/s", conn.RxMbps, conn.TxMbps);
        var same = bss.Where(b => b.Ssid == conn.Ssid).ToList();
        var better = same.Where(b => b.Bssid != conn.Bssid && b.Rssi > p.Rssi + 8).OrderByDescending(b => b.Rssi).FirstOrDefault();
        p.SameSsid = same.Count.ToString() + (better != null ? L.F(" · besserer AP {0} ({1} dBm)", better.Bssid, better.Rssi) : "");

        status?.Invoke(L.T("Ping zum Gateway …"));
        var gw = NetUtil.ActiveIPv4().Where(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211).Select(x => x.Gateway).FirstOrDefault(g => g != null);
        if (gw != null)
        {
            var rtts = new List<long>();
            int lost = 0;
            for (int i = 0; i < 5; i++)
            {
                var r = await NetUtil.PingAsync(gw, 1000, ct);
                if (r.HasValue) rtts.Add(r.Value); else lost++;
            }
            p.Ping = rtts.Count > 0 ? $"{rtts.Average():0} ms" + (lost > 0 ? $" ({lost}/5 ✗)" : "") : "✗";
        }

        if (speedTest)
        {
            status?.Invoke(L.T("Messe Durchsatz …"));
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(15000);
                var mbps = await CloudflareSpeed.DownloadAsync(5, 4, null, cts.Token);
                p.Speed = $"{mbps:0} Mbit/s";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { p.Speed = "✗"; }
        }
        return p;
    }
}
