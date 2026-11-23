using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetStuff.Services;

public static class WhoisClient
{
    public class Hop
    {
        public string Server { get; set; }
        public string Response { get; set; }
    }

    /// <summary>Whois-Abfrage mit Weiterleitung (IANA -> Registry -> Registrar).</summary>
    public static async Task<List<Hop>> QueryAsync(string query, string server = null, bool follow = true, CancellationToken ct = default)
    {
        query = query.Trim();
        if (!IPAddress.TryParse(query, out _)) query = NetUtil.CleanHost(query).ToLowerInvariant();

        var hops = new List<Hop>();
        server = string.IsNullOrWhiteSpace(server) ? "whois.iana.org" : server.Trim();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 4 && server != null && visited.Add(server); i++)
        {
            string text;
            try { text = await RawAsync(server, FormatQuery(server, query), ct); }
            catch (Exception ex)
            {
                hops.Add(new Hop { Server = server, Response = "Fehler: " + ex.Message });
                break;
            }
            hops.Add(new Hop { Server = server, Response = text });
            if (!follow) break;
            server = FindReferral(text, server);
        }
        return hops;
    }

    private static string FormatQuery(string server, string q)
    {
        return server.ToLowerInvariant() switch
        {
            "whois.denic.de" => "-T dn,ace " + q,
            "whois.verisign-grs.com" when !IPAddress.TryParse(q, out _) => "domain " + q,
            "whois.arin.net" when IPAddress.TryParse(q, out _) => "n + " + q,
            _ => q,
        };
    }

    private static string FindReferral(string text, string current)
    {
        string[] patterns =
        {
            @"^\s*refer:\s*(\S+)",
            @"^\s*whois:\s*(\S+)",
            @"^\s*Registrar WHOIS Server:\s*(\S+)",
            @"^\s*ReferralServer:\s*(?:whois|rwhois)://([^\s:/]+)",
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var s = m.Groups[1].Value.Trim().TrimEnd('/');
                s = Regex.Replace(s, "^(whois|rwhois|https?)://", "", RegexOptions.IgnoreCase);
                if (s.Length > 3 && !s.Equals(current, StringComparison.OrdinalIgnoreCase) && !s.Contains(' ')) return s;
            }
        }
        return null;
    }

    public static async Task<string> RawAsync(string server, string query, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(15000);
        await client.ConnectAsync(server, 43, cts.Token);
        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(query + "\r\n");
        await stream.WriteAsync(bytes, cts.Token);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cts.Token);
        var data = ms.ToArray();
        // UTF-8 versuchen, sonst Latin-1
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch { return Encoding.Latin1.GetString(data); }
    }

    /// <summary>Wichtige Felder aus der letzten Antwort extrahieren.</summary>
    public static List<(string Key, string Value)> Summarize(IEnumerable<Hop> hops)
    {
        var all = string.Join("\n", hops.Select(h => h.Response));
        var res = new List<(string, string)>();

        void Add(string label, params string[] keys)
        {
            foreach (var k in keys)
            {
                var ms = Regex.Matches(all, @"^\s*" + k + @"\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                var vals = ms.Select(m => m.Groups[1].Value.Trim()).Where(v => v.Length > 0 && !v.StartsWith("REDACTED", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (vals.Count > 0)
                {
                    res.Add((label, string.Join(", ", vals.Take(label == "Nameserver" ? 8 : 3))));
                    return;
                }
            }
        }

        Add("Domain / Netz", "Domain Name", "domain", "inetnum", "NetRange", "inet6num", "CIDR");
        Add("Status", "Domain Status", "Status", "status");
        Add("Registrar", "Registrar", "registrar", "Sponsoring Registrar");
        Add("Inhaber / Organisation", "Registrant Organization", "Registrant Name", "org-name", "OrgName", "Organization", "descr", "owner");
        Add("Land", "Registrant Country", "country", "Country");
        Add("Erstellt", "Creation Date", "created", "Created On", "RegDate", "Registered on");
        Add("Geändert", "Updated Date", "changed", "last-modified", "Last Modified", "Updated");
        Add("Läuft ab", "Registry Expiry Date", "Registrar Registration Expiration Date", "Expiration Date", "paid-till", "Expiry date");
        Add("Nameserver", "Name Server", "nserver", "Nserver");
        Add("DNSSEC", "DNSSEC", "dnssec");
        Add("ASN", "origin", "OriginAS", "aut-num");
        Add("Abuse-Kontakt", "Registrar Abuse Contact Email", "abuse-mailbox", "OrgAbuseEmail");
        return res;
    }
}
