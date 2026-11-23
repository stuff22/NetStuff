using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsClient;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== Resolver-Vergleich */

public class ResolverRow : Bindable
{
    private string _answer = "", _verdict = "", _ttl = "";
    private Level _level = Level.Info;
    private double? _avg, _min, _max;

    public bool Enabled { get; set; } = true;
    public string Name { get; init; }
    public string Address { get; init; }
    public bool IsDoh => Address.StartsWith("https://");
    public bool IsSystem => Address == "system";

    public string Answer { get => _answer; set => Set(ref _answer, value); }
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    public string Ttl { get => _ttl; set => Set(ref _ttl, value); }
    public Level Level { get => _level; set => Set(ref _level, value); }
    public string Normalized { get; set; } = "";

    public void SetTimes(List<double> t)
    {
        _avg = t.Count > 0 ? t.Average() : null;
        _min = t.Count > 0 ? t.Min() : null;
        _max = t.Count > 0 ? t.Max() : null;
        Raise(nameof(AvgText)); Raise(nameof(MinText)); Raise(nameof(MaxText)); Raise(nameof(AvgSort));
    }

    public void Reset()
    {
        Answer = ""; Verdict = ""; Ttl = ""; Level = Level.Info; Normalized = "";
        SetTimes(new());
    }

    public string AvgText => _avg.HasValue ? $"{_avg:0.#}" : "–";
    public string MinText => _min.HasValue ? $"{_min:0}" : "–";
    public string MaxText => _max.HasValue ? $"{_max:0}" : "–";
    public double AvgSort => _avg ?? double.MaxValue;
}

