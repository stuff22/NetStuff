using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Lextm.SharpSnmpLib;

namespace NetStuff.Services;

/// <summary>Was ein Gerät selbst über sich verrät (MAC, Modell, Seriennummer …), mit Quelle der MAC-Adresse.</summary>
public class DeviceFacts
{
    public byte[] Mac { get; set; }
    public string MacSource { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public List<string> Services { get; } = new();

    public void SetMac(byte[] mac, string source)
    {
        if (Mac != null || mac is not { Length: 6 } || mac.All(b => b == 0) || mac.All(b => b == 0xFF)) return;
        Mac = mac;
        MacSource = source;
    }

    private static void Fill(Func<string> get, Action<string> set, string value)
    {
        if (string.IsNullOrWhiteSpace(get()) && !string.IsNullOrWhiteSpace(value)) set(value.Trim());
    }
    public void SetHostname(string v) => Fill(() => Hostname, x => Hostname = x, v);
    public void SetName(string v) => Fill(() => Name, x => Name = x, v);
    public void SetManufacturer(string v) => Fill(() => Manufacturer, x => Manufacturer = x, v);
    public void SetModel(string v) => Fill(() => Model, x => Model = x, v);
    public void SetSerial(string v) => Fill(() => Serial, x => Serial = x, v);
    public void SetFirmware(string v) => Fill(() => Firmware, x => Firmware = x, v);

    /// <summary>Fehlende Angaben aus einer weiteren Quelle übernehmen (Reihenfolge der Aufrufe = Vorrang).</summary>
    public void MergeFrom(DeviceFacts o)
    {
        if (o == null) return;
        SetMac(o.Mac, o.MacSource);
        SetHostname(o.Hostname); SetName(o.Name); SetManufacturer(o.Manufacturer);
        SetModel(o.Model); SetSerial(o.Serial); SetFirmware(o.Firmware);
        foreach (var x in o.Services) if (!Services.Contains(x)) Services.Add(x);
    }
}

/// <summary>
/// Fragt Geräte direkt (Unicast, daher auch auf iOS ohne Multicast-Freigabe): mDNS/DNS-SD an Port 5353, SSDP-M-SEARCH an
/// Port 1900 samt UPnP-Gerätebeschreibung, Ubiquiti-Discovery (UDP 10001), Geräte-HTTP-APIs (Shelly, Tasmota, Hue,
/// Chromecast, Sonos, AVM) und – mit Community – SNMP.
/// </summary>
public static class DeviceProbe
{
    private static readonly object Gate = new();

    /* ================================================================== mDNS / DNS-SD (Unicast an das Gerät) */

    public static async Task MdnsAsync(IPAddress ip, DeviceFacts f, CancellationToken ct, int port = 5353)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var ep = new IPEndPoint(ip, port);
            var b = ip.GetAddressBytes();

            var revName = $"{b[3]}.{b[2]}.{b[1]}.{b[0]}.in-addr.arpa";
            // Responder drosseln wiederholte Antworten (≈1 s je Eintrag) – daher ein zweiter Versuch
            var rev = await DnsQueryAsync(udp, ep, revName, 12, 700, ct) ?? await DnsQueryAsync(udp, ep, revName, 12, 900, ct);
            if (rev == null) return; // kein mDNS-Responder
            f.SetHostname(rev.FirstOrDefault(r => r.Type == 12)?.Text?.TrimEnd('.'));

            var types = (await DnsQueryAsync(udp, ep, "_services._dns-sd._udp.local", 12, 700, ct)
                         ?? await DnsQueryAsync(udp, ep, "_services._dns-sd._udp.local", 12, 900, ct) ?? new())
                .Where(r => r.Type == 12).Select(r => r.Text).Distinct().Take(10).ToList();
            var instances = new List<string>();
            foreach (var t in types)
            {
                f.Services.Add(Regex.Replace(t, @"\._(tcp|udp)\.local\.?$", "").TrimStart('_'));
                var recs = await DnsQueryAsync(udp, ep, t.TrimEnd('.'), 12, 600, ct) ?? new();
                foreach (var inst in recs.Where(r => r.Type == 12).Select(r => r.Text).Distinct().Take(4))
                {
                    instances.Add(inst);
                    var txt = recs.Where(r => r.Type == 16 && SameName(r.Name, inst)).SelectMany(r => r.TxtPairs).ToList();
                    if (txt.Count == 0)
                        txt = (await DnsQueryAsync(udp, ep, inst.TrimEnd('.'), 16, 500, ct) ?? new()).Where(r => r.Type == 16).SelectMany(r => r.TxtPairs).ToList();
                    ApplyTxt(f, inst, txt);
                }
            }

