using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

public static class NetUtil
{
    /// <summary>Gemeinsamer HttpClient (ohne Redirect-Automatik für Header-Analysen gibt es eigene Clients).</summary>
    public static readonly HttpClient Http = CreateHttp();

    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 Net-Stuff/1.0";

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8),
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return h;
    }

    /// <summary>Hostname oder IP auflösen (bevorzugt IPv4).</summary>
    public static async Task<IPAddress> ResolveAsync(string host, bool preferV6 = false, CancellationToken ct = default)
    {
        host = CleanHost(host);
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = await Dns.GetHostAddressesAsync(host, ct);
        var fam = preferV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        return addrs.FirstOrDefault(a => a.AddressFamily == fam)
               ?? addrs.FirstOrDefault()
               ?? throw new Exception($"'{host}' konnte nicht aufgelöst werden.");
    }

    /// <summary>Entfernt Schema, Pfad und Port aus einer Eingabe.</summary>
    public static string CleanHost(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Contains("://") && Uri.TryCreate(s, UriKind.Absolute, out var u)) return u.Host.Trim('[', ']');
        var slash = s.IndexOf('/');
        if (slash > 0) s = s[..slash];
        if (s.StartsWith('[') && s.Contains(']')) return s[1..s.IndexOf(']')];
        if (s.Count(c => c == ':') == 1) s = s[..s.IndexOf(':')];
        return s.TrimEnd('.');
    }

    private static readonly DnsClient.LookupClient PtrClient = new(new DnsClient.LookupClientOptions
    {
        UseCache = true,
        Timeout = TimeSpan.FromMilliseconds(1500),
        Retries = 1,
        ThrowDnsErrors = false,
    });

    public static async Task<string> ReverseDnsAsync(IPAddress ip, int timeoutMs = 2000)
    {
        // 1. Direkte PTR-Abfrage über DNS
        try
        {
            var r = await PtrClient.QueryReverseAsync(ip);
            var ptr = r.Answers.PtrRecords().FirstOrDefault()?.PtrDomainName.Value.TrimEnd('.');
            if (!string.IsNullOrEmpty(ptr)) return ptr;
        }
        catch { }
        // 2. mDNS/Bonjour direkt beim Gerät (Apple-Geräte, Drucker, Linux/Avahi, viele IoT-Geräte)
        if (IsPrivate(ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var mdns = await MdnsReverseAsync(ip, Math.Min(timeoutMs, 1200));
            if (mdns != "") return mdns;
        }
        // 3. Namensauflösung des Systems (Windows inkl. LLMNR/NetBIOS im LAN)
        try
        {
            var task = Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) == task)
            {
                var name = task.Result.HostName;
                return name == ip.ToString() ? "" : name;
            }
        }
        catch { }
        return "";
    }

    /// <summary>
    /// PTR-Abfrage per mDNS als „Legacy Unicast“ direkt an Port 5353 des Geräts (RFC 6762 6.7) –
    /// der Responder antwortet per Unicast an den Absenderport. Liefert z. B. „MacBook-Pro.local“.
    /// </summary>
    public static async Task<string> MdnsReverseAsync(IPAddress ip, int timeoutMs = 1200)
    {
        try
        {
            var b = ip.GetAddressBytes();
            var name = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
            var q = new List<byte> { (byte)Random.Shared.Next(256), (byte)Random.Shared.Next(256), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in name.Split('.')) { q.Add((byte)label.Length); q.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
            q.AddRange(new byte[] { 0, 0, 12, 0, 1 }); // PTR, IN

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(q.ToArray(), q.Count, new IPEndPoint(ip, 5353));
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var d = r.Buffer;
                if (d.Length < 12 || d[0] != q[0] || d[1] != q[1] || (d[2] & 0x80) == 0) continue;
                int qd = d[4] << 8 | d[5], an = d[6] << 8 | d[7];
                int off = 12;
                for (int i = 0; i < qd; i++) { SkipName(d, ref off); off += 4; }
                for (int i = 0; i < an && off + 10 <= d.Length; i++)
                {
                    SkipName(d, ref off);
                    int type = d[off] << 8 | d[off + 1];
                    int len = d[off + 8] << 8 | d[off + 9];
                    off += 10;
                    if (type == 12)
                    {
                        int p = off;
                        var host = ReadName(d, ref p).TrimEnd('.');
                        if (host != "") return host;
                    }
                    off += len;
                }
                return "";
            }
        }
        catch { return ""; }
    }

    private static void SkipName(byte[] d, ref int off)
    {
        while (off < d.Length)
        {
            int l = d[off];
            if (l == 0) { off++; return; }
            if ((l & 0xC0) == 0xC0) { off += 2; return; }
            off += l + 1;
        }
    }

    private static string ReadName(byte[] d, ref int off)
    {
        var parts = new List<string>();
        int pos = off, jumps = 0;
        bool jumped = false;
        while (pos < d.Length && jumps < 20)
        {
            int l = d[pos];
            if (l == 0) { pos++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (pos + 1 >= d.Length) break;
                if (!jumped) off = pos + 2;
                jumped = true; jumps++;
                pos = (l & 0x3F) << 8 | d[pos + 1];
                continue;
            }
            if (pos + 1 + l > d.Length) break;
            parts.Add(System.Text.Encoding.UTF8.GetString(d, pos + 1, l));
            pos += l + 1;
        }
        if (!jumped) off = pos;
        return string.Join(".", parts);
    }

    /// <summary>
    /// Verständliche (übersetzte) Fehlermeldung statt der Systemtexte von Windows/macOS,
    /// z. B. „nodename nor servname provided, or not known“ → „Name konnte nicht aufgelöst werden …“.
    /// </summary>
    public static string ErrorText(Exception ex)
    {
        if (ex == null) return "";
        if (ex is AggregateException ag && ag.InnerExceptions.Count == 1) return ErrorText(ag.InnerException);

        // innerste Netzwerk-Ursache suchen
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException se) return L.T(SocketText(se.SocketErrorCode)) is { Length: > 0 } t ? t : L.T(se.Message);
            if (e is TimeoutException) return L.T("Zeitüberschreitung – keine Antwort.");
            if (e is System.Security.Authentication.AuthenticationException ae)
                return L.T("TLS-Aushandlung fehlgeschlagen: ") + L.T(ae.InnerException?.Message ?? ae.Message);
        }
        if (ex is TaskCanceledException or OperationCanceledException) return L.T("Zeitüberschreitung – keine Antwort.");
        if (ex is HttpRequestException { StatusCode: { } code }) return $"HTTP {(int)code} {code}";

        var msg = ex.InnerException?.Message ?? ex.Message;
        if (msg.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase) || msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("No such host is known", StringComparison.OrdinalIgnoreCase))
            return L.T(SocketText(SocketError.HostNotFound));
        return L.T(msg);
    }

    private static string SocketText(SocketError code) => code switch
    {
        SocketError.HostNotFound or SocketError.NoData => "Name konnte nicht aufgelöst werden – unbekannter Hostname oder DNS-Problem.",
        SocketError.TryAgain => "DNS-Server antwortet nicht (vorübergehender Fehler).",
        SocketError.ConnectionRefused => "Verbindung abgelehnt – der Port ist geschlossen.",
        SocketError.TimedOut => "Zeitüberschreitung – keine Antwort.",
        SocketError.HostUnreachable or SocketError.HostDown => "Host nicht erreichbar.",
        SocketError.NetworkUnreachable or SocketError.NetworkDown => "Netzwerk nicht erreichbar – besteht eine Verbindung?",
        SocketError.ConnectionReset => "Verbindung von der Gegenstelle zurückgesetzt.",
        SocketError.ConnectionAborted => "Verbindung abgebrochen.",
        SocketError.AccessDenied => "Zugriff verweigert – fehlende Berechtigung.",
        SocketError.AddressNotAvailable => "Adresse auf diesem Gerät nicht verfügbar.",
        SocketError.AddressAlreadyInUse => "Port wird bereits verwendet.",
        _ => "",
    };

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
            return false;
        }
        var b = ip.GetAddressBytes();
        return b[0] == 10
               || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
               || (b[0] == 192 && b[1] == 168)
               || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT
               || b[0] == 127 || b[0] == 0 || b[0] >= 224;
    }

    public static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static IPAddress FromUInt(uint v) =>
        new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    public static uint PrefixToMask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    public static int MaskToPrefix(uint mask)
    {
        int p = 0;
        while ((mask & 0x80000000) != 0) { p++; mask <<= 1; }
        return p;
    }

    /// <summary>Parst "192.168.1.0/24", "10.0.0.1-10.0.0.50", "10.0.0.1-50" oder eine Einzel-IP.</summary>
    public static List<IPAddress> ParseRange(string text, int max = 65536)
    {
        text = (text ?? "").Trim();
        uint start, end;
        if (text.Contains('/'))
        {
            var parts = text.Split('/');
            var ip = IPAddress.Parse(parts[0].Trim());
            int prefix = int.Parse(parts[1].Trim());
            if (prefix < 8 || prefix > 32) throw new Exception("Präfix muss zwischen /8 und /32 liegen.");
            var mask = PrefixToMask(prefix);
            var net = ToUInt(ip) & mask;
            if (prefix >= 31) { start = net; end = net | ~mask; }
            else { start = net + 1; end = (net | ~mask) - 1; }
        }
        else if (text.Contains('-'))
        {
            var parts = text.Split('-');
            var a = IPAddress.Parse(parts[0].Trim());
            start = ToUInt(a);
            var right = parts[1].Trim();
            if (IPAddress.TryParse(right, out var b) && right.Contains('.')) end = ToUInt(b);
            else end = (start & 0xFFFFFF00) | byte.Parse(right);
        }
        else
        {
            start = end = ToUInt(IPAddress.Parse(text));
        }
        if (end < start) (start, end) = (end, start);
        if (end - start + 1 > max) throw new Exception($"Bereich zu groß (max. {max} Adressen).");
        var list = new List<IPAddress>((int)(end - start + 1));
        for (uint v = start; v <= end && v >= start; v++)
        {
            list.Add(FromUInt(v));
            if (v == uint.MaxValue) break;
        }
        return list;
    }

    /// <summary>Aktive IPv4-Schnittstellen mit Gateway.</summary>
    public static IEnumerable<(NetworkInterface Nic, UnicastIPAddressInformation Addr, IPAddress Gateway)> ActiveIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            var gw = props.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                yield return (nic, a, gw);
        }
    }

    /// <summary>Wahrscheinlichstes Heimnetz (Schnittstelle mit Gateway).</summary>
    public static string DefaultScanRange()
    {
        var best = ActiveIPv4().OrderByDescending(x => x.Gateway != null).FirstOrDefault();
        if (best.Addr == null) return "192.168.1.0/24";
        int prefix = Math.Max(best.Addr.PrefixLength, 22);
        var net = ToUInt(best.Addr.Address) & PrefixToMask(prefix);
        return $"{FromUInt(net)}/{prefix}";
    }

    public static string FormatMac(byte[] mac) =>
        mac == null || mac.Length == 0 ? "" : string.Join(":", mac.Select(b => b.ToString("X2")));

    public static string FormatMac(PhysicalAddress pa) => FormatMac(pa?.GetAddressBytes());

    /// <summary>MAC-Adresse per ARP ermitteln (nur im lokalen Subnetz; null, wenn die Plattform das nicht kann).</summary>
    public static byte[] Arp(IPAddress ip)
    {
        try { return Platform.Arp?.Resolve(ip); }
        catch { return null; }
    }

    public static async Task<long?> PingAsync(IPAddress ip, int timeoutMs, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return null;
        try
        {
            // Bewusst ohne CancellationToken: Abbruch laufender ICMP-Requests kann freigegebenen Speicher beschreiben.
            using var p = new Ping();
            var r = await p.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs));
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    public static async Task<bool> TcpConnectAsync(IPAddress ip, int port, int timeoutMs, CancellationToken ct = default)
    {
        using var client = new TcpClient(ip.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    public static void OpenUrl(string url)
    {
        if (Platform.UrlOpener != null) { try { Platform.UrlOpener(url); } catch { } return; }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    public static string FormatSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "–";
        if (bitsPerSecond >= 1_000_000_000) return $"{bitsPerSecond / 1e9:0.#} Gbit/s";
        if (bitsPerSecond >= 1_000_000) return $"{bitsPerSecond / 1e6:0.#} Mbit/s";
        return $"{bitsPerSecond / 1e3:0.#} kbit/s";
    }
}
