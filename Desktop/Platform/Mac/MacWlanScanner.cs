using Avalonia.Threading;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// WLAN-Scan über CoreWLAN. Ohne Standortfreigabe liefert macOS (ab 14) weder SSID noch BSSID –
/// die Netze erscheinen dann trotzdem mit Kanal, Signal und Verschlüsselung, <see cref="NamesHidden"/> ist true.
/// </summary>
public sealed class MacWlanScanner : IWlanScanner
{
    private static readonly object Lock = new();
    private static bool _loaded;
    private volatile bool _hidden;

    public string PermissionHint => "x-apple.systempreferences:com.apple.preference.security?Privacy_LocationServices";
    public bool NamesHidden => _hidden;

    private static IntPtr Client()
    {
        lock (Lock)
        {
            if (!_loaded) { ObjC.LoadFramework("CoreWLAN"); _loaded = true; }
        }
        var cls = ObjC.Class("CWWiFiClient");
        if (cls == IntPtr.Zero) throw new WlanException(L.T("CoreWLAN ist nicht verfügbar."), 1);
        return ObjC.Get(cls, "sharedWiFiClient");
    }

    private static IntPtr Interface(string name)
    {
        foreach (var i in ObjC.Items(ObjC.Get(Client(), "interfaces")))
            if (ObjC.Str(ObjC.Get(i, "interfaceName")) == name) return i;
        return IntPtr.Zero;
    }

    public IReadOnlyList<WlanInterfaceInfo> Interfaces()
    {
        using var pool = new ObjC.Pool(true);
        var list = new List<WlanInterfaceInfo>();
        foreach (var i in ObjC.Items(ObjC.Get(Client(), "interfaces")))
        {
            var name = ObjC.Str(ObjC.Get(i, "interfaceName"));
            if (name != null) list.Add(new WlanInterfaceInfo(name, L.F("WLAN ({0})", name)));
        }
        return list;
    }

    public Task<List<WlanBss>> ScanAsync(string interfaceId, int settleMs, CancellationToken ct) =>
        Task.Run(() => Scan(interfaceId), ct);

    public WlanConnection CurrentConnection(string interfaceId)
    {
        using var pool = new ObjC.Pool(true);
        var iface = Interface(interfaceId);
        if (iface == IntPtr.Zero || ObjC.Get(iface, "wlanChannel") == IntPtr.Zero) return null;
        int rssi = (int)ObjC.GetNInt(iface, "rssiValue");
        int rate = (int)Math.Round(ObjC.GetDouble(iface, "transmitRate"));
        var ssid = ObjC.Str(ObjC.Get(iface, "ssid")) ?? L.T("(Name verborgen)");
        var bssid = NormalizeMac(ObjC.Str(ObjC.Get(iface, "bssid")));
        return new WlanConnection(ssid, ssid, bssid, Math.Clamp(2 * (rssi + 100), 0, 100), rate, rate);
    }

