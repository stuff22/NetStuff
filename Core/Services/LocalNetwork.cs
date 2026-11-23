using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== Netzwerkscanner */

public class ScanHost : Bindable
{
    private string _hostname = "", _mac = "", _vendor = "", _type = "", _ports = "", _info = "";
    private long? _ping;

    public IPAddress Address { get; init; }
    public string Ip => Address.ToString();
    public uint SortKey => NetUtil.ToUInt(Address);
    public bool IsSelf { get; set; }
    public bool IsGateway { get; set; }
    public List<int> OpenPorts { get; } = new();
    public SsdpDevice Ssdp { get; set; }
    public string NetBiosName { get; set; } = "";
    public string Workgroup { get; set; } = "";

    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public string Mac { get => _mac; set => Set(ref _mac, value); }
    public string Vendor { get => _vendor; set => Set(ref _vendor, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Ports { get => _ports; set => Set(ref _ports, value); }
    public string Info { get => _info; set => Set(ref _info, value); }

    public long? Ping
    {
        get => _ping;
        set { Set(ref _ping, value); Raise(nameof(PingText)); Raise(nameof(PingSort)); }
    }

    public string PingText => _ping.HasValue ? $"{_ping} ms" : "–";
    public long PingSort => _ping ?? long.MaxValue;
}

public class NetScanSettings
{
    public int TimeoutMs { get; set; } = 700;
    public bool CheckPorts { get; set; } = true;
    public int Parallel { get; set; } = 128;
}

/// <summary>
/// Findet Geräte in einem IPv4-Bereich: Ping + ARP (über Platform.Arp, nur im lokalen Subnetz), danach Reverse-DNS,
/// NetBIOS, Schnellportscan und UPnP/SSDP; Hersteller über die OUI-Liste, Gerätetyp heuristisch.
/// </summary>
public static class NetworkScanner
{
    /// <summary>
    /// <paramref name="found"/> wird für jedes gefundene Gerät aufgerufen (Details folgen per PropertyChanged),
    /// <paramref name="progress"/> mit (geprüft, gefunden). Rückgabe: alle Geräte nach IP sortiert.
    /// </summary>
    public static async Task<List<ScanHost>> ScanAsync(IReadOnlyList<IPAddress> ips, NetScanSettings s, Action<ScanHost> found,
        Action<int, int> progress, Action<string> status, CancellationToken ct)
    {
        var locals = NetUtil.ActiveIPv4().ToList();
        var selfIps = locals.Select(l => l.Addr.Address.ToString()).ToHashSet();
        var gateways = locals.Where(l => l.Gateway != null).Select(l => l.Gateway.ToString()).ToHashSet();
        bool IsLocalSubnet(IPAddress ip) => locals.Any(l =>
        {
            var mask = NetUtil.PrefixToMask(l.Addr.PrefixLength);
            return (NetUtil.ToUInt(l.Addr.Address) & mask) == (NetUtil.ToUInt(ip) & mask);
        });

        await OuiDatabase.EnsureLoadedAsync();
        status?.Invoke(L.T("Suche Geräte …"));
        var ssdpTask = SsdpDiscovery.DiscoverAsync(3500, ct);

        var hosts = new List<ScanHost>();
        using var gate = new SemaphoreSlim(Math.Max(1, s.Parallel));
        int done = 0;
        var detailTasks = new List<Task>();

        var tasks = ips.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var pingTask = NetUtil.PingAsync(ip, s.TimeoutMs, ct);
                byte[] mac = null;
                if (Platform.HasArp && IsLocalSubnet(ip)) mac = await Task.Run(() => NetUtil.Arp(ip), ct);
                var rtt = await pingTask;
                bool self = selfIps.Contains(ip.ToString());
                if (rtt == null && mac == null && !self) return;

                var host = new ScanHost { Address = ip, IsSelf = self, IsGateway = gateways.Contains(ip.ToString()), Ping = rtt };
                if (self)
                {
                    var nic = locals.First(l => l.Addr.Address.Equals(ip)).Nic;
                    mac = nic.GetPhysicalAddress().GetAddressBytes();
                    host.Ping ??= 0;
                }
                if (mac != null && mac.Any(b => b != 0)) host.Mac = NetUtil.FormatMac(mac);
                host.Vendor = OuiDatabase.Lookup(host.Mac);
                lock (hosts) hosts.Add(host);
                found?.Invoke(host);
                lock (detailTasks) detailTasks.Add(DetailsAsync(host, s.CheckPorts, ct));
            }
            catch (OperationCanceledException) { }
            finally
            {
                gate.Release();
                int d = Interlocked.Increment(ref done);
                if (d % 8 == 0 || d == ips.Count) { int c; lock (hosts) c = hosts.Count; progress?.Invoke(d, c); }
            }
        });
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        status?.Invoke(L.F("{0} Geräte gefunden – ermittle Details …", hosts.Count));
        Task[] dt;
        lock (detailTasks) dt = detailTasks.ToArray();
        await Task.WhenAll(dt);

