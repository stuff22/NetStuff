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
    private string _hostname = "", _mac = "", _vendor = "", _type = "", _ports = "", _info = "", _model = "", _serial = "";
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
    /// <summary>Gerätename laut Router (FRITZ!Box-Hostliste).</summary>
    public string RouterName { get; set; } = "";

    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public string Mac { get => _mac; set => Set(ref _mac, value); }
    public string Vendor { get => _vendor; set => Set(ref _vendor, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Ports { get => _ports; set => Set(ref _ports, value); }
    public string Info { get => _info; set => Set(ref _info, value); }
    /// <summary>Modell/Hersteller laut Gerät oder Router (mDNS, UPnP, Ubiquiti, HTTP-API, SNMP, UniFi).</summary>
    public string Model { get => _model; set => Set(ref _model, value); }
    public string Serial { get => _serial; set => Set(ref _serial, value); }
    /// <summary>Woher die MAC-Adresse stammt (leer = eigene ARP-Tabelle).</summary>
    public string MacSource { get; set; } = "";
    public string Firmware { get; set; } = "";
    public List<string> MdnsServices { get; } = new();

    /// <summary>MAC übernehmen, wenn noch keine (echte) bekannt ist; Hersteller über die OUI-Liste.</summary>
    public void SetMac(byte[] mac, string source)
    {
        if (mac is not { Length: 6 } || mac.All(b => b == 0)) return;
        if (Mac != "" && Mac != "02:00:00:00:00:00") return;
        Mac = NetUtil.FormatMac(mac);
        MacSource = source;
        Vendor = OuiDatabase.Lookup(Mac);
    }

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

    /// <summary>MAC-Adressen zusätzlich beim Router erfragen (FRITZ!Box TR-064, SNMP-ARP-Tabelle).</summary>
    public bool QueryRouter { get; set; } = true;
    /// <summary>Art des Routers für die Abfrage (Automatisch probiert alle).</summary>
    public RouterKind RouterKind { get; set; } = RouterKind.Auto;
    /// <summary>Geräte direkt fragen: mDNS, UPnP, Ubiquiti-Discovery, Geräte-HTTP-APIs, SNMP (mit Community).</summary>
    public bool DeviceProbes { get; set; } = true;
    /// <summary>Router-Adresse; leer = Standard-Gateway.</summary>
    public string Router { get; set; } = "";
    public string RouterUser { get; set; } = "";
    public string RouterPassword { get; set; } = "";
    /// <summary>SNMP-Community für die ARP-Tabelle des Routers; leer = keine SNMP-Abfrage.</summary>
    public string SnmpCommunity { get; set; } = "";

    /// <summary>Ergebnis der Router-Abfrage für die Statuszeile (wird vom Scanner gesetzt).</summary>
    public string RouterSummary { get; set; } = "";
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

        // Router parallel zum Ping-Durchlauf fragen – liefert MACs auch dort, wo das System keine ARP-Tabelle herausgibt (iOS)
        Task<RouterHostResult> routerTask = Task.FromResult<RouterHostResult>(null);
        IPAddress router = null;
        if (s.QueryRouter)
        {
            if (!IPAddress.TryParse((s.Router ?? "").Trim(), out router))
                router = locals.Select(l => l.Gateway).FirstOrDefault(g => g != null);
            if (router != null)
            {
                var r = router;
                routerTask = Task.Run(async () =>
                {
                    try { return await RouterHosts.QueryAsync(r, s.RouterUser, s.RouterPassword, s.SnmpCommunity, ct, kind: s.RouterKind); }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }
                }, ct);
            }
        }

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
                lock (detailTasks) detailTasks.Add(DetailsAsync(host, s, ct));
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

        // ARP-Tabelle nachlesen (macOS): fehlende MACs ergänzen und Geräte aufnehmen, die nicht auf Ping antworten
        IReadOnlyDictionary<string, byte[]> table = null;
        try { table = Platform.Arp?.Table(); } catch { }
        if (table != null)
        {
            var known = hosts.ToDictionary(h => h.Ip);
            foreach (var ip in ips)
            {
                if (!table.TryGetValue(ip.ToString(), out var mac) || !IsLocalSubnet(ip)) continue;
                if (known.TryGetValue(ip.ToString(), out var h))
                {
                    if (h.Mac != "") continue;
                    h.Mac = NetUtil.FormatMac(mac);
                    h.Vendor = OuiDatabase.Lookup(h.Mac);
                }
                else
                {
                    h = new ScanHost { Address = ip, IsGateway = gateways.Contains(ip.ToString()), Mac = NetUtil.FormatMac(mac) };
                    h.Vendor = OuiDatabase.Lookup(h.Mac);
                    hosts.Add(h);
                    found?.Invoke(h);
                    detailTasks.Add(DetailsAsync(h, s, ct));
                }
            }
        }

        // Hostliste/ARP-Tabelle des Routers: fehlende MACs und Namen ergänzen, aktive Geräte ohne Ping-Antwort aufnehmen
        var routerResult = await routerTask;
        s.RouterSummary = routerResult?.Summary ?? "";
        if (s.RouterSummary == "" && router != null && !Platform.HasArp)
            s.RouterSummary = L.F("Router {0} liefert keine MAC-Adressen (kein TR-064 – SNMP-Community eintragen?)", router);
        if (routerResult != null && routerResult.Hosts.Count > 0)
        {
            var known = hosts.ToDictionary(h => h.Ip);
            var wanted = ips.Select(i => i.ToString()).ToHashSet();
            foreach (var rh in routerResult.Hosts.Values)
            {
                if (!wanted.Contains(rh.Ip)) continue;
                bool isNew = !known.TryGetValue(rh.Ip, out var h);
                if (isNew)
                {
                    if (!rh.Active) continue;
                    h = new ScanHost { Address = IPAddress.Parse(rh.Ip), IsGateway = gateways.Contains(rh.Ip) };
                }
                h.SetMac(rh.Mac, rh.Source);
                if (rh.Name != "") h.RouterName = rh.Name;
                if (h.Model == "" && rh.Model != "") h.Model = rh.Model;
                if (h.Serial == "" && rh.Serial != "") h.Serial = rh.Serial;
                if (isNew)
                {
                    hosts.Add(h);
                    known[rh.Ip] = h;
                    found?.Invoke(h);
                    lock (detailTasks) detailTasks.Add(DetailsAsync(h, s, ct));
                }
            }
        }

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

    private static async Task DetailsAsync(ScanHost h, NetScanSettings s, CancellationToken ct)
    {
        var dnsTask = NetUtil.ReverseDnsAsync(h.Address, 2500);
        var nbTask = NetBiosClient.QueryExAsync(h.Address, 1200);
        Task<int[]> portTask = Task.FromResult(Array.Empty<int>());
        if (s.CheckPorts)
        {
            portTask = Task.WhenAll(PortCatalog.Quick.Select(async p => await NetUtil.TcpConnectAsync(h.Address, p, 600, ct) ? p : 0))
                .ContinueWith(t => t.Result.Where(p => p > 0).ToArray(), ct);
        }

        // Das Gerät selbst fragen – jede Quelle für sich, danach nach Verlässlichkeit zusammenführen
        DeviceFacts mdns = new(), ubnt = new(), snmp = new(), http = new();
        var probes = new List<Task>
        {
            DeviceProbe.MdnsAsync(h.Address, mdns, ct),
            DeviceProbe.UbiquitiAsync(h.Address, ubnt, ct),
        };
        var ssdpTask = s.DeviceProbes ? DeviceProbe.SsdpAsync(h.Address, ct) : Task.FromResult<SsdpDevice>(null);
        if (!s.DeviceProbes) probes.Clear();
        if (s.DeviceProbes && !string.IsNullOrWhiteSpace(s.SnmpCommunity)) probes.Add(DeviceProbe.SnmpAsync(h.Address, s.SnmpCommunity.Trim(), snmp, ct));

        var dns = await dnsTask;
        var nb = await nbTask;
        h.NetBiosName = nb.Name;
        h.Workgroup = nb.Group;

        try
        {
            var ports = await portTask;
            h.OpenPorts.AddRange(ports);
            h.Ports = string.Join(", ", ports.Select(p => PortCatalog.Name(p) is { Length: > 0 } n ? $"{p} {n}" : p.ToString()));
        }
        catch { }
        if (s.DeviceProbes) probes.Add(DeviceProbe.HttpApisAsync(h.Address, h.OpenPorts, h.Vendor, http, ct));
        try { await Task.WhenAll(probes); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { }
        var ssdp = await ssdpTask;
        if (ssdp != null) h.Ssdp = ssdp;

        var facts = new DeviceFacts();
        foreach (var f in new[] { ubnt, http }) facts.MergeFrom(f);
        var up = new DeviceFacts();
        DeviceProbe.ApplySsdp(ssdp, up);
        facts.MergeFrom(up);
        facts.MergeFrom(mdns);
        facts.MergeFrom(snmp);
        // NetBIOS: Windows liefert die echte MAC, Samba meist Nullen (werden ignoriert)
        if (facts.Mac == null) facts.SetMac(nb.Mac, "NetBIOS");
        // Viele Hersteller (Synology, Loxone, AVM, Sonos …) verwenden die MAC als Seriennummer oder am Ende der UPnP-UDN
        if (facts.Mac == null) facts.SetMac(MacFromId(facts.Serial, facts.Manufacturer), L.T("Seriennummer"));
        if (facts.Mac == null && ssdp != null) facts.SetMac(MacFromId(ssdp.Udn, facts.Manufacturer, atEnd: true), "UPnP-UDN");

        h.SetMac(facts.Mac, facts.MacSource);
        h.Hostname = dns != "" ? dns : mdns.Hostname != "" ? mdns.Hostname : nb.Name != "" ? nb.Name : h.RouterName;
        if (h.Model == "") h.Model = ModelText(facts.Manufacturer, facts.Model);
        if (h.Serial == "") h.Serial = facts.Serial;
        if (h.Firmware == "") h.Firmware = facts.Firmware;
        if (h.Hostname == "" && facts.Name != "") h.Hostname = facts.Name;
        h.MdnsServices.AddRange(facts.Services.Take(6));
        UpdateInfo(h);
    }

    /// <summary>
    /// 12 Hex-Zeichen am Anfang (Seriennummer) bzw. Ende (UDN) als MAC, aber nur wenn die OUI zum gemeldeten Hersteller passt –
    /// sonst wäre jede zufällige Hex-Seriennummer eine „MAC“.
    /// </summary>
    internal static byte[] MacFromId(string id, string manufacturer, bool atEnd = false)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(manufacturer)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(id, atEnd ? @"([0-9A-Fa-f]{12})$" : @"^([0-9A-Fa-f]{12})(?![0-9A-Fa-f])");
        if (!m.Success) return null;
        var vendor = OuiDatabase.Lookup(m.Groups[1].Value);
        var word = manufacturer.Split(' ', ',', '.')[0];
        if (vendor == "" || word.Length < 3 || !vendor.Contains(word, StringComparison.OrdinalIgnoreCase)) return null;
        return RouterHosts.ParseMac(m.Groups[1].Value);
    }

    /// <summary>„Hersteller Modell“ ohne Wiederholungen („Loxone Electronics GmbH Loxone Miniserver“ → „Loxone Miniserver“).</summary>
    internal static string ModelText(string manufacturer, string model)
    {
        model = (model ?? "").Trim();
        manufacturer = (manufacturer ?? "").Trim();
        var words = model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // doppelte Hälfte entfernen („Loxone Miniserver Loxone Miniserver“)
        if (words.Length % 2 == 0 && words.Length > 0 && words.Take(words.Length / 2).SequenceEqual(words.Skip(words.Length / 2), StringComparer.OrdinalIgnoreCase))
            model = string.Join(" ", words.Take(words.Length / 2));
        var first = manufacturer.Split(' ', ',')[0];
        if (manufacturer == "" || first.Length > 1 && model.Contains(first, StringComparison.OrdinalIgnoreCase)) return model;
        // Rechtsform weglassen: „Synology Inc.“ → „Synology“, „Hewlett Packard Enterprise“ bleibt
        var short_ = System.Text.RegularExpressions.Regex.Replace(manufacturer,
            @"[,\s]+(GmbH|AG|Inc\.?|Incorporated|Corp\.?|Corporation|Co\.?,?\s*Ltd\.?|Ltd\.?|LLC|S\.?A\.?|B\.?V\.?|Limited)\s*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return (short_ + " " + model).Trim();
    }

    private static void UpdateInfo(ScanHost h)
    {
        var parts = new List<string>();
        if (h.Ssdp != null && h.Ssdp.Summary != "") parts.Add(h.Ssdp.Summary);
        if (h.NetBiosName != "" && !h.Hostname.StartsWith(h.NetBiosName, StringComparison.OrdinalIgnoreCase)) parts.Add("NetBIOS " + h.NetBiosName);
        if (h.Workgroup != "") parts.Add(L.T("Arbeitsgruppe ") + h.Workgroup);
        if (h.RouterName != "" && !h.Hostname.StartsWith(h.RouterName, StringComparison.OrdinalIgnoreCase)) parts.Add(L.T("Router: ") + h.RouterName);
        if (h.MdnsServices.Count > 0) parts.Add("mDNS: " + string.Join(", ", h.MdnsServices));
        if (h.Firmware != "") parts.Add("Firmware " + h.Firmware);
        if (h.MacSource != "") parts.Add(L.F("MAC via {0}", h.MacSource));
        h.Info = string.Join(" · ", parts.Distinct());
        h.Type = L.T(DeviceClassifier.Classify(h));
    }

    /// <summary>CSV (Semikolon, für Excel) der Scan-Ergebnisse.</summary>
    public static string ToCsv(IEnumerable<ScanHost> hosts)
    {
        var sb = new StringBuilder("IP;Hostname;MAC;MAC-Quelle;Hersteller;Modell;Seriennummer;Typ;Offene Ports;Ping ms;Info\n");
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var h in hosts)
            sb.AppendLine(string.Join(";", Q(h.Ip), Q(h.Hostname), Q(h.Mac), Q(h.Mac == "" ? "" : h.MacSource == "" ? "ARP" : h.MacSource), Q(h.Vendor),
                Q(h.Model), Q(h.Serial), Q(h.Type), Q(h.Ports), h.Ping?.ToString() ?? "", Q(h.Info)));
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
