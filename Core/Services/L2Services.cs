using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== LLDP / CDP */

public class NeighborInfo
{
    public string Protocol { get; set; }
    public string SourceMac { get; set; }
    public string DeviceName { get; set; } = "";
    public string ChassisId { get; set; } = "";
    public string PortId { get; set; } = "";
    public string PortDescription { get; set; } = "";
    public string Vlan { get; set; } = "";
    public string VoiceVlan { get; set; } = "";
    public string ManagementIp { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Software { get; set; } = "";
    public string Capabilities { get; set; } = "";
    public string Duplex { get; set; } = "";
    public string MaxFrame { get; set; } = "";
    public string AutoNeg { get; set; } = "";
    public string Poe { get; set; } = "";
    public int Ttl { get; set; }
}

/// <summary>
/// LLDP/CDP: Mitschnitt über die Plattform (<see cref="Platform.Lldp"/>, z. B. pktmon unter Windows) und Dekodierung der Rahmen.
/// </summary>
public static class LldpCapture
{
    public static bool IsSupported => Platform.HasLldp;

    public static async Task<List<NeighborInfo>> CaptureAsync(int seconds, CancellationToken ct)
    {
        if (Platform.Lldp == null) throw new PlatformNotSupportedException("LLDP/CDP-Mitschnitt ist auf dieser Plattform nicht verfügbar.");
        var frames = await Platform.Lldp.CaptureFramesAsync(seconds, ct);
        return Decode(frames);
    }

    /// <summary>Rahmen dekodieren und Duplikate (gleicher Nachbar/Port) zusammenfassen.</summary>
    public static List<NeighborInfo> Decode(IEnumerable<byte[]> frames)
    {
        var result = new Dictionary<string, NeighborInfo>();
        foreach (var f in frames)
        {
            var n = Parse(f);
            if (n != null) result[$"{n.Protocol}|{n.SourceMac}|{n.PortId}"] = n;
        }
        return result.Values.ToList();
    }

    public static NeighborInfo Parse(byte[] f)
    {
        if (f.Length < 22) return null;
        var src = NetUtil.FormatMac(f[6..12]);
        int type = (f[12] << 8) | f[13];
        if (type == 0x88CC) return ParseLldp(f.AsSpan(14), src);
        if (type <= 1500 && f.Length > 22 && f[14] == 0xAA && f[15] == 0xAA && f[17] == 0x00 && f[18] == 0x00 && f[19] == 0x0C && f[20] == 0x20 && f[21] == 0x00)
            return ParseCdp(f.AsSpan(22), src);
        return null;
    }

    private static string Str(ReadOnlySpan<byte> b) => Encoding.UTF8.GetString(b).Trim('\0', ' ', '\r', '\n');