            // Apple & Co.: Modell steht unter <Name>._device-info._tcp.local (nicht durchsuchbar, daher direkt fragen)
            var first = instances.Select(i => i.Split("._")[0]).FirstOrDefault(n => n != "");
            if (first != null && f.Model == "")
            {
                var di = await DnsQueryAsync(udp, ep, first + "._device-info._tcp.local", 16, 500, ct) ?? new();
                ApplyTxt(f, first, di.Where(r => r.Type == 16).SelectMany(r => r.TxtPairs).ToList());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    private static bool SameName(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>TXT-Schlüssel, die Geräte für MAC, Modell und Seriennummer verwenden.</summary>
    internal static void ApplyTxt(DeviceFacts f, string instance, List<(string Key, string Value)> txt)
    {
        string V(params string[] keys) => txt.FirstOrDefault(p => keys.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).Value ?? "";
        var label = instance.Split("._")[0];

        // MAC: AirPlay „deviceid“, diverse „mac…“-Schlüssel, RAOP-Instanz „AABBCCDDEEFF@Name“, Linux/Avahi „Name [aa:bb:…]“
        f.SetMac(RouterHosts.ParseMac(V("deviceid", "macaddress", "mac", "hwaddr", "mac_address", "macaddr")), "mDNS");
        var raop = Regex.Match(label, @"^([0-9A-Fa-f]{12})@");
        if (raop.Success) f.SetMac(RouterHosts.ParseMac(raop.Groups[1].Value), "mDNS");
        var ws = Regex.Match(label, @"\[([0-9a-fA-F:]{17})\]");
        if (ws.Success) f.SetMac(RouterHosts.ParseMac(ws.Groups[1].Value), "mDNS");

        f.SetSerial(V("serialnumber", "serial", "sn", "dsn", "serialno", "usb_sn", "srcvers_sn"));
        f.SetManufacturer(V("usb_mfg", "mfg", "manufacturer", "vendor"));
        var model = V("model", "md", "ty", "usb_mdl", "am", "modelname");
        if (model == "" && V("product") is { Length: > 0 } prod) model = prod.Trim('(', ')');
        f.SetModel(model);
        f.SetFirmware(V("fv", "fw", "firmware", "osvers", "srcvers"));
        f.SetName(V("fn", "name"));
        if (f.Name == "" && !Regex.IsMatch(label, @"^[0-9A-Fa-f]{12}@")) f.SetName(Regex.Replace(label, @"\s*\[[0-9a-fA-F:]{17}\]$", ""));
    }

    internal record DnsRecord(string Name, int Type, string Text, List<(string Key, string Value)> TxtPairs);

    internal static async Task<List<DnsRecord>> DnsQueryAsync(UdpClient udp, IPEndPoint ep, string name, int type, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        var q = new List<byte> { (byte)(id >> 8), (byte)id, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var lb = Encoding.UTF8.GetBytes(label);
            q.Add((byte)lb.Length);
            q.AddRange(lb);
        }
        q.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
        await udp.SendAsync(q.ToArray(), q.Count, ep);

        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var recv = udp.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(recv, Task.Delay(left, ct)) != recv) return null;
            var r = recv.Result;
            if (!r.RemoteEndPoint.Address.Equals(ep.Address) || r.Buffer.Length < 12) continue;
            int rid = r.Buffer[0] << 8 | r.Buffer[1];
            if (rid != id && rid != 0) continue; // manche Responder setzen die ID auf 0
            return ParseDns(r.Buffer);
        }
    }