/// <summary>Vergleicht Antworten und Antwortzeiten mehrerer DNS-Resolver (klassisch und DoH/JSON).</summary>
public static class ResolverBench
{
    /// <summary>Standardliste: System, Gateway, große öffentliche Resolver und zwei DoH-Dienste.</summary>
    public static List<ResolverRow> DefaultResolvers(string systemName = "System")
    {
        var list = new List<ResolverRow> { new() { Name = systemName, Address = "system" } };
        var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).FirstOrDefault(g => g != null);
        if (gw != null) list.Add(new ResolverRow { Name = "Router / Gateway", Address = gw.ToString() });
        foreach (var (n, a) in new[]
                 {
                     ("Cloudflare", "1.1.1.1"), ("Cloudflare 2", "1.0.0.1"), ("Google", "8.8.8.8"), ("Google 2", "8.8.4.4"),
                     ("Quad9", "9.9.9.9"), ("OpenDNS", "208.67.222.222"), ("AdGuard", "94.140.14.14"), ("DNS4EU", "86.54.11.100"),
                     ("Control D", "76.76.2.0"),
                     ("Cloudflare DoH", "https://cloudflare-dns.com/dns-query"), ("Google DoH", "https://dns.google/resolve"),
                 })
            list.Add(new ResolverRow { Name = n, Address = a });
        return list;
    }

    /// <summary>Gültige eigene Resolver-Adresse? (IP oder https-URL)</summary>
    public static bool IsValidAddress(string a) => a.StartsWith("https://") || IPAddress.TryParse(a, out _);

    /// <summary>Fragt alle aktiven Resolver ab, bewertet die Antworten (Mehrheit) und liefert eine Zusammenfassung.</summary>
    public static async Task<string> RunAsync(IList<ResolverRow> rows, string name, QueryType type, int rounds, CancellationToken ct = default)
    {
        name = NetUtil.CleanHost(name);
        var active = rows.Where(r => r.Enabled).ToList();
        foreach (var r in rows) r.Reset();

        await Task.WhenAll(active.Select(r => QueryResolver(r, name, type, rounds, ct)));

        var majority = active.Where(r => r.Normalized != "" && r.Level != Level.Bad)
            .GroupBy(r => r.Normalized).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        foreach (var r in active.Where(r => r.Level != Level.Bad))
        {
            if (r.Normalized == "") { r.Verdict = L.T("leer"); r.Level = Level.Warn; }
            else if (r.Normalized == majority) { r.Verdict = L.T("übereinstimmend"); r.Level = Level.Ok; }
            else { r.Verdict = L.T("abweichend"); r.Level = Level.Warn; }
        }
        int diff = active.Count(r => r.Level == Level.Warn && r.Normalized != "");
        var fastest = active.Where(r => r.AvgSort < double.MaxValue).OrderBy(r => r.AvgSort).FirstOrDefault();
        return L.F("{0} Resolver abgefragt · {1} abweichende Antworten", active.Count, diff)
               + (fastest != null ? L.F(" · schnellster: {0} ({1} ms)", fastest.Name, fastest.AvgText) : "")
               + (diff > 0 ? L.T(" · Abweichungen sind bei CDN/GeoDNS (z. B. A-Records großer Seiten) normal.") : "");
    }

    private static async Task QueryResolver(ResolverRow row, string name, QueryType type, int rounds, CancellationToken ct)
    {
        var times = new List<double>();
        try
        {
            List<(string Value, int Ttl)> answers = null;
            for (int i = 0; i < rounds; i++)
            {
                var sw = Stopwatch.StartNew();
                answers = row.IsDoh ? await DohAsync(row.Address, name, type, ct) : await ClassicAsync(row, name, type, ct);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            row.SetTimes(times);
            var values = answers.Select(a => a.Value).ToList();
            row.Answer = values.Count == 0 ? L.T("(keine Einträge)") : string.Join("\n", values);
            row.Ttl = answers.Count > 0 ? answers.Min(a => a.Ttl).ToString() : "";
            row.Normalized = string.Join("|", values.Select(Normalize).OrderBy(v => v));
        }
        catch (Exception ex)
        {
            row.SetTimes(times);
            row.Answer = ex is TaskCanceledException or OperationCanceledException || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? L.T("Zeitüberschreitung – Resolver nicht erreichbar")
                : ex.Message;
            row.Verdict = L.T("Fehler");
            row.Level = Level.Bad;
        }
    }

    private static string Normalize(string v) => v.Replace("\"", "").Trim().TrimEnd('.').ToLowerInvariant();

    private static async Task<List<(string, int)>> ClassicAsync(ResolverRow row, string name, QueryType type, CancellationToken ct)
    {
        var client = row.IsSystem ? DnsHelper.Create(timeoutMs: 3000, dnssec: false) : DnsHelper.Create(IPAddress.Parse(row.Address), timeoutMs: 3000, dnssec: false);
        var r = await client.QueryAsync(name, type, cancellationToken: ct);
        if (r.HasError && r.Header.ResponseCode != DnsHeaderResponseCode.NoError)
        {
            if (r.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain) return new();
            throw new Exception(r.ErrorMessage);
        }
        return r.Answers.Where(a => a.RecordType.ToString() == type.ToString())
            .Select(a => (DnsHelper.ValueOf(a), a.TimeToLive)).ToList();
    }

    private static async Task<List<(string, int)>> DohAsync(string url, string name, QueryType type, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}?name={Uri.EscapeDataString(name)}&type={type}");
        req.Headers.Accept.ParseAdd("application/dns-json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(5000);
        using var res = await NetUtil.Http.SendAsync(req, cts.Token);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
        var list = new List<(string, int)>();
        int typeNum = (int)type;
        if (doc.RootElement.TryGetProperty("Answer", out var ans))
        {
            foreach (var a in ans.EnumerateArray())
            {
                if (a.GetProperty("type").GetInt32() != typeNum) continue;
                list.Add((a.GetProperty("data").GetString(), a.TryGetProperty("TTL", out var ttl) ? ttl.GetInt32() : 0));
            }
        }
        return list;
    }
}

/* ====================================================================== Whois / RDAP */

public record WhoisResult(string Raw, List<CheckItem> Summary);

public static class WhoisLookup
{
    /// <summary>Whois mit Weiterleitung; Rohtext aller Server + Zusammenfassung (Ablaufdatum bewertet).</summary>
    public static async Task<WhoisResult> WhoisAsync(string query, string server, bool follow, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (Regex.IsMatch(q, @"^AS\d+$", RegexOptions.IgnoreCase) && string.IsNullOrWhiteSpace(server)) q = q.ToUpperInvariant();
        var hops = await WhoisClient.QueryAsync(q, server, follow, ct);
        var sb = new StringBuilder();
        foreach (var h in hops)
        {
            sb.AppendLine($"──────── {h.Server} ────────");
            sb.AppendLine(h.Response.Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim());
            sb.AppendLine();
        }
        var summary = WhoisClient.Summarize(hops.AsEnumerable().Reverse())
            .Select(kv => new CheckItem(kv.Key, kv.Value, kv.Key == "Läuft ab" ? ExpiryLevel(kv.Value) : Level.Info)).ToList();
        return new WhoisResult(sb.ToString(), summary);
    }

