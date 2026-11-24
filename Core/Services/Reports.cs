using System.Net;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== DHCP */

public class DhcpAdapterChoice
{
    public string Label { get; init; }
    public IPAddress Ip { get; init; }
    public byte[] Mac { get; init; }
    /// <summary>Schnittstellenindex (macOS: Bindung des Sockets an den Adapter).</summary>
    public int InterfaceIndex { get; init; }
    public override string ToString() => Label;
}

/// <summary>DHCP-Test: Adapterauswahl, Durchführung und Bewertung (warnt bei mehreren Servern).</summary>
public static class DhcpCheck
{
    public static List<DhcpAdapterChoice> Adapters() => NetUtil.ActiveIPv4()
        .Where(x => x.Nic.GetPhysicalAddress().GetAddressBytes().Length == 6)
        .OrderByDescending(x => x.Gateway != null)
        .Select(x => new DhcpAdapterChoice
        {
            Label = $"{x.Nic.Name} – {x.Addr.Address}/{x.Addr.PrefixLength}",
            Ip = x.Addr.Address,
            Mac = x.Nic.GetPhysicalAddress().GetAddressBytes(),
            InterfaceIndex = IndexOf(x.Nic),
        }).ToList();

    private static int IndexOf(System.Net.NetworkInformation.NetworkInterface nic)
    {
        try { return nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0; }
        catch { return 0; }
    }

    /// <summary>Zufällige, lokal verwaltete Unicast-MAC (damit der echte Lease nicht verändert wird).</summary>
    public static byte[] RandomMac()
    {
        var mac = new byte[6];
        Random.Shared.NextBytes(mac);
        mac[0] = (byte)((mac[0] & 0xFE) | 0x02);
        return mac;
    }

    public static async Task<List<CheckSection>> RunAsync(DhcpAdapterChoice adapter, bool randomMac, int waitSeconds, CancellationToken ct = default)
    {
        _ = OuiDatabase.EnsureLoadedAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(waitSeconds + 5));
        var offers = await DhcpTester.DiscoverAsync(adapter.Ip, randomMac ? RandomMac() : adapter.Mac, waitSeconds * 1000, cts.Token, adapter.InterfaceIndex);
        return Evaluate(offers);
    }

    public static List<CheckSection> Evaluate(List<DhcpOffer> offers)
    {
        var sections = new List<CheckSection>();
        if (offers.Count == 0)
        {
            sections.Add(CheckSection.Message(
                "Kein DHCP-Server hat geantwortet. Mögliche Ursachen: statische IP-Konfiguration im Netz, eine Firewall blockiert eingehende Antworten (bei Nachfrage Zugriff erlauben) oder DHCP-Server antwortet nur bekannten Geräten.", Level.Warn));
            return sections;
        }
        var servers = offers.Select(o => o.Server).Distinct().ToList();
        sections.Add(servers.Count > 1
            ? new CheckSection("Bewertung", new[] { new CheckItem("Mehrere DHCP-Server!", L.F("{0} verschiedene Server antworten: {1} – das führt zu IP-Konflikten. Fremden Server suchen (z. B. versehentlich angeschlossener Router).", servers.Count, string.Join(", ", servers)), Level.Bad) })
            : new CheckSection("Bewertung", new[] { new CheckItem("DHCP-Server", L.F("Genau ein Server antwortet ({0}) ✓", servers[0]), Level.Ok) }));

        foreach (var o in offers)
        {
            var items = new List<CheckItem>
            {
                new("DHCP-Server", o.Server + (o.RelayOrSource != o.Server ? L.F("  (Antwort über {0} – Relay?)", o.RelayOrSource) : ""), Level.Ok),
                new("Angebotene IP", o.OfferedIp),
                new("Subnetzmaske", o.SubnetMask),
                new("Gateway", o.Router),
                new("DNS-Server", o.Dns),
            };
            if (o.Domain != "") items.Add(new CheckItem("Domain", o.Domain));
            if (o.Lease != "") items.Add(new CheckItem("Lease-Dauer", o.Lease));
            if (o.Ntp != "") items.Add(new CheckItem("NTP-Server", o.Ntp));
            if (o.BootServer != "" || o.BootFile != "") items.Add(new CheckItem("PXE / Boot", $"{o.BootServer} {o.BootFile}".Trim()));
            if (o.Other != "") items.Add(new CheckItem("Weitere Optionen", o.Other));
            items.Add(new CheckItem("Antwortzeit", $"{o.Ms:0} ms"));
            try
            {
                var smac = NetUtil.Arp(IPAddress.Parse(o.Server));
                if (smac != null) { var m = NetUtil.FormatMac(smac); items.Add(new CheckItem("Server-MAC / Hersteller", $"{m} ({OuiDatabase.Lookup(m)})")); }
            }
            catch { }
            sections.Add(new CheckSection(L.F("Angebot von {0}", o.Server), items));
        }
        return sections;
    }
}