    internal static List<DnsRecord> ParseDns(byte[] m)
    {
        var list = new List<DnsRecord>();
        int qd = m[4] << 8 | m[5], an = m[6] << 8 | m[7], ns = m[8] << 8 | m[9], ar = m[10] << 8 | m[11];
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(m, ref pos); pos += 4; }
        for (int i = 0; i < an + ns + ar && pos + 10 <= m.Length; i++)
        {
            var name = ReadName(m, ref pos);
            int type = m[pos] << 8 | m[pos + 1];
            int len = m[pos + 8] << 8 | m[pos + 9];
            pos += 10;
            int end = pos + len;
            if (end > m.Length) break;
            string text = "";
            var pairs = new List<(string, string)>();
            switch (type)
            {
                case 12: // PTR
                    { int p = pos; text = ReadName(m, ref p); break; }
                case 33: // SRV: Priorität, Gewicht, Port, Ziel
                    { int p = pos + 6; text = ReadName(m, ref p); break; }
                case 1 when len == 4:
                    text = new IPAddress(m.AsSpan(pos, 4)).ToString(); break;
                case 16: // TXT
                    for (int p = pos; p < end;)
                    {
                        int l = m[p++];
                        if (p + l > end) break;
                        var s = Encoding.UTF8.GetString(m, p, l);
                        p += l;
                        int eq = s.IndexOf('=');
                        pairs.Add(eq > 0 ? (s[..eq], s[(eq + 1)..]) : (s, ""));
                    }
                    break;
            }
            list.Add(new DnsRecord(name, type, text, pairs));
            pos = end;
        }
        return list;
    }

    private static string ReadName(byte[] m, ref int pos)
    {
        var parts = new List<string>();
        int p = pos, jumps = 0;
        bool jumped = false;
        while (p < m.Length)
        {
            int l = m[p];
            if (l == 0) { p++; break; }
            if ((l & 0xC0) == 0xC0)
            {
                if (p + 1 >= m.Length || ++jumps > 20) break;
                int target = (l & 0x3F) << 8 | m[p + 1];
                if (!jumped) pos = p + 2;
                jumped = true;
                p = target;
                continue;
            }
            p++;
            if (p + l > m.Length) break;
            parts.Add(Encoding.UTF8.GetString(m, p, l));
            p += l;
        }
        if (!jumped) pos = p;
        return string.Join(".", parts) + ".";
    }

    /* ================================================================== Ubiquiti-Discovery (UDP 10001) */