    private static NeighborInfo ParseLldp(ReadOnlySpan<byte> d, string src)
    {
        var n = new NeighborInfo { Protocol = "LLDP", SourceMac = src };
        int i = 0;
        while (i + 2 <= d.Length)
        {
            int hdr = (d[i] << 8) | d[i + 1];
            int t = hdr >> 9, len = hdr & 0x1FF;
            if (t == 0 || i + 2 + len > d.Length) break;
            var v = d.Slice(i + 2, len);
            switch (t)
            {
                case 1: n.ChassisId = IdValue(v); break;
                case 2: n.PortId = IdValue(v); break;
                case 3: n.Ttl = len >= 2 ? (v[0] << 8) | v[1] : 0; break;
                case 4: n.PortDescription = Str(v); break;
                case 5: n.DeviceName = Str(v); break;
                case 6: n.Software = Str(v); break;
                case 7: if (len >= 4) n.Capabilities = Caps((v[2] << 8) | v[3], lldp: true); break;
                case 8:
                    if (len > 2 && v[1] == 1 && v[0] >= 5) n.ManagementIp = new IPAddress(v.Slice(2, 4)).ToString();
                    else if (len > 2 && v[1] == 2 && v[0] >= 17) n.ManagementIp = new IPAddress(v.Slice(2, 16)).ToString();
                    break;
                case 127 when len >= 4:
                    var oui = (v[0] << 16) | (v[1] << 8) | v[2];
                    int sub = v[3];
                    var x = v[4..];
                    if (oui == 0x0080C2 && sub == 1 && x.Length >= 2) n.Vlan = ((x[0] << 8) | x[1]).ToString();
                    else if (oui == 0x00120F && sub == 1 && x.Length >= 5)
                    {
                        n.AutoNeg = (x[0] & 0x02) != 0 ? "aktiv" : "aus";
                        int mau = (x[3] << 8) | x[4];
                        n.Duplex = Mau(mau);
                    }
                    else if (oui == 0x00120F && sub == 4 && x.Length >= 2) n.MaxFrame = ((x[0] << 8) | x[1]).ToString();
                    else if (oui == 0x00120F && sub == 2) n.Poe = "PoE (802.3af/at) unterstützt";
                    else if (oui == 0x0012BB && sub == 2 && x.Length >= 3 && x[0] == 1) n.VoiceVlan = (((x[1] << 8) | x[2]) >> 1 & 0xFFF).ToString();
                    break;
            }
            i += 2 + len;
        }
        return n;
    }

    private static string IdValue(ReadOnlySpan<byte> v)
    {
        if (v.Length < 2) return "";
        int sub = v[0];
        var rest = v[1..];
        if ((sub == 4 || sub == 3) && rest.Length == 6) return NetUtil.FormatMac(rest.ToArray());
        if (sub == 5 && rest.Length >= 5 && rest[0] == 1) return new IPAddress(rest.Slice(1, 4)).ToString();
        return Str(rest);
    }

    private static string Caps(int c, bool lldp)
    {
        var names = lldp
            ? new[] { "Other", "Repeater", "Bridge/Switch", "WLAN-AP", "Router", "Telefon", "DOCSIS", "Station", "C-VLAN", "S-VLAN", "TPMR" }
            : new[] { "Router", "Transparent Bridge", "Source-Route Bridge", "Switch", "Host", "IGMP", "Repeater", "Telefon", "Remote", "CVTA", "Two-Port MAC Relay" };
        return string.Join(", ", names.Where((_, i) => (c & (1 << i)) != 0));
    }

    private static string Mau(int mau) => mau switch
    {
        10 => "10 Mbit/s Halbduplex", 11 => "10 Mbit/s Vollduplex", 15 => "100 Mbit/s Halbduplex", 16 => "100 Mbit/s Vollduplex",
        29 => "1 Gbit/s Halbduplex", 30 => "1 Gbit/s Vollduplex", 109 or 110 or 111 or 112 => "10 Gbit/s Vollduplex",
        113 or 114 => "2,5/5 Gbit/s", _ => $"MAU-Typ {mau}",
    };

    private static NeighborInfo ParseCdp(ReadOnlySpan<byte> d, string src)
    {
        var n = new NeighborInfo { Protocol = "CDP", SourceMac = src };
        if (d.Length < 4) return null;
        n.Ttl = d[1];
        int i = 4;
        while (i + 4 <= d.Length)
        {
            int t = (d[i] << 8) | d[i + 1], len = (d[i + 2] << 8) | d[i + 3];
            if (len < 4 || i + len > d.Length) break;
            var v = d.Slice(i + 4, len - 4);
            switch (t)
            {
                case 0x0001: n.DeviceName = Str(v); break;
                case 0x0002:
                case 0x0016:
                    var ip = CdpAddress(v);
                    if (ip != "" && n.ManagementIp == "") n.ManagementIp = ip;
                    break;
                case 0x0003: n.PortId = Str(v); break;
                case 0x0004: if (v.Length >= 4) n.Capabilities = Caps(BinaryPrimitives.ReadInt32BigEndian(v), lldp: false); break;
                case 0x0005: n.Software = Str(v); break;
                case 0x0006: n.Platform = Str(v); break;
                case 0x000A: if (v.Length >= 2) n.Vlan = ((v[0] << 8) | v[1]).ToString(); break;
                case 0x000B: if (v.Length >= 1) n.Duplex = v[0] == 1 ? "Vollduplex" : "Halbduplex"; break;
                case 0x000E: if (v.Length >= 3) n.VoiceVlan = ((v[1] << 8) | v[2]).ToString(); break;
                case 0x0010: if (v.Length >= 2) n.Poe = $"{(v[0] << 8) | v[1]} mW"; break;
            }
            i += len;
        }
        return n;
    }