/* ====================================================================== LLDP / CDP */

public static class LldpReport
{
    public static List<CheckSection> Sections(List<NeighborInfo> list)
    {
        var sections = new List<CheckSection>();
        if (list.Count == 0)
        {
            sections.Add(CheckSection.Message(
                "Keine LLDP-/CDP-Pakete empfangen. Mögliche Ursachen: LLDP/CDP am Switch deaktiviert, unmanaged Switch, WLAN-Verbindung (LLDP gibt es nur am Kabel) oder Dauer zu kurz.", Level.Warn));
            return sections;
        }
        foreach (var n in list)
        {
            var items = new List<CheckItem>
            {
                new("Gerät", n.DeviceName == "" ? "(kein Name übermittelt)" : n.DeviceName, Level.Ok),
                new("Switch-Port", n.PortId + (n.PortDescription != "" && n.PortDescription != n.PortId ? $"  ({n.PortDescription})" : ""), Level.Ok),
            };
            if (n.Vlan != "") items.Add(new CheckItem("VLAN (Port / nativ)", n.Vlan, Level.Ok));
            if (n.VoiceVlan != "") items.Add(new CheckItem("Voice-VLAN", n.VoiceVlan));
            if (n.ManagementIp != "") items.Add(new CheckItem("Management-IP", n.ManagementIp));
            if (n.ChassisId != "") items.Add(new CheckItem("Chassis-ID", n.ChassisId));
            if (n.Platform != "") items.Add(new CheckItem("Plattform", n.Platform));
            if (n.Software != "") items.Add(new CheckItem("Systembeschreibung", n.Software.Length > 300 ? n.Software[..300] + " …" : n.Software));
            if (n.Capabilities != "") items.Add(new CheckItem("Funktionen", n.Capabilities));
            if (n.Duplex != "") items.Add(new CheckItem("Geschwindigkeit / Duplex", n.Duplex + (n.AutoNeg != "" ? L.F(" · Autonegotiation {0}", L.T(n.AutoNeg)) : "")));
            if (n.MaxFrame != "") items.Add(new CheckItem("Max. Framegröße", n.MaxFrame + " Byte"));
            if (n.Poe != "") items.Add(new CheckItem("PoE", n.Poe));
            items.Add(new CheckItem("Absender-MAC", $"{n.SourceMac}  ({OuiDatabase.Lookup(n.SourceMac)})"));
            sections.Add(new CheckSection($"{n.Protocol}: {n.DeviceName}", items));
        }
        return sections;
    }
}

/* ====================================================================== SNMP */

public static class SnmpReport
{
    private static readonly Dictionary<string, string> SystemLabels = new()
    {
        ["sysName"] = "Name", ["sysDescr"] = "Beschreibung", ["sysUpTime"] = "Laufzeit", ["sysContact"] = "Kontakt",
        ["sysLocation"] = "Standort", ["sysObjectID"] = "Objekt-ID", ["sysServices"] = "Dienste",
    };

    /// <summary>Verständliche Meldung für SNMP-Fehler (Timeout = meist falsche Community/Version oder SNMP aus).</summary>
    public static string ErrorText(Exception ex) =>
        ex.GetType().Name.Contains("Timeout") || ex is System.Net.Sockets.SocketException || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            ? L.T("Keine Antwort – SNMP am Gerät aktiviert? Community und Version korrekt? UDP 161 erreichbar?")
            : ex.Message;

    public static CheckSection System(IEnumerable<(string Name, string Value)> info) =>
        new("System", info.Select(i => new CheckItem(SystemLabels.GetValueOrDefault(i.Name, i.Name), i.Value, Level.Info)));

    public static CheckSection LldpNeighbors(IReadOnlyCollection<(string LocalPort, string Neighbor, string RemotePort, string Description)> n) =>
        n.Count == 0
            ? CheckSection.Message("Keine LLDP-Nachbarn gefunden (LLDP am Gerät deaktiviert oder MIB nicht unterstützt).", Level.Warn)
            : new CheckSection("LLDP-Nachbarn", n.Select(x => new CheckItem(x.LocalPort, $"{x.Neighbor} · Port {x.RemotePort}" + (x.Description != "" ? $"\n{x.Description}" : ""), Level.Info)));

    /// <summary>OID-Walk als Tabellenzeilen (mit bekannten OID-Namen).</summary>
    public static async Task<List<SnmpRow>> WalkAsync(SnmpService c, string oid) =>
        (await c.WalkAsync(oid)).Select(v => new SnmpRow
        {
            Oid = v.Id.ToString(),
            Name = SnmpService.KnownOids.GetValueOrDefault(v.Id.ToString(), ""),
            Type = v.Data.TypeCode.ToString(),
            Value = SnmpService.Format(v.Data),
        }).ToList();