        var ssdp = await ssdpTask;
        foreach (var h in hosts)
        {
            if (ssdp.TryGetValue(h.Ip, out var dev)) h.Ssdp = dev;
            UpdateInfo(h);
        }
        return hosts.OrderBy(h => h.SortKey).ToList();
    }

    private static async Task DetailsAsync(ScanHost h, bool checkPorts, CancellationToken ct)
    {
        var dnsTask = NetUtil.ReverseDnsAsync(h.Address, 2500);
        var nbTask = NetBiosClient.QueryAsync(h.Address, 1200);
        Task<int[]> portTask = Task.FromResult(Array.Empty<int>());
        if (checkPorts)
        {
            portTask = Task.WhenAll(PortCatalog.Quick.Select(async p => await NetUtil.TcpConnectAsync(h.Address, p, 600, ct) ? p : 0))
                .ContinueWith(t => t.Result.Where(p => p > 0).ToArray(), ct);
        }

        var dns = await dnsTask;
        var nb = await nbTask;
        h.NetBiosName = nb.Name;
        h.Hostname = dns != "" ? dns : nb.Name;
        h.Workgroup = nb.Group;

        try
        {
            var ports = await portTask;
            h.OpenPorts.AddRange(ports);
            h.Ports = string.Join(", ", ports.Select(p => PortCatalog.Name(p) is { Length: > 0 } n ? $"{p} {n}" : p.ToString()));
        }
        catch { }
        UpdateInfo(h);
    }

    private static void UpdateInfo(ScanHost h)
    {
        var parts = new List<string>();
        if (h.Ssdp != null && h.Ssdp.Summary != "") parts.Add(h.Ssdp.Summary);
        if (h.NetBiosName != "" && !h.Hostname.StartsWith(h.NetBiosName, StringComparison.OrdinalIgnoreCase)) parts.Add("NetBIOS " + h.NetBiosName);
        if (h.Workgroup != "") parts.Add(L.T("Arbeitsgruppe ") + h.Workgroup);
        h.Info = string.Join(" · ", parts.Distinct());
        h.Type = L.T(DeviceClassifier.Classify(h));
    }

    /// <summary>CSV (Semikolon, für Excel) der Scan-Ergebnisse.</summary>
    public static string ToCsv(IEnumerable<ScanHost> hosts)
    {
        var sb = new StringBuilder("IP;Hostname;MAC;Hersteller;Typ;Offene Ports;Ping ms;Info\n");
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var h in hosts)
            sb.AppendLine(string.Join(";", Q(h.Ip), Q(h.Hostname), Q(h.Mac), Q(h.Vendor), Q(h.Type), Q(h.Ports), h.Ping?.ToString() ?? "", Q(h.Info)));
        return sb.ToString();
    }

    public static string OuiStatus() => OuiDatabase.Count > 0
        ? L.F("Herstellerliste: {0:N0} Einträge (Stand {1:dd.MM.yyyy})", OuiDatabase.Count, OuiDatabase.CacheDate)
        : L.T("Herstellerliste nicht verfügbar (keine Internetverbindung?)");
}