    private static string CdpAddress(ReadOnlySpan<byte> v)
    {
        if (v.Length < 4) return "";
        int count = BinaryPrimitives.ReadInt32BigEndian(v);
        int p = 4;
        for (int k = 0; k < count && p + 2 < v.Length; k++)
        {
            int plen = v[p + 1];
            int alenPos = p + 2 + plen;
            if (alenPos + 2 > v.Length) break;
            int alen = (v[alenPos] << 8) | v[alenPos + 1];
            var addr = v.Slice(alenPos + 2, Math.Min(alen, v.Length - alenPos - 2));
            if (alen == 4) return new IPAddress(addr).ToString();
            p = alenPos + 2 + alen;
        }
        return "";
    }
}

/// <summary>Minimaler pcapng-Leser (Enhanced/Simple Packet Blocks).</summary>
public static class PcapNg
{
    /// <summary>Liest Ethernet-Rahmen aus pcapng (Windows: pktmon) oder klassischem pcap (macOS/Linux: tcpdump -w).</summary>
    public static List<byte[]> ReadFrames(byte[] data)
    {
        if (data.Length >= 24)
        {
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (m is 0xA1B2C3D4 or 0xA1B23C4D) return ReadClassic(data, true);
            if (m is 0xD4C3B2A1 or 0x4D3CB2A1) return ReadClassic(data, false);
        }
        var frames = new List<byte[]>();
        int i = 0;
        bool le = true;
        while (i + 12 <= data.Length)
        {
            uint type = le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i));
            if (type == 0x0A0D0D0A)
            {
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8));
                le = magic == 0x1A2B3C4D;
            }
            int len = (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 4)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + 4)));
            if (len < 12 || i + len > data.Length) break;
            if (type == 6 && len >= 32)
            {
                int cap = (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 20)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + 20)));
                if (i + 28 + cap <= data.Length) frames.Add(data.AsSpan(i + 28, cap).ToArray());
            }
            else if (type == 3 && len >= 16)
            {
                int cap = Math.Min(len - 16, (int)(le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 8)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i + 8))));
                frames.Add(data.AsSpan(i + 12, cap).ToArray());
            }
            i += len;
        }
        return frames;
    }

    /// <summary>Rahmen mit Zeitstempel (Sekunden) aus klassischem pcap (tcpdump -w); andere Formate ohne Zeit (0).</summary>
    public static List<(byte[] Frame, double Time)> ReadFramesTimed(byte[] data)
    {
        if (data.Length >= 24)
        {
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(data);
            bool? le = m is 0xA1B2C3D4 or 0xA1B23C4D ? true : m is 0xD4C3B2A1 or 0x4D3CB2A1 ? false : null;
            if (le != null)
            {
                bool nano = m is 0xA1B23C4D or 0x4D3CB2A1;
                uint U(int off) => le.Value ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off));
                var list = new List<(byte[], double)>();
                int i = 24;
                while (i + 16 <= data.Length)
                {
                    int cap = (int)U(i + 8);
                    if (cap < 0 || cap > 262144 || i + 16 + cap > data.Length) break;
                    double t = U(i) + U(i + 4) / (nano ? 1e9 : 1e6);
                    list.Add((data.AsSpan(i + 16, cap).ToArray(), t));
                    i += 16 + cap;
                }
                return list;
            }
        }
        return ReadFrames(data).Select(f => (f, 0.0)).ToList();
    }

    private static List<byte[]> ReadClassic(byte[] data, bool le)
    {
        uint U(int off) => le ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off));
        var frames = new List<byte[]>();
        int i = 24;                                  // globaler Kopf
        while (i + 16 <= data.Length)
        {
            int cap = (int)U(i + 8);                 // ts_sec, ts_usec, incl_len, orig_len
            if (cap < 0 || cap > 262144 || i + 16 + cap > data.Length) break;
            frames.Add(data.AsSpan(i + 16, cap).ToArray());
            i += 16 + cap;
        }
        return frames;
    }
}