    /// <summary>RDAP über rdap.org (Domain, IP/Netz oder AS-Nummer). JSON formatiert + Zusammenfassung.</summary>
    public static async Task<WhoisResult> RdapAsync(string query, CancellationToken ct = default)
    {
        var q = query.Trim();
        string path;
        if (Regex.IsMatch(q, @"^AS\d+$", RegexOptions.IgnoreCase)) path = "autnum/" + q[2..];
        else if (IPAddress.TryParse(q, out _) || q.Contains('/')) path = "ip/" + q;
        else path = "domain/" + NetUtil.CleanHost(q);

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://rdap.org/" + path);
        req.Headers.Accept.ParseAdd("application/rdap+json, application/json");
        using var res = await NetUtil.Http.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            return new WhoisResult(L.F("RDAP: HTTP {0} – für diese TLD ist evtl. kein RDAP verfügbar. Whois verwenden.", (int)res.StatusCode) + "\n\n" + json, new());
        using var doc = JsonDocument.Parse(json);
        var pretty = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new WhoisResult(pretty, SummarizeRdap(doc.RootElement));
    }

    public static Level ExpiryLevel(string v)
    {
        var m = Regex.Match(v, @"\d{4}-\d{2}-\d{2}");
        if (m.Success && DateTime.TryParse(m.Value, out var d))
            return (d - DateTime.Now).TotalDays < 30 ? Level.Warn : Level.Ok;
        return Level.Info;
    }

    public static List<CheckItem> SummarizeRdap(JsonElement root)
    {
        var items = new List<CheckItem>();
        string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var name = S(root, "ldhName") ?? S(root, "name") ?? S(root, "handle");
        if (name != null) items.Add(new CheckItem("Objekt", name));
        if (S(root, "startAddress") is { } sa) items.Add(new CheckItem("Bereich", $"{sa} – {S(root, "endAddress")}"));
        if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Array)
            items.Add(new CheckItem("Status", string.Join(", ", st.EnumerateArray().Select(x => x.GetString()))));
        if (root.TryGetProperty("events", out var ev))
        {
            foreach (var e in ev.EnumerateArray())
            {
                var action = S(e, "eventAction");
                var date = S(e, "eventDate");
                if (action == null || date == null) continue;
                var label = action switch
                {
                    "registration" => "Erstellt",
                    "expiration" => "Läuft ab",
                    "last changed" => "Geändert",
                    "last update of RDAP database" => null,
                    _ => action,
                };
                if (label == null) continue;
                var lvl = Level.Info;
                if (action == "expiration" && DateTime.TryParse(date, out var d)) lvl = (d - DateTime.Now).TotalDays < 30 ? Level.Warn : Level.Ok;
                items.Add(new CheckItem(label, date.Length >= 10 ? date[..10] : date, lvl));
            }
        }
        if (root.TryGetProperty("entities", out var ents))
        {
            foreach (var en in ents.EnumerateArray())
            {
                var roles = en.TryGetProperty("roles", out var r) ? string.Join(", ", r.EnumerateArray().Select(x => x.GetString())) : "";
                var fn = VcardFn(en);
                if (!string.IsNullOrEmpty(fn))
                    items.Add(new CheckItem(roles switch { "registrar" => "Registrar", "registrant" => "Inhaber", "abuse" => "Abuse", _ => roles }, fn));
            }
        }
        if (root.TryGetProperty("nameservers", out var ns))
            items.Add(new CheckItem("Nameserver", string.Join(", ", ns.EnumerateArray().Select(n => S(n, "ldhName")).Where(x => x != null))));
        if (root.TryGetProperty("secureDNS", out var sd) && sd.TryGetProperty("delegationSigned", out var ds))
            items.Add(new CheckItem("DNSSEC", ds.GetBoolean() ? "signiert" : "nicht signiert", ds.GetBoolean() ? Level.Ok : Level.Info));
        if (S(root, "country") is { } c) items.Add(new CheckItem("Land", c));
        return items;
    }

    private static string VcardFn(JsonElement entity)
    {
        try
        {
            if (!entity.TryGetProperty("vcardArray", out var va) || va.GetArrayLength() < 2) return null;
            foreach (var prop in va[1].EnumerateArray())
            {
                if (prop[0].GetString() == "fn") return prop[3].GetString();
            }
        }
        catch { }
        return null;
    }
}