    public static async Task UbiquitiAsync(IPAddress ip, DeviceFacts f, CancellationToken ct, int port = 10001)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            await udp.SendAsync(new byte[] { 1, 0, 0, 0 }, 4, new IPEndPoint(ip, port));
            var recv = udp.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(recv, Task.Delay(900, ct)) != recv) return;
            ParseUbiquiti(recv.Result.Buffer, ip, f);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    internal static void ParseUbiquiti(byte[] d, IPAddress ip, DeviceFacts f)
    {
        if (d.Length < 4 || d[0] != 1) return;
        byte[] anyMac = null;
        string modelShort = "", product = "";
        for (int i = 4; i + 3 <= d.Length;)
        {
            int t = d[i], l = d[i + 1] << 8 | d[i + 2];
            if (i + 3 + l > d.Length) break;
            var v = d.AsSpan(i + 3, l);
            var str = Encoding.UTF8.GetString(v).Trim('\0', ' ');
            string S() => str;
            switch (t)
            {
                case 0x01 when l == 6: anyMac = v.ToArray(); break;
                case 0x02 when l == 10: // MAC + IPv4 je Schnittstelle – die zur abgefragten IP passt
                    if (new IPAddress(v.Slice(6, 4)).Equals(ip)) f.SetMac(v[..6].ToArray(), "Ubiquiti-Discovery");
                    break;
                case 0x03: f.SetFirmware(S()); break;
                case 0x0B: f.SetName(S()); break;
                case 0x0C: modelShort = S(); break;
                case 0x14: case 0x06: if (product == "") product = S(); break;
            }
            i += 3 + l;
        }
        f.SetMac(anyMac, "Ubiquiti-Discovery");
        f.SetManufacturer("Ubiquiti");
        f.SetModel(product != "" ? product : modelShort);
    }

    /* ================================================================== SSDP / UPnP-Gerätebeschreibung */

    public static async Task<SsdpDevice> SsdpAsync(IPAddress ip, CancellationToken ct, int port = 1900)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var msg = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: {ip}:{port}\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: upnp:rootdevice\r\n\r\n");
            await udp.SendAsync(msg, msg.Length, new IPEndPoint(ip, port));
            var recv = udp.ReceiveAsync(ct).AsTask();
            if (await Task.WhenAny(recv, Task.Delay(1200, ct)) != recv) return null;
            var text = Encoding.UTF8.GetString(recv.Result.Buffer);
            var dev = new SsdpDevice
            {
                Server = SsdpDiscovery.Header(text, "SERVER"),
                Location = SsdpDiscovery.Header(text, "LOCATION"),
            };
            if (dev.Location.StartsWith("http")) await SsdpDiscovery.LoadDescriptionAsync(dev, ct);
            return dev;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public static void ApplySsdp(SsdpDevice d, DeviceFacts f)
    {
        if (d == null) return;
        f.SetName(d.FriendlyName);
        f.SetManufacturer(d.Manufacturer);
        f.SetModel(d.Model);
        f.SetSerial(d.Serial);
        f.SetMac(RouterHosts.ParseMac(d.Mac), "UPnP");
    }

    /* ================================================================== HTTP-APIs der Geräte */

    public static async Task HttpApisAsync(IPAddress ip, IReadOnlyCollection<int> openPorts, string vendor, DeviceFacts f, CancellationToken ct)
    {
        vendor = (vendor ?? "").ToLowerInvariant();
        var tasks = new List<Task>();
        if (openPorts.Contains(80))
        {
            // Shelly Gen1/Gen2+: /shelly ohne Anmeldung
            tasks.Add(Json(ip, 80, "/shelly", ct, j =>
            {
                if (Str(j, "mac") == "" || Str(j, "type") == "" && Str(j, "model") == "" && Str(j, "app") == "") return;
                f.SetMac(RouterHosts.ParseMac(Str(j, "mac")), "Shelly-API");
                f.SetManufacturer("Shelly");
                f.SetModel(Str(j, "model") is { Length: > 0 } m ? m : Str(j, "type"));
                f.SetFirmware(Str(j, "fw_id") is { Length: > 0 } fw ? fw : Str(j, "fw"));
                f.SetName(Str(j, "name"));
            }));
            // Tasmota: Status 0 (alles) ohne Anmeldung, sofern kein Web-Kennwort gesetzt
            tasks.Add(Json(ip, 80, "/cm?cmnd=Status%200", ct, j =>
            {
                if (!j.TryGetProperty("StatusNET", out var net)) return;
                f.SetMac(RouterHosts.ParseMac(Str(net, "Mac")), "Tasmota-API");
                f.SetManufacturer("Tasmota");
                if (j.TryGetProperty("StatusFWR", out var fw)) { f.SetFirmware(Str(fw, "Version")); f.SetModel(Str(fw, "Hardware")); }
                if (j.TryGetProperty("Status", out var st)) f.SetName(Str(st, "DeviceName"));
            }));
            // Philips Hue Bridge: /api/config ohne Schlüssel
            tasks.Add(Json(ip, 80, "/api/config", ct, j =>
            {
                if (Str(j, "bridgeid") == "") return;
                f.SetMac(RouterHosts.ParseMac(Str(j, "mac")), "Hue-API");
                f.SetManufacturer("Signify (Philips Hue)");
                f.SetModel("Hue Bridge " + Str(j, "modelid"));
                f.SetSerial(Str(j, "bridgeid"));
                f.SetFirmware(Str(j, "swversion"));
                f.SetName(Str(j, "name"));
            }));
        }
        if (openPorts.Contains(8008) || openPorts.Contains(8009))
        {
            // Google Cast (Chromecast, Nest, Cast-TVs)
            tasks.Add(Json(ip, 8008, "/setup/eureka_info?params=name,device_info,build_info", ct, j =>
            {
                f.SetName(Str(j, "name"));
                if (j.TryGetProperty("device_info", out var di))
                {
                    f.SetMac(RouterHosts.ParseMac(Str(di, "mac_address")), "Google-Cast-API");
                    f.SetManufacturer(Str(di, "manufacturer"));
                    f.SetModel(Str(di, "model_name"));
                }
                if (j.TryGetProperty("build_info", out var bi)) f.SetFirmware(Str(bi, "cast_build_revision"));
            }));
        }
        if (openPorts.Contains(1400) || vendor.Contains("sonos"))
            tasks.Add(Xml(ip, 1400, "/xml/device_description.xml", f, ct));
        if (openPorts.Contains(49000) || vendor.Contains("avm"))
            tasks.Add(Xml(ip, 49000, "/tr64desc.xml", f, ct));
        await Task.WhenAll(tasks);
    }

    private static string Str(JsonElement e, string n) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind == JsonValueKind.Number ? v.ToString() : ""
            : "";

    private static async Task<string> Get(IPAddress ip, int port, string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(2000);
        using var resp = await NetUtil.Http.GetAsync($"http://{ip}:{port}{path}", cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    private static async Task Json(IPAddress ip, int port, string path, CancellationToken ct, Action<JsonElement> apply)
    {
        try
        {
            var body = await Get(ip, port, path, ct);
            if (body == null || !body.TrimStart().StartsWith("{")) return;
            using var doc = JsonDocument.Parse(body);
            lock (Gate) apply(doc.RootElement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    private static async Task Xml(IPAddress ip, int port, string path, DeviceFacts f, CancellationToken ct)
    {
        try
        {
            var body = await Get(ip, port, path, ct);
            if (body == null) return;
            var dev = new SsdpDevice();
            SsdpDiscovery.ParseDescription(dev, XDocument.Parse(body));
            lock (Gate) ApplySsdp(dev, f);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    /* ================================================================== SNMP am Gerät */

    public static async Task SnmpAsync(IPAddress ip, string community, DeviceFacts f, CancellationToken ct)
    {
        try
        {
            var snmp = new SnmpService(ip, 161, community, v1: false);
            var vars = await snmp.GetAsync("1.3.6.1.2.1.1.5.0", "1.3.6.1.2.1.1.1.0");
            ct.ThrowIfCancellationRequested();
            f.SetName(SnmpService.Format(vars[0].Data).Replace("–", ""));
            var descr = SnmpService.Format(vars[1].Data);
            // Seriennummer/Modell: ENTITY-MIB (Switches, Router, APs) und Printer-MIB
            var serial = (await Safe(() => snmp.WalkAsync("1.3.6.1.2.1.47.1.1.1.1.11", 20))).Select(v => SnmpService.Format(v.Data)).FirstOrDefault(s => s.Length > 2 && s != "–");
            var model = (await Safe(() => snmp.WalkAsync("1.3.6.1.2.1.47.1.1.1.1.13", 20))).Select(v => SnmpService.Format(v.Data)).FirstOrDefault(s => s.Length > 1 && s != "–");
            serial ??= (await Safe(() => snmp.WalkAsync("1.3.6.1.2.1.43.5.1.1.17", 4))).Select(v => SnmpService.Format(v.Data)).FirstOrDefault(s => s.Length > 2 && s != "–");
            f.SetSerial(serial);
            f.SetModel(model ?? (descr.Length is > 0 and < 80 ? descr : ""));
            // MAC: erste echte Schnittstellenadresse (ifPhysAddress)
            foreach (var v in await Safe(() => snmp.WalkAsync("1.3.6.1.2.1.2.2.1.6", 64)))
                if (v.Data is OctetString os && os.GetRaw() is { Length: 6 } mac && mac.Any(x => x != 0)) { f.SetMac(mac, "SNMP"); break; }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    private static async Task<List<Variable>> Safe(Func<Task<List<Variable>>> f)
    {
        try { return await f(); } catch { return new List<Variable>(); }
    }
}