/// <summary>Heuristische Geräteerkennung aus Hersteller, Ports, Hostname und UPnP-Daten.</summary>
public static class DeviceClassifier
{
    public static string Classify(ScanHost h)
    {
        if (h.IsSelf) return "Dieses Gerät";
        var v = (h.Vendor ?? "").ToLowerInvariant();
        var n = (h.Hostname ?? "").ToLowerInvariant();
        var s = ((h.Ssdp?.DeviceType ?? "") + " " + (h.Ssdp?.Summary ?? "") + " " + (h.Ssdp?.Server ?? "")).ToLowerInvariant();
        var p = h.OpenPorts;
        bool Has(params int[] ports) => ports.Any(p.Contains);
        bool V(params string[] k) => k.Any(v.Contains);
        bool N(params string[] k) => k.Any(n.Contains);
        bool S(params string[] k) => k.Any(s.Contains);

        if (h.IsGateway || S("internetgatewaydevice", "fritz!box", "router") || N("fritz.box", "router", "gateway", "speedport"))
            return "Router / Gateway";
        if (S("mediarenderer", "tv", "bravia", "webos", "tizen") || V("lg innotek", "samsung electronics", "tcl", "hisense") && Has(8008, 8009, 9080))
            return "Smart-TV / Mediaplayer";
        if (Has(8009) || S("chromecast", "google cast")) return "Chromecast / Google Cast";
        if (V("sonos") || S("sonos")) return "Sonos-Lautsprecher";
        if (Has(9100, 631, 515) || S("printer", "drucker") || V("brother", "canon", "epson", "hewlett", "kyocera", "lexmark", "ricoh", "xerox"))
            return "Drucker";
        if (Has(554) || V("hikvision", "dahua", "axis comm", "reolink", "ubiquiti") && Has(554) || S("camera", "nvr"))
            return "Kamera / NVR";
        if (V("synology", "qnap", "western digital", "asustor") || Has(5000, 5001) && Has(445) || S("nas"))
            return "NAS";
        if (Has(8123) || N("homeassistant")) return "Home Assistant";
        if (V("raspberry")) return "Raspberry Pi";
        if (V("espressif", "tuya", "shelly", "tp-link") && !Has(445, 3389) || Has(1883)) return "IoT-Gerät";
        if (V("avm", "lancom", "mikrotik", "cisco", "juniper", "aruba", "zyxel", "netgear", "d-link", "draytek") && Has(80, 443, 22, 23))
            return "Netzwerkgerät";
        if (V("ubiquiti")) return "Ubiquiti (AP/Switch)";
        if (Has(62078) || V("apple") && !Has(445, 22)) return N("ipad") ? "iPad" : N("iphone") ? "iPhone" : "Apple-Gerät";
        if (Has(3389) || (Has(135, 139, 445) && !Has(22))) return "Windows-PC/Server";
        if (Has(548) || N("macbook", "imac", "mac-mini")) return "Mac";
        if (Has(22) && Has(445)) return "Linux-Server / NAS";
        if (Has(22)) return "Linux / Netzwerkgerät";
        if (V("amazon")) return "Amazon-Gerät (Echo/Fire)";
        if (V("nintendo", "sony interactive", "microsoft") && !Has(445)) return "Spielkonsole";
        if (OuiDatabase.IsRandomized(h.Mac)) return "Smartphone / Tablet (private MAC)";
        if (V("samsung", "xiaomi", "huawei", "oneplus", "oppo", "motorola", "google")) return "Smartphone / Tablet";
        if (Has(80, 443)) return "Gerät mit Weboberfläche";
        return "";
    }
}

/* ====================================================================== Übersicht */

public class AdapterInfo
{
    public string Name { get; init; }
    public string Description { get; init; }
    public bool Up { get; init; }
    public string Status => L.T(Up ? "Verbunden" : "Getrennt");
    public List<CheckItem> Items { get; init; }
}