    /// <summary>Tonerstände/Verbrauchsmaterial: (Name, Füllstand, Maximum) – negative Werte laut Printer-MIB.</summary>
    public static CheckSection Supplies(IEnumerable<(string Name, int Level, int Max)> s)
    {
        var list = s.ToList();
        if (list.Count == 0) return CheckSection.Message("Keine Printer-MIB-Daten – kein Drucker oder SNMP eingeschränkt.", Level.Warn);
        return new CheckSection("Verbrauchsmaterial", list.Select(x =>
        {
            if (x.Level >= 0 && x.Max > 0)
            {
                var pct = 100.0 * x.Level / x.Max;
                return new CheckItem(x.Name, $"{pct:0} %  ({x.Level}/{x.Max})", pct < 10 ? Level.Bad : pct < 25 ? Level.Warn : Level.Ok);
            }
            return new CheckItem(x.Name, x.Level == -3 ? L.T("vorhanden (Füllstand nicht messbar)") : x.Level == -2 ? L.T("unbekannt") : x.Level.ToString(), Level.Info);
        }));
    }

    /// <summary>Live-Traffic einer Schnittstelle: liefert alle <paramref name="intervalMs"/> die Raten (Mbit/s) bis zum Abbruch.</summary>
    public static async Task TrafficAsync(SnmpService c, int ifIndex, int intervalMs, Action<double, double> sample, CancellationToken ct)
    {
        var (lastIn, lastOut) = await c.CountersAsync(ifIndex);
        var last = DateTime.Now;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(intervalMs, ct);
            var (i, o) = await c.CountersAsync(ifIndex);
            var dt = (DateTime.Now - last).TotalSeconds;
            last = DateTime.Now;
            double inMbps = i >= lastIn ? (i - lastIn) * 8 / 1e6 / dt : 0;
            double outMbps = o >= lastOut ? (o - lastOut) * 8 / 1e6 / dt : 0;
            lastIn = i; lastOut = o;
            sample(inMbps, outMbps);
        }
    }
}

/* ====================================================================== Speedtest / iPerf3 */

public static class SpeedReport
{
    public static string Mbps(double v) => v >= 100 ? v.ToString("0") : v.ToString("0.0");

    public static CheckItem Internet(double down, double up, double latency, double jitter, string asOrg, string colo) =>
        new(L.F("Internet {0:HH:mm}", DateTime.Now),
            L.F("↓ {0} Mbit/s · ↑ {1} Mbit/s · Ping {2:0.0} ms · Jitter {3:0.0} ms · {4} ({5})", Mbps(down), Mbps(up), latency, jitter, asOrg, colo),
            down > 0 ? Level.Ok : Level.Warn);

    public static CheckItem Iperf(IperfResult res, int streams, int seconds, string host) =>
        new(L.F("iPerf3 {0} {1:HH:mm}", L.T(res.Direction), DateTime.Now),
            L.F("{0} Mbit/s · {1} · {2} Streams, {3} s · Server {4}", Mbps(res.Mbps), NetUtil.FormatBytes(res.Bytes), streams, seconds, host)
            + (res.Retransmits >= 0 ? L.F(" · Retransmits {0}", res.Retransmits) : ""), Level.Ok);

    /// <summary>
    /// iPerf3-Test in eine Richtung; ist ein Port belegt oder nicht erreichbar, wird der nächste aus <paramref name="ports"/> versucht.
    /// <paramref name="portIndex"/> merkt sich den zuletzt erfolgreichen Port über mehrere Richtungen hinweg.
    /// </summary>
    public static async Task<IperfResult> IperfWithFallbackAsync(string host, IReadOnlyList<int> ports, int seconds, int streams, bool reverse,
        IProgress<SpeedSample> progress, Action<string> status, Func<int> getIndex, Action<int> setIndex, int maxAttempts, CancellationToken ct)
    {
        while (true)
        {
            int idx = getIndex();
            int port = ports[idx % ports.Count];
            status?.Invoke(L.F("{0} zu {1}:{2} läuft …", reverse ? "Download" : "Upload", host, port));
            try
            {
                return await IperfClient.RunAsync(host, port, seconds, streams, reverse, progress, ct);
            }
            catch (Exception busy) when (busy is IperfBusyException or System.Net.Sockets.SocketException && ports.Count > 1)
            {
                setIndex(++idx);
                if (idx >= maxAttempts) throw new Exception(L.T("Kein Port des Servers verfügbar (belegt oder nicht erreichbar) – bitte später erneut versuchen oder anderen Server wählen."));
                await Task.Delay(500, ct);
            }
        }
    }
}