    private List<WlanBss> Scan(string interfaceId)
    {
        using var pool = new ObjC.Pool(true);
        var iface = Interface(interfaceId);
        if (iface == IntPtr.Zero) throw new WlanException(L.T("WLAN-Adapter nicht gefunden."), 2);

        var set = ObjC.CallWithError(iface, "scanForNetworksWithName:error:", IntPtr.Zero, out var err);
        if (set == IntPtr.Zero)
        {
            // z. B. „Resource busy“ bei schnell aufeinanderfolgenden Scans – dann die letzten Ergebnisse
            set = ObjC.Get(iface, "cachedScanResults");
            if (set == IntPtr.Zero)
                throw new WlanException(L.T("WLAN-Scan fehlgeschlagen: ") + (ObjC.Str(ObjC.Get(err, "localizedDescription")) ?? "?"), 3);
        }

        var connChannel = ObjC.Get(iface, "wlanChannel");
        int connCh = (int)ObjC.GetNInt(connChannel, "channelNumber");
        int connRssi = (int)ObjC.GetNInt(iface, "rssiValue");
        var connBssid = NormalizeMac(ObjC.Str(ObjC.Get(iface, "bssid")));

        var list = new List<WlanBss>();
        int hidden = 0;
        foreach (var net in ObjC.Items(set))
        {
            var ies = ObjC.Bytes(ObjC.Get(net, "informationElementData"));
            var ssid = ObjC.Str(ObjC.Get(net, "ssid")) ?? SsidFromIes(ies);
            var bssid = NormalizeMac(ObjC.Str(ObjC.Get(net, "bssid")));
            if (bssid == null) hidden++;

            var ch = ObjC.Get(net, "wlanChannel");
            int num = (int)ObjC.GetNInt(ch, "channelNumber");
            string band = (int)ObjC.GetNInt(ch, "channelBand") switch
            {
                1 => "2,4 GHz",
                2 => "5 GHz",
                3 => "6 GHz",
                _ => num <= 14 ? "2,4 GHz" : "5 GHz",
            };
            int rssi = (int)ObjC.GetNInt(net, "rssiValue");

            var b = new WlanBss
            {
                Ssid = ssid ?? "",
                Bssid = bssid ?? L.F("verborgen {0}", hidden),
                Rssi = rssi,
                Quality = Math.Clamp(2 * (rssi + 100), 0, 100),
                Channel = num,
                Band = band,
                FrequencyMhz = (int)WlanParser.ChannelToMhz(num, band),
                MaxRateMbps = MaxRate(ies),
            };

            int phy = ObjC.CallBool(net, "supportsPHYMode:", 7) ? 11
                : ObjC.CallBool(net, "supportsPHYMode:", 6) ? 10
                : ObjC.CallBool(net, "supportsPHYMode:", 5) ? 8
                : ObjC.CallBool(net, "supportsPHYMode:", 4) ? 7 : 0;
            WlanParser.ParseIes(b, ies, 0, phy);
            if (ies.Length == 0)
            {
                (b.WidthMhz, b.CenterMhz) = WidthAndCenter(num, band, (int)ObjC.GetNInt(ch, "channelWidth"));
                b.Security = SecurityOf(net);
            }

            b.Connected = connBssid != null ? connBssid == b.Bssid : false;
            list.Add(b);
        }

        // Ohne BSSIDs: verbundenes Netz über Kanal und Signal zuordnen
        if (connBssid == null && connChannel != IntPtr.Zero)
        {
            var me = list.Where(b => b.Channel == connCh).OrderBy(b => Math.Abs(b.Rssi - connRssi)).FirstOrDefault();
            if (me != null && Math.Abs(me.Rssi - connRssi) <= 8) me.Connected = true;
        }

        bool wasHidden = _hidden;
        _hidden = list.Count > 0 && hidden == list.Count;
        if (_hidden && !wasHidden) Dispatcher.UIThread.Post(MacLocation.RequestOnce);
        return list;
    }

    private static string NormalizeMac(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return null;
        var parts = mac.Split(':');
        if (parts.Length != 6) return mac.ToUpperInvariant();
        return string.Join(":", parts.Select(p => p.PadLeft(2, '0').ToUpperInvariant()));
    }

    private static string SsidFromIes(byte[] ies)
    {
        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            if (id == 0) return len == 0 ? null : WlanParser.DecodeSsid(ies[(i + 2)..(i + 2 + len)]);
            i += 2 + len;
        }
        return null;
    }

    /// <summary>Höchste Basisrate aus „Supported Rates“ (1) und „Extended Supported Rates“ (50).</summary>
    private static double MaxRate(byte[] ies)
    {
        double max = 0;
        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            if (id is 1 or 50)
                for (int k = 0; k < len; k++) max = Math.Max(max, (ies[i + 2 + k] & 0x7F) * 0.5);
            i += 2 + len;
        }
        return max;
    }

    /// <summary>Kanalbreite (CWChannelWidth) und Mittenfrequenz, wenn keine Informationselemente vorliegen.</summary>
    private static (int, double) WidthAndCenter(int ch, string band, int cwWidth)
    {
        double f = WlanParser.ChannelToMhz(ch, band);
        int width = cwWidth switch { 2 => 40, 3 => 80, 4 => 160, _ => 20 };
        if (width == 20) return (20, f);
        if (band == "2,4 GHz") return (40, ch <= 7 ? f + 10 : f - 10);
        int block = width / 5;                                   // Kanäle je Block (40 MHz = 8)
        int first = band == "6 GHz" ? 1 : 36;
        int start = ch >= 149 && band == "5 GHz" ? 149 : first;  // 5 GHz: oberer Bereich beginnt bei 149
        int baseCh = start + (ch - start) / block * block;
        return (width, WlanParser.ChannelToMhz(baseCh, band) + width / 2.0 - 10);
    }

    private static string SecurityOf(IntPtr net)
    {
        bool S(int v) => ObjC.CallBool(net, "supportsSecurity:", v);
        if (S(13)) return "WPA2/WPA3-Personal";
        if (S(11)) return "WPA3-Personal";
        if (S(12)) return "WPA3-Enterprise";
        if (S(9) || S(10)) return "WPA2-Enterprise";
        if (S(4) || S(5)) return "WPA2-Personal";
        if (S(2) || S(3)) return "WPA";
        if (S(14) || S(15)) return "OWE";
        if (S(1) || S(6)) return "WEP";
        return "Offen";
    }
}