/* ====================================================================== DHCP */

public class DhcpOffer
{
    public string Server { get; set; } = "";
    public string RelayOrSource { get; set; } = "";
    public string OfferedIp { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string Router { get; set; } = "";
    public string Dns { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Lease { get; set; } = "";
    public string Ntp { get; set; } = "";
    public string BootServer { get; set; } = "";
    public string BootFile { get; set; } = "";
    public string Other { get; set; } = "";
    public double Ms { get; set; }
}

/// <summary>Sendet ein DHCPDISCOVER und sammelt alle Angebote (OFFER) – findet auch fremde DHCP-Server.</summary>
public static class DhcpTester
{
    public static async Task<List<DhcpOffer>> DiscoverAsync(IPAddress localIp, byte[] mac, int waitMs, CancellationToken ct, int interfaceIndex = 0)
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsMacOS())
            try { sock.SetRawSocketOption(0xFFFF /* SOL_SOCKET */, 0x0200 /* SO_REUSEPORT */, BitConverter.GetBytes(1)); } catch { }
        sock.EnableBroadcast = true;
        if (!OperatingSystem.IsWindows())
        {
            // macOS/Linux: an eine Unicast-Adresse gebundene Sockets empfangen keine Broadcast-Antworten –
            // daher an alle Adressen binden und unter macOS per IP_BOUND_IF an den gewählten Adapter koppeln.
            localIp = null;
            if (OperatingSystem.IsMacOS() && interfaceIndex > 0)
                try { sock.SetRawSocketOption(0 /* IPPROTO_IP */, 25 /* IP_BOUND_IF */, BitConverter.GetBytes(interfaceIndex)); } catch { }
        }
        try { sock.Bind(new IPEndPoint(localIp ?? IPAddress.Any, 68)); }
        catch (SocketException)
        {
            try { sock.Bind(new IPEndPoint(IPAddress.Any, 68)); }
            catch (SocketException ex)
            {
                // macOS: der System-DHCP-Client belegt Port 68 exklusiv → Antworten per Paketmitschnitt empfangen
                if (Platform.Capture != null) return await DiscoverViaCaptureAsync(mac, waitMs, interfaceIndex, ct);
                throw new Exception(L.T("UDP-Port 68 ist belegt und kann nicht verwendet werden: ") + NetUtil.ErrorText(ex));
            }
        }

        uint xid = (uint)Random.Shared.Next();
        var pkt = BuildDiscover(xid, mac);
        var sw = Stopwatch.StartNew();
        await sock.SendToAsync(pkt, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, 67), ct);

