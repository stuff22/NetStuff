using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

namespace NetStuff.Services;

public class SnmpRow
{
    public string Oid { get; init; }
    public string Name { get; init; }
    public string Type { get; init; }
    public string Value { get; init; }
}

public class SnmpInterface
{
    public int Index { get; init; }
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Status { get; set; } = "";
    public string Speed { get; set; } = "";
    public string Mac { get; set; } = "";
    public string InOctets { get; set; } = "";
    public string OutOctets { get; set; } = "";
    public string Errors { get; set; } = "";
    public string Vlan { get; set; } = "";
}

/// <summary>SNMP v1/v2c-Abfragen (Get/Walk) über SharpSnmpLib.</summary>
public class SnmpService
{
    private readonly IPEndPoint _ep;
    private readonly OctetString _community;
    private readonly VersionCode _version;
    private const int Timeout = 3000;

    public SnmpService(IPAddress ip, int port, string community, bool v1)
    {
        _ep = new IPEndPoint(ip, port);
        _community = new OctetString(community);
        _version = v1 ? VersionCode.V1 : VersionCode.V2;
    }

    public static readonly Dictionary<string, string> KnownOids = new()
    {
        ["1.3.6.1.2.1.1.1.0"] = "sysDescr", ["1.3.6.1.2.1.1.2.0"] = "sysObjectID", ["1.3.6.1.2.1.1.3.0"] = "sysUpTime",
        ["1.3.6.1.2.1.1.4.0"] = "sysContact", ["1.3.6.1.2.1.1.5.0"] = "sysName", ["1.3.6.1.2.1.1.6.0"] = "sysLocation",
        ["1.3.6.1.2.1.1.7.0"] = "sysServices",
    };

    public Task<IList<Variable>> GetAsync(params string[] oids) => Task.Run(() =>
        Messenger.Get(_version, _ep, _community, oids.Select(o => new Variable(new ObjectIdentifier(o))).ToList(), Timeout));

    public Task<List<Variable>> WalkAsync(string root, int max = 5000) => Task.Run(() =>
    {
        var list = new List<Variable>();
        Messenger.Walk(_version, _ep, _community, new ObjectIdentifier(root), list, Timeout, WalkMode.WithinSubtree);
        return list.Take(max).ToList();
    });

    public static string Format(ISnmpData data)
    {
        switch (data)
        {
            case TimeTicks tt:
                var ts = TimeSpan.FromMilliseconds(tt.ToUInt32() * 10.0);
                return $"{(int)ts.TotalDays} d {ts.Hours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            case OctetString os:
                var bytes = os.GetRaw();
                if (bytes.Length > 0 && bytes.All(b => b >= 0x20 && b < 0x7F || b is 0x0A or 0x0D or 0x09))
                    return os.ToString().Trim('\0', ' ', '\r', '\n');
                if (bytes.Length == 6) return NetUtil.FormatMac(bytes);
                return BitConverter.ToString(bytes).Replace('-', ' ');
            case NoSuchObject or NoSuchInstance or EndOfMibView:
                return "–";
            default:
                return data?.ToString() ?? "";
        }
    }

    public async Task<List<(string Name, string Value)>> SystemInfoAsync()
    {
        var vars = await GetAsync(KnownOids.Keys.ToArray());
        return vars.Select(v => (KnownOids.TryGetValue(v.Id.ToString(), out var n) ? n : v.Id.ToString(), Format(v.Data))).ToList();
    }

    public async Task<List<SnmpInterface>> InterfacesAsync()
    {
        var map = new Dictionary<int, SnmpInterface>();
        SnmpInterface Get(int idx) => map.TryGetValue(idx, out var i) ? i : map[idx] = new SnmpInterface { Index = idx };
        int Idx(Variable v) => int.Parse(v.Id.ToString().Split('.').Last());

        foreach (var v in await WalkAsync("1.3.6.1.2.1.2.2.1.2")) Get(Idx(v)).Name = Format(v.Data);
        foreach (var v in await WalkAsync("1.3.6.1.2.1.2.2.1.8"))
            Get(Idx(v)).Status = v.Data.ToString() switch { "1" => "up", "2" => "down", "3" => "testing", "5" => "dormant", "7" => "lowerLayerDown", var x => x };
        foreach (var v in await WalkAsync("1.3.6.1.2.1.2.2.1.6")) Get(Idx(v)).Mac = Format(v.Data);
        foreach (var v in await WalkAsync("1.3.6.1.2.1.2.2.1.14")) Get(Idx(v)).Errors = v.Data.ToString();

        // 64-Bit-Zähler und Aliasse (IF-MIB ifXTable), Fallback auf 32-Bit
        var high = await SafeWalk("1.3.6.1.2.1.31.1.1.1.15");
        foreach (var v in high) { var mbit = long.Parse(v.Data.ToString()); Get(Idx(v)).Speed = mbit >= 1000 ? $"{mbit / 1000.0:0.#} Gbit/s" : mbit > 0 ? $"{mbit} Mbit/s" : ""; }
        if (high.Count == 0)
            foreach (var v in await SafeWalk("1.3.6.1.2.1.2.2.1.5")) Get(Idx(v)).Speed = NetUtil.FormatSpeed(long.Parse(v.Data.ToString()));
        foreach (var v in await SafeWalk("1.3.6.1.2.1.31.1.1.1.18")) Get(Idx(v)).Alias = Format(v.Data);
        var inHc = await SafeWalk("1.3.6.1.2.1.31.1.1.1.6");
        var outHc = await SafeWalk("1.3.6.1.2.1.31.1.1.1.10");
        if (inHc.Count == 0) inHc = await SafeWalk("1.3.6.1.2.1.2.2.1.10");
        if (outHc.Count == 0) outHc = await SafeWalk("1.3.6.1.2.1.2.2.1.16");
        foreach (var v in inHc) Get(Idx(v)).InOctets = NetUtil.FormatBytes(long.Parse(v.Data.ToString()));
        foreach (var v in outHc) Get(Idx(v)).OutOctets = NetUtil.FormatBytes(long.Parse(v.Data.ToString()));

        // Port-VLAN (Q-BRIDGE-MIB dot1qPvid, über Bridge-Port ≈ ifIndex bei vielen Switches)
        foreach (var v in await SafeWalk("1.3.6.1.2.1.17.7.1.4.5.1.1"))
            if (map.TryGetValue(Idx(v), out var i)) i.Vlan = v.Data.ToString();

        return map.Values.OrderBy(i => i.Index).ToList();
    }

