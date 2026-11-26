using System.Net;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;

namespace NetStuff.Services;

public class DnsRow
{
    public string Type { get; init; }
    public string Name { get; init; }
    public int Ttl { get; init; }
    public string Value { get; init; }
    public string TtlText => Ttl >= 3600 ? $"{Ttl} ({Ttl / 3600.0:0.#} h)" : Ttl >= 60 ? $"{Ttl} ({Ttl / 60.0:0.#} min)" : Ttl.ToString();
}

public static class DnsHelper
{
    public static readonly QueryType[] CommonTypes =
    {
        QueryType.A, QueryType.AAAA, QueryType.CNAME, QueryType.MX, QueryType.NS, QueryType.TXT,
        QueryType.SOA, QueryType.CAA, QueryType.SRV, QueryType.PTR, QueryType.DS, QueryType.DNSKEY, QueryType.TLSA,
    };

    public static LookupClient Create(IPAddress server = null, bool tcpOnly = false, int timeoutMs = 4000, bool dnssec = true)
    {
        var options = server == null ? new LookupClientOptions(SystemNameServers().ToArray()) : new LookupClientOptions(new NameServer(server));
        options.UseCache = false;
        options.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        options.Retries = 1;
        options.UseTcpOnly = tcpOnly;
        options.ThrowDnsErrors = false;
        options.ContinueOnDnsError = false;
        options.RequestDnsSecRecords = dnssec;
        return new LookupClient(options);
    }

    private static IReadOnlyCollection<NameServer> _systemServers;

    /// <summary>
    /// Nameserver des Systems. iOS gibt sie in der App-Sandbox nicht heraus (/etc/resolv.conf ist nicht lesbar) –
    /// dann öffentliche Resolver verwenden, statt mit „List of configured name servers must not be empty“ abzubrechen.
    /// </summary>
    public static IReadOnlyCollection<NameServer> SystemNameServers()
    {
        if (_systemServers != null) return _systemServers;
        IReadOnlyCollection<NameServer> found = null;
        try { found = NameServer.ResolveNameServers(skipIPv6SiteLocal: true, fallbackToGooglePublicDns: false); } catch { }
        _systemServers = found is { Count: > 0 } ? found : PublicFallback;
        return _systemServers;
    }

    /// <summary>True, wenn statt der System-Nameserver öffentliche Resolver verwendet werden.</summary>
    public static bool UsesPublicFallback => ReferenceEquals(SystemNameServers(), PublicFallback);

    private static readonly NameServer[] PublicFallback =
    {
        new(IPAddress.Parse("1.1.1.1")), new(IPAddress.Parse("8.8.8.8")), new(IPAddress.Parse("9.9.9.9")),
    };

    /// <summary>Datenteil eines Records (ohne Name, TTL, Klasse, Typ).</summary>
    public static string ValueOf(DnsResourceRecord rec)
    {
        switch (rec)
        {
            case ARecord a: return a.Address.ToString();
            case AaaaRecord a: return a.Address.ToString();
            case TxtRecord t: return string.Join("", t.Text);
        }
        var s = rec.ToString();
        var m = Regex.Match(s, @"^\S+\s+\d+\s+\S+\s+\S+\s+(.*)$", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : s;
    }

    public static string TypeOf(DnsResourceRecord rec) => rec.RecordType.ToString();

    public static IEnumerable<DnsRow> Rows(IEnumerable<DnsResourceRecord> records) =>
        records.Select(r => new DnsRow { Type = TypeOf(r), Name = r.DomainName.Value.TrimEnd('.'), Ttl = r.TimeToLive, Value = ValueOf(r) });

    public static async Task<List<string>> TxtAsync(LookupClient client, string name, CancellationToken ct = default)
    {
        var r = await client.QueryAsync(name, QueryType.TXT, QueryClass.IN, ct);
        return r.Answers.TxtRecords().Select(t => string.Join("", t.Text)).ToList();
    }

    /// <summary>IP des ersten autoritativen Nameservers einer Domain.</summary>
    public static async Task<(IPAddress Ip, string Name)> AuthoritativeAsync(string domain, CancellationToken ct = default)
    {
        var client = Create();
        var labels = domain.TrimEnd('.').Split('.');
        for (int i = 0; i < labels.Length - 1; i++)
        {
            var zone = string.Join(".", labels.Skip(i));
            var r = await client.QueryAsync(zone, QueryType.NS, QueryClass.IN, ct);
            var ns = r.Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.')).FirstOrDefault();
            if (ns == null) continue;
            var addrs = await Dns.GetHostAddressesAsync(ns, ct);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
            if (ip != null) return (ip, ns);
        }
        throw new Exception("Kein autoritativer Nameserver gefunden.");
    }

    public static string RcodeText(DnsResponseCode code) => code switch
    {
        DnsResponseCode.NoError => "NOERROR",
        DnsResponseCode.NotExistentDomain => "NXDOMAIN – Domain existiert nicht",
        DnsResponseCode.ServerFailure => "SERVFAIL – Serverfehler (evtl. DNSSEC-Validierung fehlgeschlagen)",
        DnsResponseCode.Refused => "REFUSED – Abfrage abgelehnt",
        _ => code.ToString(),
    };
}