/// <summary>Netzwerkadapter des Geräts und öffentliche Anbindung (IP, Provider, Standort).</summary>
public static class NetworkOverview
{
    public static List<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .Where(n => !Regex.IsMatch(n.Description, @"-\d{4}$")) // Windows: NDIS-Filter (WFP, QoS …)
                     .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                     .ThenByDescending(n => Safe(() => n.GetIPProperties().GatewayAddresses.Count, 0) > 0))
        {
            var up = nic.OperationalStatus == OperationalStatus.Up;
            var items = new List<CheckItem>
            {
                new("Typ", TypeName(nic.NetworkInterfaceType)),
                new("MAC-Adresse", NetUtil.FormatMac(nic.GetPhysicalAddress())),
            };
            if (up && nic.Speed > 0) items.Add(new CheckItem("Geschwindigkeit", NetUtil.FormatSpeed(nic.Speed)));
            try
            {
                var props = nic.GetIPProperties();
                foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    bool apipa = a.Address.ToString().StartsWith("169.254.");
                    items.Add(new CheckItem("IPv4", $"{a.Address}/{a.PrefixLength}" + (apipa ? L.T("  (APIPA – kein DHCP!)") : ""), apipa ? Level.Warn : Level.Info));
                }
                foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6))
                    items.Add(new CheckItem("IPv6", $"{a.Address}/{a.PrefixLength}"));
                var gws = props.GatewayAddresses.Select(g => g.Address.ToString()).Where(s => s != "0.0.0.0").ToList();
                if (gws.Count > 0) items.Add(new CheckItem("Gateway", string.Join(", ", gws)));
                var dns = Safe(() => props.DnsAddresses.Select(d => d.ToString()).ToList(), new List<string>());
                if (dns.Count > 0) items.Add(new CheckItem("DNS-Server", string.Join(", ", dns)));
                // Nicht alle Plattformen liefern DHCP/MTU (macOS: teilweise PlatformNotSupported)
                try
                {
                    var v4 = props.GetIPv4Properties();
                    if (v4 != null)
                    {
                        items.Add(new CheckItem("DHCP", v4.IsDhcpEnabled ? "aktiv" : "statisch"));
                        if (v4.IsDhcpEnabled && props.DhcpServerAddresses.Count > 0)
                            items.Add(new CheckItem("DHCP-Server", string.Join(", ", props.DhcpServerAddresses)));
                    }
                }
                catch { }
                try { if (props.GetIPv4Properties() is { } p4) items.Add(new CheckItem("MTU", p4.Mtu.ToString())); } catch { }
                var suffix = Safe(() => props.DnsSuffix, "");
                if (!string.IsNullOrEmpty(suffix)) items.Add(new CheckItem("DNS-Suffix", suffix));
            }
            catch { }

            list.Add(new AdapterInfo { Name = nic.Name, Description = nic.Description, Up = up, Items = items });
        }
        return list;
    }

    /// <summary>Öffentliche IPv4/IPv6, Provider, Organisation, Standort, AS und Reverse-DNS.</summary>
    public static async Task<List<CheckItem>> PublicInfoAsync(CancellationToken ct = default)
    {
        var items = new List<CheckItem>();
        var v4Task = SafeGet("https://api.ipify.org", ct);
        var v6Task = SafeGet("https://api6.ipify.org", ct);
        var geoTask = GeoIpService.SelfAsync(ct);
        var v4 = await v4Task;
        var v6 = await v6Task;
        items.Add(new CheckItem("Öffentliche IPv4", v4 ?? "nicht ermittelbar", v4 != null ? Level.Ok : Level.Warn));
        items.Add(new CheckItem("Öffentliche IPv6", v6 ?? "keine IPv6-Konnektivität", v6 != null ? Level.Ok : Level.Info));
        try
        {
            var g = await geoTask;
            if (g?.Ok == true)
            {
                items.Add(new CheckItem("Provider", g.Isp));
                if (!string.IsNullOrEmpty(g.Org) && g.Org != g.Isp) items.Add(new CheckItem("Organisation", g.Org));
                items.Add(new CheckItem("Standort (ungefähr)", g.Location));
                items.Add(new CheckItem("AS", g.As));
            }
        }
        catch { }
        if (v4 != null && IPAddress.TryParse(v4, out var ip))
        {
            var ptr = await NetUtil.ReverseDnsAsync(ip);
            if (ptr != "") items.Add(new CheckItem("Reverse-DNS", ptr));
        }
        return items;
    }

    private static async Task<string> SafeGet(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(6000);
            return (await NetUtil.Http.GetStringAsync(url, cts.Token)).Trim();
        }
        catch { return null; }
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
    }

    public static string TypeName(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "WLAN",
        NetworkInterfaceType.Ppp => "PPP / VPN",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobilfunk",
        _ => t.ToString(),
    };
}