    public async Task<(ulong In, ulong Out)> CountersAsync(int ifIndex)
    {
        var vars = await GetAsync($"1.3.6.1.2.1.31.1.1.1.6.{ifIndex}", $"1.3.6.1.2.1.31.1.1.1.10.{ifIndex}");
        ulong P(ISnmpData d) => d is Counter64 c ? c.ToUInt64() : d is Counter32 c32 ? c32.ToUInt32() : 0;
        if (vars.Count == 2 && vars[0].Data is Counter64)
            return (P(vars[0].Data), P(vars[1].Data));
        vars = await GetAsync($"1.3.6.1.2.1.2.2.1.10.{ifIndex}", $"1.3.6.1.2.1.2.2.1.16.{ifIndex}");
        return (P(vars[0].Data), P(vars[1].Data));
    }

    /// <summary>LLDP-Nachbarn des Geräts (LLDP-MIB).</summary>
    public async Task<List<(string LocalPort, string Neighbor, string RemotePort, string Description)>> LldpNeighborsAsync()
    {
        var locPorts = new Dictionary<string, string>();
        foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.3.7.1.4")) locPorts[v.Id.ToString().Split('.').Last()] = Format(v.Data);
        if (locPorts.Count == 0)
            foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.3.7.1.3")) locPorts[v.Id.ToString().Split('.').Last()] = Format(v.Data);

        var rows = new Dictionary<string, (string L, string N, string P, string D)>();
        string Key(Variable v) { var p = v.Id.ToString().Split('.'); return string.Join(".", p.Skip(p.Length - 3)); }
        string Loc(string key) { var lp = key.Split('.')[1]; return locPorts.TryGetValue(lp, out var n) ? n : "Port " + lp; }

        foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.4.1.1.9")) { var k = Key(v); rows[k] = (Loc(k), Format(v.Data), "", ""); }
        foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.4.1.1.7"))
        {
            var k = Key(v);
            if (!rows.TryGetValue(k, out var r)) r = (Loc(k), "", "", "");
            rows[k] = (r.L, r.N, Format(v.Data), r.D);
        }
        foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.4.1.1.8")) { var k = Key(v); if (rows.TryGetValue(k, out var r) && Format(v.Data) is { Length: > 0 } pd) rows[k] = (r.L, r.N, string.IsNullOrEmpty(r.P) ? pd : $"{r.P} ({pd})", r.D); }
        foreach (var v in await SafeWalk("1.0.8802.1.1.2.1.4.1.1.10")) { var k = Key(v); if (rows.TryGetValue(k, out var r)) rows[k] = (r.L, r.N, r.P, Format(v.Data)); }
        return rows.Values.Select(r => (r.L, r.N, r.P, r.D)).ToList();
    }

    /// <summary>Druckerverbrauchsmaterial (Printer-MIB).</summary>
    public async Task<List<(string Name, int Level, int Max)>> SuppliesAsync()
    {
        var names = await SafeWalk("1.3.6.1.2.1.43.11.1.1.6");
        var max = (await SafeWalk("1.3.6.1.2.1.43.11.1.1.8")).ToDictionary(v => v.Id.ToString().Split('.').Last(), v => int.Parse(v.Data.ToString()));
        var lvl = (await SafeWalk("1.3.6.1.2.1.43.11.1.1.9")).ToDictionary(v => v.Id.ToString().Split('.').Last(), v => int.Parse(v.Data.ToString()));
        return names.Select(n =>
        {
            var k = n.Id.ToString().Split('.').Last();
            return (Format(n.Data), lvl.GetValueOrDefault(k, -3), max.GetValueOrDefault(k, -2));
        }).ToList();
    }

    private async Task<List<Variable>> SafeWalk(string oid)
    {
        try { return await WalkAsync(oid); } catch { return new List<Variable>(); }
    }
}