/* ====================================================================== Subnetzrechner */

public record SubnetSplitRow(int Index, string Network, string Hosts);

/// <summary>Ergebnis einer Subnetzberechnung (IPv4 oder IPv6).</summary>
public class SubnetInfo
{
    public BigInteger Network { get; init; }
    public int Prefix { get; init; }
    public bool IPv6 { get; init; }
    public List<CheckSection> Sections { get; init; } = new();
}

public static class SubnetCalculator
{
    /// <summary>Eingabe wie „192.168.1.10/24“, „10.0.0.1 255.255.0.0“ oder „2001:db8::1/48“. Wirft bei ungültiger Eingabe.</summary>
    public static SubnetInfo Calculate(string input)
    {
        var parts = input.Trim().Split(new[] { '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        IPAddress ip;
        try { ip = IPAddress.Parse(parts[0]); }
        catch (FormatException) { throw new FormatException(L.T("Ungültige Adresse.")); }
        bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        int bits = v6 ? 128 : 32;
        int prefix;
        if (parts.Length > 1)
        {
            if (parts[1].Contains('.'))
            {
                var mask = NetUtil.ToUInt(IPAddress.Parse(parts[1]));
                prefix = NetUtil.MaskToPrefix(mask);
                if (NetUtil.PrefixToMask(prefix) != mask) throw new Exception(L.T("Ungültige Subnetzmaske (nicht zusammenhängend)."));
            }
            else prefix = int.Parse(parts[1]);
        }
        else prefix = v6 ? 64 : 24;
        if (prefix < 0 || prefix > bits) throw new Exception(L.F("Präfix muss zwischen 0 und {0} liegen.", bits));
        return v6 ? V6(ip, prefix) : V4(ip, prefix);
    }

    private static SubnetInfo V4(IPAddress ip, int prefix)
    {
        uint addr = NetUtil.ToUInt(ip);
        uint mask = NetUtil.PrefixToMask(prefix);
        uint net = addr & mask, bc = net | ~mask;
        long total = 1L << (32 - prefix);
        long usable = prefix >= 31 ? total : total - 2;
        uint first = prefix >= 31 ? net : net + 1, last = prefix >= 31 ? bc : bc - 1;

        string Bin(uint v) => string.Join(".", new[] { v >> 24, (v >> 16) & 255, (v >> 8) & 255, v & 255 }.Select(o => Convert.ToString((int)o, 2).PadLeft(8, '0')));
        byte b0 = (byte)(addr >> 24);
        string cls = b0 < 128 ? "A" : b0 < 192 ? "B" : b0 < 224 ? "C" : b0 < 240 ? "D (Multicast)" : "E (reserviert)";
        string kind = NetUtil.IsPrivate(ip) ? (b0 == 100 ? "Carrier-Grade-NAT (100.64/10)" : b0 == 169 ? "Link-Local (APIPA)" : b0 == 127 ? "Loopback" : "privat (RFC 1918)") : "öffentlich";

        return new SubnetInfo
        {
            Network = net, Prefix = prefix, IPv6 = false,
            Sections =
            {
                new CheckSection("IPv4-Netz", new[]
                {
                    new CheckItem("Adresse", $"{ip}/{prefix}"),
                    new CheckItem("Netzadresse", $"{NetUtil.FromUInt(net)}/{prefix}", Level.Ok),
                    new CheckItem("Subnetzmaske", NetUtil.FromUInt(mask).ToString()),
                    new CheckItem("Wildcard-Maske", NetUtil.FromUInt(~mask).ToString()),
                    new CheckItem("Broadcast", prefix >= 31 ? L.T("– (Punkt-zu-Punkt)") : NetUtil.FromUInt(bc).ToString()),
                    new CheckItem("Erster Host", NetUtil.FromUInt(first).ToString()),
                    new CheckItem("Letzter Host", NetUtil.FromUInt(last).ToString()),
                    new CheckItem("Nutzbare Hosts", usable.ToString("N0")),
                    new CheckItem("Adressen gesamt", total.ToString("N0")),
                    new CheckItem("Klasse / Art", L.F("Klasse {0} · {1}", L.T(cls), L.T(kind))),
                    new CheckItem("Reverse-Zone", ReverseZone(net, prefix)),
                }),
                new CheckSection("Binär", new[]
                {
                    new CheckItem("Adresse", Bin(addr)),
                    new CheckItem("Maske", Bin(mask)),
                    new CheckItem("Netz", Bin(net)),
                }),
            },
        };
    }

    public static string ReverseZone(uint net, int prefix)
    {
        var oct = new[] { net >> 24, (net >> 16) & 255, (net >> 8) & 255, net & 255 };
        if (prefix > 24)
            return $"{oct[2]}.{oct[1]}.{oct[0]}.in-addr.arpa  " + L.T("(klassenlose Delegation nach RFC 2317 nötig)");
        int full = Math.Max(1, (prefix + 7) / 8); // auf nächste Oktettgrenze aufrunden
        int zones = 1 << (full * 8 - prefix);
        var parts = oct.Take(full).Reverse().ToArray();
        var zone = string.Join(".", parts) + ".in-addr.arpa";
        if (zones == 1) return zone;
        var first = parts[0];
        var rest = string.Join(".", parts.Skip(1));
        return $"{first}–{first + zones - 1}.{rest}.in-addr.arpa  " + L.F("({0} Zonen)", zones);
    }

    private static SubnetInfo V6(IPAddress ip, int prefix)
    {
        var addr = ToBig(ip);
        var all = (BigInteger.One << 128) - 1;
        var mask = prefix == 0 ? BigInteger.Zero : (all >> (128 - prefix)) << (128 - prefix);
        var net = addr & mask;
        var last = net | (all ^ mask);
        var count = BigInteger.One << (128 - prefix);

        string kind = ip.IsIPv6LinkLocal ? "Link-Local (fe80::/10)" : ip.IsIPv6UniqueLocal ? "Unique Local (fc00::/7)" : IPAddress.IsLoopback(ip) ? "Loopback"
            : ip.IsIPv6Multicast ? "Multicast" : (ip.GetAddressBytes()[0] & 0xE0) == 0x20 ? "Global Unicast (2000::/3)" : "sonstige";

        return new SubnetInfo
        {
            Network = net, Prefix = prefix, IPv6 = true,
            Sections =
            {
                new CheckSection("IPv6-Netz", new[]
                {
                    new CheckItem("Adresse", $"{ip}/{prefix}"),
                    new CheckItem("Ausgeschrieben", Expand(ip)),
                    new CheckItem("Netz", $"{FromBig(net)}/{prefix}", Level.Ok),
                    new CheckItem("Erste Adresse", FromBig(net).ToString()),
                    new CheckItem("Letzte Adresse", FromBig(last).ToString()),
                    new CheckItem("Adressen", count > 1_000_000_000_000 ? $"2^{128 - prefix}" : count.ToString("N0")),
                    new CheckItem("/64-Netze darin", prefix <= 64 ? (BigInteger.One << (64 - prefix)).ToString("N0") : L.T("– (kleiner als /64)")),
                    new CheckItem("Art", kind),
                }),
            },
        };
    }

    /// <summary>Netz in kleinere Subnetze aufteilen (max. 1024 Zeilen). Titelzeile beschreibt das Ergebnis.</summary>
    public static (List<SubnetSplitRow> Rows, string Title) Split(SubnetInfo c, int newPrefix)
    {
        var rows = new List<SubnetSplitRow>();
        int bits = c.IPv6 ? 128 : 32;
        if (newPrefix <= c.Prefix || newPrefix > bits)
            return (rows, L.F("Aufteilen: Präfix muss größer als /{0} sein", c.Prefix));
        var count = BigInteger.One << (newPrefix - c.Prefix);
        var size = BigInteger.One << (bits - newPrefix);
        for (int i = 0; i < 1024 && i < count; i++)
        {
            var start = c.Network + size * i;
            var end = start + size - 1;
            string net, hosts;
            if (c.IPv6)
            {
                net = $"{FromBig(start)}/{newPrefix}";
                hosts = $"{FromBig(start)} – {FromBig(end)}";
            }
            else
            {
                net = $"{NetUtil.FromUInt((uint)start)}/{newPrefix}";
                hosts = newPrefix >= 31 ? $"{NetUtil.FromUInt((uint)start)} – {NetUtil.FromUInt((uint)end)}"
                    : $"{NetUtil.FromUInt((uint)(start + 1))} – {NetUtil.FromUInt((uint)(end - 1))}";
            }
            rows.Add(new SubnetSplitRow(i + 1, net, hosts));
        }
        var title = L.F("{0:N0} Subnetze /{1}", count, newPrefix) + (count > 1024 ? L.T(" (erste 1024 angezeigt)") : "") +
                    (c.IPv6 ? "" : L.F(" · je {0:N0} Hosts", newPrefix >= 31 ? size : size - 2));
        return (rows, title);
    }

    private static string Expand(IPAddress ip) =>
        string.Join(":", Enumerable.Range(0, 8).Select(i => ((ip.GetAddressBytes()[i * 2] << 8) | ip.GetAddressBytes()[i * 2 + 1]).ToString("x4")));

    private static BigInteger ToBig(IPAddress ip) => new(ip.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    private static IPAddress FromBig(BigInteger v)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[16];
        Array.Copy(bytes, 0, full, 16 - bytes.Length, bytes.Length);
        return new IPAddress(full);
    }
}

/* ====================================================================== Wake-on-LAN */

public class WolDevice
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Target { get; set; } = "255.255.255.255";
    public int Port { get; set; } = 9;
}

public static class WakeOnLan
{
    public static List<WolDevice> LoadDevices() => JsonStore.LoadList<WolDevice>("wol.json");