        var offers = new List<DhcpOffer>();
        var buf = new byte[1500];
        var until = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(until - DateTime.UtcNow);
            SocketReceiveFromResult r;
            try { r = await sock.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token); }
            catch (OperationCanceledException) { break; }
            var o = Parse(buf.AsSpan(0, r.ReceivedBytes), xid);
            if (o == null) continue;
            o.Ms = sw.Elapsed.TotalMilliseconds;
            o.RelayOrSource = ((IPEndPoint)r.RemoteEndPoint).Address.ToString();
            if (!offers.Any(x => x.Server == o.Server && x.OfferedIp == o.OfferedIp)) offers.Add(o);
        }
        return offers;
    }

    /// <summary>DISCOVER von einem freien Port senden und die Antworten (an Port 68) per Mitschnitt auswerten.</summary>
    private static async Task<List<DhcpOffer>> DiscoverViaCaptureAsync(byte[] mac, int waitMs, int interfaceIndex, CancellationToken ct)
    {
        uint xid = (uint)Random.Shared.Next();
        var pkt = BuildDiscover(xid, mac);
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.EnableBroadcast = true;
        if (OperatingSystem.IsMacOS() && interfaceIndex > 0)
            try { sock.SetRawSocketOption(0 /* IPPROTO_IP */, 25 /* IP_BOUND_IF */, BitConverter.GetBytes(interfaceIndex)); } catch { }
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        int seconds = Math.Max(3, (int)Math.Ceiling(waitMs / 1000.0)) + 1;
        var frames = await Platform.Capture.CaptureAsync("udp and (port 67 or port 68)", seconds,
            L.T("Net-Stuff möchte die Antworten der DHCP-Server mitschneiden (tcpdump) – macOS belegt den DHCP-Port 68 selbst."),
            async () => await sock.SendToAsync(pkt, SocketFlags.None, new IPEndPoint(IPAddress.Broadcast, 67), ct), ct);
        return OffersFromFrames(frames, xid);
    }

    /// <summary>Angebote zur Transaktion <paramref name="xid"/> aus mitgeschnittenen Rahmen; Antwortzeit ab dem eigenen DISCOVER.</summary>
    internal static List<DhcpOffer> OffersFromFrames(IEnumerable<(byte[] Frame, double Time)> frames, uint xid)
    {
        double? sent = null;
        var offers = new List<(DhcpOffer Offer, double Time)>();
        foreach (var (frame, time) in frames)
        {
            if (!TryUdpPayload(frame, out var src, out int dstPort, out var payload)) continue;
            if (dstPort == 67 && payload.Length > 8 && payload[0] == 1 && BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(4)) == xid) { sent ??= time; continue; }
            var o = Parse(payload, xid);
            if (o == null) continue;
            o.RelayOrSource = src.ToString();
            if (!offers.Any(x => x.Offer.Server == o.Server && x.Offer.OfferedIp == o.OfferedIp)) offers.Add((o, time));
        }
        foreach (var (o, t) in offers) o.Ms = sent != null && t >= sent ? (t - sent.Value) * 1000 : 0;
        return offers.Select(x => x.Offer).ToList();
    }

    /// <summary>UDP-Nutzdaten aus einem Ethernet/IPv4-Rahmen (auch mit VLAN-Tag).</summary>
    internal static bool TryUdpPayload(byte[] f, out IPAddress src, out int dstPort, out byte[] payload)
    {
        src = null; dstPort = 0; payload = null;
        if (f.Length < 14) return false;
        int off = 12;
        int type = f[off] << 8 | f[off + 1];
        if (type == 0x8100 && f.Length >= 18) { off += 4; type = f[off] << 8 | f[off + 1]; }
        if (type != 0x0800) return false;
        int ip = off + 2;
        if (f.Length < ip + 20 || (f[ip] >> 4) != 4 || f[ip + 9] != 17) return false;
        int ihl = (f[ip] & 0x0F) * 4;
        int udp = ip + ihl;
        if (f.Length < udp + 8) return false;
        src = new IPAddress(f.AsSpan(ip + 12, 4));
        dstPort = f[udp + 2] << 8 | f[udp + 3];
        int len = Math.Min((f[udp + 4] << 8 | f[udp + 5]) - 8, f.Length - udp - 8);
        if (len <= 0) return false;
        payload = f.AsSpan(udp + 8, len).ToArray();
        return true;
    }

    private static byte[] BuildDiscover(uint xid, byte[] mac)
    {
        var p = new List<byte>();
        p.AddRange(new byte[] { 1, 1, 6, 0 });
        p.AddRange(BitConverter.GetBytes(xid).Reverse());
        p.AddRange(new byte[] { 0, 0, 0x80, 0x00 }); // secs, flags=broadcast
        p.AddRange(new byte[16]);                    // ciaddr, yiaddr, siaddr, giaddr
        var ch = new byte[16];
        Array.Copy(mac, ch, Math.Min(6, mac.Length));
        p.AddRange(ch);
        p.AddRange(new byte[192]);                   // sname + file
        p.AddRange(new byte[] { 99, 130, 83, 99 });  // magic cookie
        p.AddRange(new byte[] { 53, 1, 1 });         // DHCPDISCOVER
        p.AddRange(new byte[] { 61, 7, 1 }); p.AddRange(mac.Take(6));
        p.AddRange(new byte[] { 12, 9 }); p.AddRange(Encoding.ASCII.GetBytes("net-stuff"));
        p.AddRange(new byte[] { 55, 10, 1, 3, 6, 15, 42, 51, 54, 58, 66, 67 });
        p.Add(255);
        while (p.Count < 300) p.Add(0);
        return p.ToArray();
    }

    private static DhcpOffer Parse(ReadOnlySpan<byte> d, uint xid)
    {
        if (d.Length < 240 || d[0] != 2) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4)) != xid) return null;
        if (d[236] != 99 || d[237] != 130 || d[238] != 83 || d[239] != 99) return null;
        var o = new DhcpOffer { OfferedIp = new IPAddress(d.Slice(16, 4)).ToString() };
        var siaddr = new IPAddress(d.Slice(20, 4));
        var sname = Encoding.ASCII.GetString(d.Slice(44, 64)).Trim('\0');
        var file = Encoding.ASCII.GetString(d.Slice(108, 128)).Trim('\0');
        if (!siaddr.Equals(IPAddress.Any)) o.BootServer = siaddr.ToString();
        if (sname != "") o.BootServer = (o.BootServer + " " + sname).Trim();
        if (file != "") o.BootFile = file;

        int msgType = 0;
        var other = new List<string>();
        int i = 240;
        while (i < d.Length)
        {
            int opt = d[i];
            if (opt == 255) break;
            if (opt == 0) { i++; continue; }
            if (i + 1 >= d.Length) break;
            int len = d[i + 1];
            if (i + 2 + len > d.Length) break;
            var v = d.Slice(i + 2, len);
            var arr = v.ToArray();
            string Ips() => string.Join(", ", Enumerable.Range(0, arr.Length / 4).Select(k => new IPAddress(arr.AsSpan(k * 4, 4)).ToString()));
            switch (opt)
            {
                case 53: msgType = v[0]; break;
                case 54: o.Server = new IPAddress(v.Slice(0, 4)).ToString(); break;
                case 1: o.SubnetMask = new IPAddress(v.Slice(0, 4)).ToString(); break;
                case 3: o.Router = Ips(); break;
                case 6: o.Dns = Ips(); break;
                case 15: o.Domain = Encoding.ASCII.GetString(v).Trim('\0'); break;
                case 42: o.Ntp = Ips(); break;
                case 51: var s = BinaryPrimitives.ReadUInt32BigEndian(v); o.Lease = s == 86400 ? L.T("1 Tag") : s >= 86400 ? L.F("{0} Tage", (s / 86400.0).ToString("0.#")) : $"{s / 3600.0:0.#} h"; break;
                case 66: o.BootServer = Encoding.ASCII.GetString(v).Trim('\0'); break;
                case 67: o.BootFile = Encoding.ASCII.GetString(v).Trim('\0'); break;
                case 58: case 59: break;
                default: other.Add($"Option {opt}"); break;
            }
            i += 2 + len;
        }
        if (msgType != 2) return null; // nur OFFER
        o.Other = string.Join(", ", other);
        return o;
    }
}
