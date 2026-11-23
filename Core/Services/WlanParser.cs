using System.Text;

namespace NetStuff.Services;

public class WlanBss
{
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public int Rssi { get; set; }
    public int Quality { get; set; }
    public int FrequencyMhz { get; set; }
    public int Channel { get; set; }
    public string Band { get; set; }
    public int WidthMhz { get; set; } = 20;
    /// <summary>Mittenfrequenz des belegten Kanalblocks (bei 40/80/160 MHz ≠ Primärkanal).</summary>
    public double CenterMhz { get; set; }
    public string Security { get; set; }
    public string Standard { get; set; }
    public string Vendor { get; set; }
    public bool Connected { get; set; }
    public double MaxRateMbps { get; set; }

    public string DisplaySsid => string.IsNullOrEmpty(Ssid) ? "(versteckt)" : Ssid;
}

public class WlanException : Exception
{
    public WlanException(string message, uint code) : base(message) { Code = code; }
    public uint Code { get; }
    public bool LocationDenied => Code == 5;
}

/// <summary>Auswertung von WLAN-Beacon-Daten (plattformunabhängig: Windows-WLAN-API, macOS CoreWLAN …).</summary>
public static class WlanParser
{
    public static string DecodeSsid(byte[] b)
    {
        if (b.Length == 0 || b.All(x => x == 0)) return "";
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return Encoding.Latin1.GetString(b); }
    }

    public static (int Channel, string Band) ChannelOf(int mhz)
    {
        if (mhz >= 2400 && mhz < 2500) return (mhz == 2484 ? 14 : (mhz - 2407) / 5, "2,4 GHz");
        if (mhz >= 5925 && mhz < 7200) return ((mhz - 5950) / 5, "6 GHz");
        if (mhz >= 4900 && mhz < 5925) return ((mhz - 5000) / 5, "5 GHz");
        return (0, "?");
    }

    public static double ChannelToMhz(int ch, string band) => band switch
    {
        "2,4 GHz" => ch == 14 ? 2484 : 2407 + ch * 5,
        "6 GHz" => 5950 + ch * 5,
        _ => 5000 + ch * 5,
    };

    public static void ParseIes(WlanBss bss, byte[] ies, ushort capability, int phy)
    {
        bool ht = false, vht = false, he = false, eht = false, wpa1 = false;
        string rsn = null;
        int width = 20;
        double center = bss.FrequencyMhz;

        int i = 0;
        while (i + 2 <= ies.Length)
        {
            int id = ies[i], len = ies[i + 1];
            if (i + 2 + len > ies.Length) break;
            var d = new ReadOnlySpan<byte>(ies, i + 2, len);

            switch (id)
            {
                case 48: // RSN
                    rsn = ParseRsn(d);
                    break;
                case 221: // Vendor (WPA1: 00-50-F2 Typ 1)
                    if (len >= 4 && d[0] == 0x00 && d[1] == 0x50 && d[2] == 0xF2 && d[3] == 1) wpa1 = true;
                    break;
                case 61: // HT Operation
                    ht = true;
                    if (len >= 2 && (d[1] & 0x04) != 0)
                    {
                        int off = d[1] & 0x03;
                        if (off == 1) { width = 40; center = bss.FrequencyMhz + 10; }
                        else if (off == 3) { width = 40; center = bss.FrequencyMhz - 10; }
                    }
                    break;
                case 192: // VHT Operation
                    vht = true;
                    if (len >= 3 && bss.Band == "5 GHz")
                    {
                        int cw = d[0], c0 = d[1], c1 = d[2];
                        if (cw == 1)
                        {
                            if (c1 != 0 && Math.Abs(c1 - c0) == 8) { width = 160; center = ChannelToMhz(c1, bss.Band); }
                            else { width = 80; center = ChannelToMhz(c0, bss.Band); }
                        }
                        else if (cw == 2) { width = 160; center = ChannelToMhz(c0, bss.Band); }
                        else if (cw == 3) { width = 80; center = ChannelToMhz(c0, bss.Band); }
                    }
                    break;
                case 255 when len >= 1:
                    int ext = d[0];
                    if (ext == 35) he = true; // HE Capabilities
                    if (ext == 36) { he = true; ParseHeOperation(bss, d[1..], ref width, ref center); }
                    if (ext == 106 || ext == 108) eht = true;
                    break;
            }
            i += 2 + len;
        }

        bss.WidthMhz = width;
        bss.CenterMhz = center;

        if (rsn != null) bss.Security = wpa1 ? "WPA/" + rsn : rsn;
        else if (wpa1) bss.Security = "WPA";
        else if ((capability & 0x0010) != 0) bss.Security = "WEP";
        else bss.Security = "Offen";

        if (eht || phy == 11) bss.Standard = "Wi-Fi 7 (be)";
        else if (he || phy == 10) bss.Standard = bss.Band == "6 GHz" ? "Wi-Fi 6E (ax)" : "Wi-Fi 6 (ax)";
        else if (vht || phy == 8) bss.Standard = "Wi-Fi 5 (ac)";
        else if (ht || phy == 7) bss.Standard = "Wi-Fi 4 (n)";
        else bss.Standard = bss.Band == "5 GHz" ? "802.11a" : "802.11b/g";
    }

    private static void ParseHeOperation(WlanBss bss, ReadOnlySpan<byte> d, ref int width, ref double center)
    {
        // HE Operation Parameters (3) + BSS Color (1) + Basic HE-MCS/NSS (2) [+ VHT Op Info (3)] [+ Co-Hosted (1)] [+ 6 GHz Op Info (5)]
        if (d.Length < 6) return;
        int pars = d[0] | (d[1] << 8) | (d[2] << 16);
        int off = 6;
        if ((pars & (1 << 14)) != 0) off += 3;
        if ((pars & (1 << 15)) != 0) off += 1;
        if ((pars & (1 << 17)) != 0 && d.Length >= off + 5 && bss.Band == "6 GHz")
        {
            int cw = d[off + 1] & 0x03, c0 = d[off + 2], c1 = d[off + 3];
            switch (cw)
            {
                case 1: width = 40; center = ChannelToMhz(c0, bss.Band); break;
                case 2: width = 80; center = ChannelToMhz(c0, bss.Band); break;
                case 3:
                    width = 160;
                    center = ChannelToMhz(c1 != 0 ? c1 : c0, bss.Band);
                    break;
            }
        }
    }

    private static string ParseRsn(ReadOnlySpan<byte> d)
    {
        try
        {
            // Version(2) GroupCipher(4) PairwiseCount(2) Pairwise(4n) AkmCount(2) Akm(4n)
            int p = 2 + 4;
            int pc = d[p] | (d[p + 1] << 8);
            p += 2 + 4 * pc;
            int ac = d[p] | (d[p + 1] << 8);
            p += 2;
            var akms = new HashSet<string>();
            for (int k = 0; k < ac && p + 4 <= d.Length; k++, p += 4)
            {
                if (d[p] != 0x00 || d[p + 1] != 0x0F || d[p + 2] != 0xAC) continue;
                akms.Add(d[p + 3] switch
                {
                    1 or 3 or 5 => "WPA2-Enterprise",
                    2 or 4 or 6 => "WPA2-Personal",
                    8 or 9 => "WPA3-Personal",
                    11 or 12 or 13 => "WPA3-Enterprise",
                    18 => "OWE",
                    24 or 25 => "WPA3-Personal",
                    _ => "RSN",
                });
            }
            if (akms.Contains("WPA3-Personal") && akms.Contains("WPA2-Personal")) return "WPA2/WPA3-Personal";
            if (akms.Count == 0) return "WPA2";
            return string.Join("/", akms.OrderBy(a => a));
        }
        catch { return "WPA2"; }
    }
}