    public static void SaveDevices(IEnumerable<WolDevice> devices) => JsonStore.SaveList("wol.json", devices);

    public static byte[] ParseMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new Exception(L.T("MAC-Adresse muss 12 Hex-Zeichen haben."));
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    public static byte[] MagicPacket(byte[] mac)
    {
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Array.Copy(mac, 0, packet, i * 6, 6);
        return packet;
    }

    /// <summary>Lokale Broadcast-Adresse des Subnetzes, in dem <paramref name="ip"/> liegt (oder null).</summary>
    public static IPAddress SubnetBroadcastFor(uint ip)
    {
        var local = NetUtil.ActiveIPv4().FirstOrDefault(l =>
            (NetUtil.ToUInt(l.Addr.Address) & NetUtil.PrefixToMask(l.Addr.PrefixLength)) == (ip & NetUtil.PrefixToMask(l.Addr.PrefixLength)));
        return local.Addr == null ? null : NetUtil.FromUInt(NetUtil.ToUInt(local.Addr.Address) | ~NetUtil.PrefixToMask(local.Addr.PrefixLength));
    }

    /// <summary>Sendet das Magic Packet dreimal; bei 255.255.255.255 zusätzlich an alle lokalen Subnetz-Broadcasts.</summary>
    public static async Task SendAsync(string mac, string target, int port, CancellationToken ct = default)
    {
        var packet = MagicPacket(ParseMac(mac));
        var ip = IPAddress.TryParse(target, out var t) ? t : await NetUtil.ResolveAsync(target, false, ct);
        using var udp = new UdpClient(ip.AddressFamily) { EnableBroadcast = true };
        for (int i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(ip, port));
            await Task.Delay(100, ct);
        }
        if (ip.Equals(IPAddress.Broadcast))
        {
            foreach (var l in NetUtil.ActiveIPv4())
            {
                var bc = NetUtil.FromUInt(NetUtil.ToUInt(l.Addr.Address) | ~NetUtil.PrefixToMask(l.Addr.PrefixLength));
                try { await udp.SendAsync(packet, packet.Length, new IPEndPoint(bc, port)); } catch { }
            }
        }
    }
}
