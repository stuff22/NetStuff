using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== DNS-Abfrage */

public class DnsLookupResult
{
    public List<DnsRow> Rows { get; init; } = new();
    public string Server { get; init; }
    public long Ms { get; init; }
    public bool Authenticated { get; init; }
    public List<string> Notes { get; init; } = new();

    public string Summary => L.F("{0} Einträge · Server: {1} · {2} ms · {3}", Rows.Count, Server, Ms,
                                 Authenticated ? L.T("DNSSEC: validiert (AD-Flag) ✓") : L.T("DNSSEC: nicht validiert"))
                             + (Notes.Count > 0 ? "\n" + string.Join("  ·  ", Notes) : "");

    /// <summary>Zonendatei-ähnlicher Text.</summary>
    public string ToText() => string.Join("\n", Rows.Select(r => $"{r.Name}\t{r.Ttl}\tIN\t{r.Type}\t{r.Value}"));
}

public static class DnsLookup
{
    /// <summary>Standardauswahl der Eintragstypen in der Oberfläche.</summary>
    public static bool DefaultSelected(QueryType t) => t is QueryType.A or QueryType.AAAA or QueryType.MX or QueryType.NS or QueryType.TXT or QueryType.SOA or QueryType.CNAME or QueryType.CAA;

    /// <summary>
    /// Fragt Einträge ab. <paramref name="server"/>: „System…“ (System-Resolver), „Autoritativ…“ (zuständiger Nameserver)
    /// oder eine IP/ein Hostname (optional gefolgt von einer Beschreibung). Bei einer IP als Name wird PTR abgefragt.
    /// </summary>
    public static async Task<DnsLookupResult> QueryAsync(string name, string server, bool tcp, IEnumerable<QueryType> types, CancellationToken ct = default)
    {
        name = NetUtil.CleanHost(name);
        var sw = Stopwatch.StartNew();
        IPAddress serverIp = null;
        string serverLabel = DnsHelper.UsesPublicFallback
            ? L.T("System-Resolver nicht lesbar – öffentliche Resolver 1.1.1.1 / 8.8.8.8 / 9.9.9.9")
            : L.T("System-Resolver");
        var sel = (server ?? "").Trim();
        if (sel.StartsWith("Autoritativ") || sel.StartsWith("Authoritative"))
        {
            var (ip, ns) = await DnsHelper.AuthoritativeAsync(name, ct);
            serverIp = ip;
            serverLabel = L.F("{0} ({1}) – autoritativ", ns, ip);
        }
        else if (sel != "" && !sel.StartsWith("System"))
        {
            var first = sel.Split(' ')[0];
            serverIp = IPAddress.TryParse(first, out var ip) ? ip : await NetUtil.ResolveAsync(first, false, ct);
            serverLabel = serverIp.ToString();
        }

        var client = DnsHelper.Create(serverIp, tcp);
        var rows = new List<DnsRow>();
        var notes = new List<string>();
        bool anyAd = false;

        if (IPAddress.TryParse(name, out var addr))
        {
            var r = await client.QueryReverseAsync(addr, ct);
            rows.AddRange(DnsHelper.Rows(r.Answers));
            if (r.Answers.Count == 0) notes.Add(L.T("Kein PTR-Eintrag"));
        }
        else
        {
            var list = types.Where(t => t != QueryType.PTR).ToList();
            // Einzelne Typen dürfen scheitern (Timeout, gefilterte Typen) – die übrigen Ergebnisse bleiben erhalten.
            var tasks = list.Select(async t =>
            {
                try { return (Type: t, Res: await client.QueryAsync(name, t, cancellationToken: ct), Error: (string)null); }
                catch (DnsResponseException ex) when (!ct.IsCancellationRequested) { return (Type: t, Res: (IDnsQueryResponse)null, Error: ex.Message); }
            }).ToList();
            var results = await Task.WhenAll(tasks);
            if (results.All(x => x.Error != null)) throw new DnsResponseException(results[0].Error);
            foreach (var (t, r, err) in results)
            {
                if (err != null)
                {
                    notes.Add(L.F("{0}: keine Antwort (Timeout)", t));
                    continue;
                }
                if (r.HasError && r.Header.ResponseCode != DnsHeaderResponseCode.NoError)
                {
                    notes.Add($"{t}: {r.ErrorMessage}");
                    continue;
                }
                anyAd |= r.Header.IsAuthenticData;
                rows.AddRange(DnsHelper.Rows(r.Answers)); // CNAME-Ketten werden unten dedupliziert
            }
            rows = rows.GroupBy(x => (x.Type, x.Name, x.Value)).Select(g => g.First())
                .OrderBy(x => Array.IndexOf(DnsHelper.CommonTypes, Enum.TryParse<QueryType>(x.Type, out var qt) ? qt : QueryType.ANY))
                .ThenBy(x => x.Value).ToList();
        }
        return new DnsLookupResult { Rows = rows, Server = serverLabel, Ms = sw.ElapsedMilliseconds, Authenticated = anyAd, Notes = notes };
    }
}

/* ====================================================================== Mail-Check */

public class MailCheckOptions
{
    public IEnumerable<string> DkimSelectors { get; set; } = Array.Empty<string>();
    public bool SmtpTest { get; set; } = true;
}

/// <summary>MX, SPF, DMARC, DKIM, MTA-STS/TLS-RPT/BIMI und optional SMTP mit STARTTLS.</summary>
public static class MailChecker
{
    /// <summary>Domain aus Eingabe (auch E-Mail-Adresse) ermitteln.</summary>
    public static string ParseDomain(string input)
    {
        var domain = NetUtil.CleanHost(input ?? "").ToLowerInvariant();
        if (domain.Contains('@')) domain = domain[(domain.IndexOf('@') + 1)..];
        return domain;
    }

    public static async Task CheckAsync(string domain, MailCheckOptions o, ICheckSink sink, CancellationToken ct = default)
    {
        var client = DnsHelper.Create();
        sink.Status("Mailserver werden geprüft …");

        // MX
        var mxItems = new List<CheckItem>();
        var mxRes = await client.QueryAsync(domain, QueryType.MX, cancellationToken: ct);
        var mxs = mxRes.Answers.MxRecords().OrderBy(m => m.Preference).ToList();
        if (mxs.Count == 0)
        {
            var a = await client.QueryAsync(domain, QueryType.A, cancellationToken: ct);
            mxItems.Add(new CheckItem("MX-Einträge", a.Answers.ARecords().Any()
                ? "Keine MX-Einträge – Mails werden an den A-Record zugestellt (Fallback)."
                : "Keine MX-Einträge – Domain kann keine E-Mails empfangen.", Level.Warn));
        }
        foreach (var mx in mxs)
        {
            var host = mx.Exchange.Value.TrimEnd('.');
            if (host == "") { mxItems.Add(new CheckItem("Null-MX", "Domain empfängt bewusst keine E-Mails (RFC 7505).", Level.Info)); continue; }
            string ips = "", ptr = "";
            bool resolvable = true;
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                ips = string.Join(", ", addrs.Select(x => x.ToString()));
                var v4 = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) ptr = await NetUtil.ReverseDnsAsync(v4);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { ips = L.T("nicht auflösbar"); resolvable = false; }
            mxItems.Add(new CheckItem($"MX {mx.Preference}", $"{host}  →  {ips}" + (ptr != "" ? $"  (PTR: {ptr})" : L.T("  (kein PTR)")),
                !resolvable ? Level.Bad : ptr == "" ? Level.Warn : Level.Ok));
        }
        sink.Add(new CheckSection("MX – Mailserver", mxItems));

        // SPF
        var txt = await DnsHelper.TxtAsync(client, domain, ct);
        var spf = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
        sink.Add(new CheckSection("SPF", SpfItems(spf)));

        // DMARC
        var dmarc = (await DnsHelper.TxtAsync(client, "_dmarc." + domain, ct)).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        sink.Add(new CheckSection("DMARC", DmarcItems(dmarc, domain)));

        // DKIM
        var dkimItems = new List<CheckItem>();
        var selectors = o.DkimSelectors.Select(s => s.Trim()).Where(s => s != "").Distinct().ToList();
        var dkimTasks = selectors.Select(async s =>
        {
            try
            {
                var r = await DnsHelper.TxtAsync(client, $"{s}._domainkey.{domain}", ct);
                return (s, r.FirstOrDefault(t => t.Contains("p=")));
            }
            catch { return (s, (string)null); }
        });
        foreach (var (sel, rec) in await Task.WhenAll(dkimTasks))
        {
            if (rec != null) dkimItems.Add(DkimItem(sel, rec));
        }
        if (dkimItems.Count == 0)
            dkimItems.Add(new CheckItem("DKIM", "Keiner der getesteten Selektoren gefunden. Den Selektor findest du im Header einer gesendeten Mail (DKIM-Signature: s=…).", Level.Warn));
        sink.Add(new CheckSection("DKIM", dkimItems));

        // Weitere
        var more = new List<CheckItem>();
        var sts = (await DnsHelper.TxtAsync(client, "_mta-sts." + domain, ct)).FirstOrDefault(t => t.StartsWith("v=STSv1"));
        if (sts != null)
        {
            string mode;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(6000);
                var policy = await NetUtil.Http.GetStringAsync($"https://mta-sts.{domain}/.well-known/mta-sts.txt", cts.Token);
                mode = Regex.Match(policy, @"mode:\s*(\w+)").Groups[1].Value;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { mode = L.T("Richtliniendatei nicht abrufbar"); }
            more.Add(new CheckItem("MTA-STS", $"{sts}  ·  {L.T("Modus")}: {mode}", mode == "enforce" ? Level.Ok : Level.Warn));
        }
        else more.Add(new CheckItem("MTA-STS", "nicht konfiguriert (optional, schützt vor TLS-Downgrade)", Level.Info));

        var tlsrpt = (await DnsHelper.TxtAsync(client, "_smtp._tls." + domain, ct)).FirstOrDefault(t => t.StartsWith("v=TLSRPTv1"));
        more.Add(new CheckItem("TLS-RPT", tlsrpt ?? "nicht konfiguriert (optional)", tlsrpt != null ? Level.Ok : Level.Info));
        var bimi = (await DnsHelper.TxtAsync(client, "default._bimi." + domain, ct)).FirstOrDefault(t => t.StartsWith("v=BIMI1"));
        more.Add(new CheckItem("BIMI", bimi ?? "nicht konfiguriert (optional, Markenlogo im Postfach)", bimi != null ? Level.Ok : Level.Info));
        sink.Add(new CheckSection("Weitere Einträge", more));

        // SMTP
        if (o.SmtpTest && mxs.Count > 0 && mxs[0].Exchange.Value.TrimEnd('.') != "")
        {
            var mxHost = mxs[0].Exchange.Value.TrimEnd('.');
            sink.Status("SMTP-Test läuft …");
            var smtp = await SmtpTestAsync(mxHost, ct);
            sink.Add(new CheckSection("SMTP – " + mxHost, smtp));
        }
    }

    public static List<CheckItem> SpfItems(List<string> spf)
    {
        var items = new List<CheckItem>();
        if (spf.Count == 0) { items.Add(new CheckItem("SPF", "Kein SPF-Eintrag – Absender können gefälscht werden.", Level.Bad)); return items; }
        if (spf.Count > 1) items.Add(new CheckItem("Mehrere SPF-Einträge", "Ungültig! Es darf nur einen v=spf1-Eintrag geben.", Level.Bad));
        var rec = spf[0];
        items.Add(new CheckItem("Eintrag", rec, Level.Ok));
        int lookups = Regex.Matches(rec, @"(?:^|\s)[+\-~?]?(include:|a(?=[\s:/]|$)|mx(?=[\s:/]|$)|ptr|exists:|redirect=)", RegexOptions.IgnoreCase).Count;
        items.Add(new CheckItem("DNS-Lookups (oberste Ebene)", L.F("{0} von max. 10", lookups) + (lookups > 10 ? L.T(" – Limit überschritten!") : ""), lookups > 10 ? Level.Bad : lookups > 8 ? Level.Warn : Level.Ok));
        var all = Regex.Match(rec, @"([+\-~?]?)all\b", RegexOptions.IgnoreCase);
        if (!all.Success && !rec.Contains("redirect=")) items.Add(new CheckItem("Abschluss", "Kein „all“-Mechanismus", Level.Warn));
        else if (all.Success)
        {
            items.Add(all.Groups[1].Value switch
            {
                "-" => new CheckItem("Abschluss", "-all (hart ablehnen) – empfohlen", Level.Ok),
                "~" => new CheckItem("Abschluss", "~all (Softfail) – üblich, zusammen mit DMARC in Ordnung", Level.Ok),
                "?" => new CheckItem("Abschluss", "?all (neutral) – bietet keinen Schutz", Level.Warn),
                _ => new CheckItem("Abschluss", "+all – erlaubt JEDEM Server das Senden!", Level.Bad),
            });
        }
        if (rec.Contains("ptr", StringComparison.OrdinalIgnoreCase)) items.Add(new CheckItem("ptr-Mechanismus", "veraltet, sollte nicht verwendet werden", Level.Warn));
        return items;
    }

    public static List<CheckItem> DmarcItems(string dmarc, string domain)
    {
        var items = new List<CheckItem>();
        if (dmarc == null) { items.Add(new CheckItem("DMARC", L.T("Kein DMARC-Eintrag unter _dmarc.") + domain, Level.Bad)); return items; }
        items.Add(new CheckItem("Eintrag", dmarc, Level.Ok));
        var tags = dmarc.Split(';').Select(p => p.Trim().Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
        items.Add(tags.GetValueOrDefault("p", "") switch
        {
            "reject" => new CheckItem("Richtlinie (p)", "reject – gefälschte Mails werden abgelehnt", Level.Ok),
            "quarantine" => new CheckItem("Richtlinie (p)", "quarantine – gefälschte Mails landen im Spam", Level.Ok),
            "none" => new CheckItem("Richtlinie (p)", "none – nur Monitoring, kein Schutz", Level.Warn),
            _ => new CheckItem("Richtlinie (p)", "fehlt/ungültig", Level.Bad),
        });
        if (tags.TryGetValue("sp", out var sp)) items.Add(new CheckItem("Subdomains (sp)", sp));
        if (tags.TryGetValue("pct", out var pct) && pct != "100") items.Add(new CheckItem("Anteil (pct)", pct + " %", Level.Warn));
        items.Add(tags.TryGetValue("rua", out var rua)
            ? new CheckItem("Berichte (rua)", rua, Level.Ok)
            : new CheckItem("Berichte (rua)", "keine Aggregat-Berichte konfiguriert", Level.Warn));
        return items;
    }

    public static CheckItem DkimItem(string selector, string rec)
    {
        var pm = Regex.Match(rec, @"p=([A-Za-z0-9+/=]*)");
        var key = pm.Success ? pm.Groups[1].Value : "";
        int bytes = key.Length * 3 / 4;
        string bits = key == "" ? L.T("widerrufen (leerer Schlüssel)") : bytes > 500 ? "~4096 Bit" : bytes > 250 ? "~2048 Bit" : bytes > 130 ? "~1024 Bit" : L.T("kurz");
        var algo = Regex.Match(rec, @"k=(\w+)").Groups[1].Value;
        return new CheckItem(L.F("Selektor „{0}“", selector), L.F("{0}, Schlüssel {1}", algo == "" ? "rsa" : algo, bits),
            key == "" ? Level.Warn : bytes > 130 && bytes <= 250 && algo != "ed25519" ? Level.Warn : Level.Ok);
    }

    public static async Task<List<CheckItem>> SmtpTestAsync(string host, CancellationToken ct = default)
    {
        var items = new List<CheckItem>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(20000);
            using var client = new TcpClient();
            var sw = Stopwatch.StartNew();
            try { await client.ConnectAsync(host, 25, cts.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch
            {
                items.Add(new CheckItem("Verbindung Port 25", "keine Verbindung – viele Internetanbieter sperren ausgehenden Port 25.", Level.Warn));
                return items;
            }
            items.Add(new CheckItem("Verbindung Port 25", L.F("erfolgreich ({0} ms)", sw.ElapsedMilliseconds), Level.Ok));

            Stream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            async Task<List<string>> ReadReply(StreamReader r)
            {
                var lines = new List<string>();
                while (true)
                {
                    var line = await r.ReadLineAsync(cts.Token);
                    if (line == null) break;
                    lines.Add(line);
                    if (line.Length < 4 || line[3] != '-') break;
                }
                return lines;
            }

            var banner = await ReadReply(reader);
            items.Add(new CheckItem("Banner", string.Join(" ", banner), banner.FirstOrDefault()?.StartsWith("220") == true ? Level.Ok : Level.Warn));
            await writer.WriteLineAsync("EHLO net-stuff.local");
            var ehlo = await ReadReply(reader);
            var caps = ehlo.Skip(1).Select(l => l.Length > 4 ? l[4..] : l).ToList();
            items.Add(new CheckItem("EHLO-Fähigkeiten", string.Join(", ", caps)));
            var size = caps.FirstOrDefault(c => c.StartsWith("SIZE", StringComparison.OrdinalIgnoreCase));
            if (size != null && long.TryParse(size[4..].Trim(), out var sz) && sz > 0)
                items.Add(new CheckItem("Max. Mailgröße", NetUtil.FormatBytes(sz)));

            if (caps.Any(c => c.Equals("STARTTLS", StringComparison.OrdinalIgnoreCase)))
            {
                await writer.WriteLineAsync("STARTTLS");
                var st = await ReadReply(reader);
                if (st.FirstOrDefault()?.StartsWith("220") == true)
                {
                    X509Certificate2 cert = null;
                    SslPolicyErrors errors = SslPolicyErrors.None;
                    using var ssl = new SslStream(stream, false, (_, c, _, e) => { cert = c != null ? new X509Certificate2(c) : null; errors = e; return true; });
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                    items.Add(new CheckItem("STARTTLS", $"{L.T("unterstützt")} · {TlsInfo.ProtoName(ssl.SslProtocol)} · {ssl.NegotiatedCipherSuite}", Level.Ok));
                    if (cert != null)
                    {
                        var days = (cert.NotAfter - DateTime.Now).TotalDays;
                        items.Add(new CheckItem("Zertifikat", L.F("{0} · Aussteller {1} · gültig bis {2:dd.MM.yyyy}", cert.GetNameInfo(X509NameType.SimpleName, false), cert.GetNameInfo(X509NameType.SimpleName, true), cert.NotAfter),
                            errors == SslPolicyErrors.None && days > 14 ? Level.Ok : Level.Warn));
                        if (errors != SslPolicyErrors.None) items.Add(new CheckItem("Zertifikatsprüfung", errors.ToString(), Level.Warn));
                    }
                    var sw2 = new StreamWriter(ssl, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    try { await sw2.WriteLineAsync("QUIT"); } catch { }
                }
                else items.Add(new CheckItem("STARTTLS", L.T("angekündigt, aber abgelehnt: ") + string.Join(" ", st), Level.Bad));
            }
            else
            {
                items.Add(new CheckItem("STARTTLS", "nicht unterstützt – Mails werden unverschlüsselt übertragen!", Level.Bad));
                try { await writer.WriteLineAsync("QUIT"); } catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            items.Add(new CheckItem("SMTP-Test", L.T("Fehler: ") + ex.Message, Level.Warn));
        }
        return items;
    }
}

/* ====================================================================== Domain-Check */

/// <summary>
/// Gesamtprüfung einer Domain: Delegation, Nameserver, SOA, DNSSEC, Web, E-Mail-Basis, CAA, Propagation, Blacklists.
/// Liefert am Ende eine Zusammenfassung (Gesamtbewertung) zurück.
/// </summary>
public static class DomainChecker
{
    public static readonly string[] Dnsbls =
    {
        "zen.spamhaus.org", "bl.spamcop.net", "b.barracudacentral.org", "psbl.surriel.com",
        "dnsbl-1.uceprotect.net", "bl.mailspike.net", "all.s5h.net", "dnsbl.dronebl.org",
    };

    /// <summary>Domain aus Eingabe ermitteln (ohne www., Punkt am Ende). null, wenn ungültig.</summary>
    public static string ParseDomain(string input)
    {
        var domain = NetUtil.CleanHost(input ?? "").ToLowerInvariant().TrimEnd('.');
        if (domain.StartsWith("www.")) domain = domain[4..];
        return domain == "" || !domain.Contains('.') ? null : domain;
    }

    public static async Task<CheckSection> CheckAsync(string domain, bool blacklists, ICheckSink sink, CancellationToken ct = default)
    {
        var all = new List<CheckItem>();
        void Add(string title, List<CheckItem> items)
        {
            if (items.Count == 0) return;
            sink.Add(new CheckSection(title, items));
            all.AddRange(items);
        }
        sink.Status("Prüfung läuft …");

        var sys = DnsHelper.Create(timeoutMs: 4000);
        var cf = DnsHelper.Create(IPAddress.Parse("1.1.1.1"), timeoutMs: 4000);

        // ---------------------------------------------------------- Delegation
        var deleg = new List<CheckItem>();
        var zoneNs = (await sys.QueryAsync(domain, QueryType.NS, cancellationToken: ct)).Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.').ToLowerInvariant()).OrderBy(x => x).ToList();
        var parent = domain[(domain.IndexOf('.') + 1)..];
        var parentNs = new List<string>();
        try
        {
            var pns = (await sys.QueryAsync(parent, QueryType.NS, cancellationToken: ct)).Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.')).FirstOrDefault();
            if (pns != null)
            {
                var pip = (await Dns.GetHostAddressesAsync(pns, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                var pr = await DnsHelper.Create(pip, timeoutMs: 4000, dnssec: false).QueryAsync(domain, QueryType.NS, cancellationToken: ct);
                parentNs = pr.Authorities.NsRecords().Concat(pr.Answers.NsRecords()).Select(n => n.NSDName.Value.TrimEnd('.').ToLowerInvariant()).Distinct().OrderBy(x => x).ToList();
                deleg.Add(new CheckItem("Delegation (Registry)", L.F("{0}  (laut {1})", string.Join(", ", parentNs), pns), parentNs.Count > 0 ? Level.Ok : Level.Bad));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { deleg.Add(new CheckItem("Delegation (Registry)", L.T("nicht ermittelbar: ") + ex.Message, Level.Warn)); }

        deleg.Add(new CheckItem("NS-Einträge der Zone", zoneNs.Count > 0 ? string.Join(", ", zoneNs) : L.T("keine – Domain existiert nicht oder ist nicht delegiert"),
            zoneNs.Count >= 2 ? Level.Ok : zoneNs.Count == 1 ? Level.Warn : Level.Bad));
        if (zoneNs.Count == 1) deleg.Add(new CheckItem("Redundanz", "Nur ein Nameserver – mindestens zwei werden empfohlen.", Level.Warn));
        if (parentNs.Count > 0 && zoneNs.Count > 0)
            deleg.Add(parentNs.SequenceEqual(zoneNs)
                ? new CheckItem("Delegation = Zone", "identisch ✓", Level.Ok)
                : new CheckItem("Delegation = Zone", L.F("abweichend! Registry: {0} · Zone: {1}", string.Join(", ", parentNs), string.Join(", ", zoneNs)), Level.Warn));
        Add("Delegation", deleg);
        if (zoneNs.Count == 0) zoneNs = parentNs;

        // ---------------------------------------------------------- Nameserver
        var nsItems = new List<CheckItem>();
        var serials = new List<uint>();
        var nets = new HashSet<string>();
        SoaRecord soa = null;
        foreach (var ns in zoneNs)
        {
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(ns, ct);
                var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                bool v6 = addrs.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                if (v4 == null) { nsItems.Add(new CheckItem(ns, "keine IPv4-Adresse", Level.Bad)); continue; }
                nets.Add(string.Join(".", v4.GetAddressBytes().Take(3)));
                var sw = Stopwatch.StartNew();
                var r = await DnsHelper.Create(v4, timeoutMs: 4000, dnssec: false).QueryAsync(domain, QueryType.SOA, cancellationToken: ct);
                var s = r.Answers.SoaRecords().FirstOrDefault();
                bool aa = r.Header.HasAuthorityAnswer;
                if (s != null) { serials.Add(s.Serial); soa ??= s; }
                nsItems.Add(new CheckItem(ns, L.F("{0}{1} · {2} ms · {3} · Serial {4}", v4, v6 ? " + IPv6" : "", sw.ElapsedMilliseconds,
                    aa ? L.T("autoritativ ✓") : L.T("NICHT autoritativ (lame delegation)"), s?.Serial.ToString() ?? "–"),
                    s != null && aa ? Level.Ok : Level.Bad));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { nsItems.Add(new CheckItem(ns, L.T("antwortet nicht: ") + ex.Message, Level.Bad)); }
        }
        if (serials.Count > 1)
            nsItems.Add(serials.Distinct().Count() == 1
                ? new CheckItem("SOA-Serial", L.F("auf allen Nameservern gleich ({0}) ✓", serials[0]), Level.Ok)
                : new CheckItem("SOA-Serial", L.F("unterschiedlich: {0} – Zonen nicht synchron!", string.Join(", ", serials.Distinct())), Level.Bad));
        if (zoneNs.Count >= 2)
            nsItems.Add(nets.Count >= 2
                ? new CheckItem("Netz-Redundanz", L.F("Nameserver in {0} verschiedenen /24-Netzen ✓", nets.Count), Level.Ok)
                : new CheckItem("Netz-Redundanz", "Alle Nameserver im selben /24-Netz – Single Point of Failure.", Level.Warn));
        Add("Nameserver", nsItems);

        // ---------------------------------------------------------- SOA
        if (soa != null)
        {
            Add("SOA", new List<CheckItem>
            {
                new("Primärer Nameserver", soa.MName.Value.TrimEnd('.')),
                new("Verantwortlich", soa.RName.Value.TrimEnd('.')),
                new("Refresh", $"{soa.Refresh} s", soa.Refresh is >= 1200 and <= 86400 ? Level.Ok : Level.Warn),
                new("Retry", $"{soa.Retry} s", soa.Retry < soa.Refresh ? Level.Ok : Level.Warn),
                new("Expire", $"{soa.Expire} s", soa.Expire >= 604800 ? Level.Ok : Level.Warn),
                new("Minimum (negativer Cache)", $"{soa.Minimum} s", soa.Minimum is >= 60 and <= 86400 ? Level.Ok : Level.Warn),
            });
        }

        // ---------------------------------------------------------- DNSSEC
        var sec = new List<CheckItem>();
        var ds = (await cf.QueryAsync(domain, QueryType.DS, cancellationToken: ct)).Answers.Where(a => a.RecordType == ResourceRecordType.DS).ToList();
        var keys = (await cf.QueryAsync(domain, QueryType.DNSKEY, cancellationToken: ct)).Answers.Where(a => a.RecordType == ResourceRecordType.DNSKEY).ToList();
        var adResp = await cf.QueryAsync(domain, QueryType.SOA, cancellationToken: ct);
        bool ad = adResp.Header.IsAuthenticData;
        sec.Add(new CheckItem("DS-Eintrag bei der Registry", ds.Count > 0 ? L.F("{0} vorhanden", ds.Count) : L.T("nicht vorhanden"), ds.Count > 0 ? Level.Ok : Level.Info));
        sec.Add(new CheckItem("DNSKEY in der Zone", keys.Count > 0 ? L.F("{0} Schlüssel", keys.Count) : L.T("keine"), keys.Count > 0 ? Level.Ok : Level.Info));
        if (ds.Count > 0)
            sec.Add(ad && adResp.Header.ResponseCode == DnsHeaderResponseCode.NoError
                ? new CheckItem("Validierung", "Kette gültig – Antworten werden validiert (AD) ✓", Level.Ok)
                : new CheckItem("Validierung", "FEHLER – DS vorhanden, aber Validierung schlägt fehl. Domain ist für validierende Resolver nicht erreichbar!", Level.Bad));
        else if (keys.Count > 0)
            sec.Add(new CheckItem("Validierung", "Zone signiert, aber kein DS bei der Registry – DNSSEC nicht aktiv.", Level.Warn));
        else
            sec.Add(new CheckItem("Validierung", "Domain ist nicht mit DNSSEC signiert (optional, empfohlen).", Level.Info));
        Add("DNSSEC", sec);

        // ---------------------------------------------------------- Web
        var web = new List<CheckItem>();
        async Task<List<string>> Q(string name, QueryType t)
        {
            // Einzelne Zeitüberschreitungen (z. B. beim Resolver des Providers) dürfen nicht die ganze Prüfung abbrechen
            try { return (await sys.QueryAsync(name, t, cancellationToken: ct)).Answers.Where(a => a.RecordType.ToString() == t.ToString()).Select(DnsHelper.ValueOf).ToList(); }
            catch (DnsResponseException) { return new List<string>(); }
        }
        var a4 = await Q(domain, QueryType.A);
        var a6 = await Q(domain, QueryType.AAAA);
        var w4 = await Q("www." + domain, QueryType.A);
        var w6 = await Q("www." + domain, QueryType.AAAA);
        web.Add(new CheckItem(domain, a4.Count + a6.Count > 0 ? string.Join(", ", a4.Concat(a6)) : L.T("keine A/AAAA-Einträge"), a4.Count > 0 ? Level.Ok : Level.Warn));
        web.Add(new CheckItem("www." + domain, w4.Count + w6.Count > 0 ? string.Join(", ", w4.Concat(w6)) : L.T("keine Einträge"), w4.Count > 0 ? Level.Ok : Level.Warn));
        web.Add(new CheckItem("IPv6-bereit", a6.Count > 0 || w6.Count > 0 ? L.T("ja (AAAA vorhanden)") : L.T("nein – keine AAAA-Einträge"), a6.Count > 0 || w6.Count > 0 ? Level.Ok : Level.Warn));
        var ttl = await Try(async () => (await sys.QueryAsync(domain, QueryType.A, cancellationToken: ct)).Answers.ARecords().Select(r => r.TimeToLive).DefaultIfEmpty(0).Min(), 0);
        if (ttl > 0) web.Add(new CheckItem("TTL A-Record", L.F("{0} s", ttl), ttl is >= 60 and <= 86400 ? Level.Info : Level.Warn));
        var wild = await Q($"nx-{Guid.NewGuid():N}".Substring(0, 20) + "." + domain, QueryType.A);
        web.Add(new CheckItem("Wildcard-DNS", wild.Count > 0 ? L.F("aktiv (*.{0} → {1})", domain, wild[0]) : L.T("nicht aktiv"), Level.Info));
        Add("Web", web);

        // ---------------------------------------------------------- Mail kurz
        var mail = new List<CheckItem>();
        var mx = await Try(async () => (await sys.QueryAsync(domain, QueryType.MX, cancellationToken: ct)).Answers.MxRecords().OrderBy(m => m.Preference).ToList(), new List<MxRecord>());
        mail.Add(new CheckItem("MX", mx.Count > 0 ? string.Join(", ", mx.Select(m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}")) : L.T("keine"), mx.Count > 0 ? Level.Ok : Level.Warn));
        var txt = await Try(() => DnsHelper.TxtAsync(sys, domain, ct), new List<string>());
        var spf = txt.FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
        mail.Add(new CheckItem("SPF", spf ?? L.T("fehlt"), spf != null ? Level.Ok : Level.Bad));
        var dmarc = (await Try(() => DnsHelper.TxtAsync(sys, "_dmarc." + domain, ct), new List<string>())).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
        mail.Add(new CheckItem("DMARC", dmarc ?? L.T("fehlt"), dmarc != null ? Level.Ok : Level.Bad));
        mail.Add(new CheckItem("", L.T("Details (DKIM, MTA-STS, SMTP/STARTTLS) im Werkzeug „Mail-Check“."), Level.Info));
        Add("E-Mail", mail);

        // ---------------------------------------------------------- CAA
        var caa = await Q(domain, QueryType.CAA);
        Add("CAA (erlaubte Zertifizierungsstellen)", new List<CheckItem>
        {
            new("CAA", caa.Count > 0 ? string.Join("\n", caa) : L.T("keine – jede CA darf Zertifikate ausstellen (optional)"), caa.Count > 0 ? Level.Ok : Level.Info),
        });

        // ---------------------------------------------------------- Propagation
        var prop = new List<CheckItem>();
        var resolvers = new (string Name, string Ip)[] { ("Cloudflare", "1.1.1.1"), ("Google", "8.8.8.8"), ("Quad9", "9.9.9.9"), ("OpenDNS", "208.67.222.222"), ("AdGuard", "94.140.14.14") };
        var answers = await Task.WhenAll(resolvers.Select(async r =>
        {
            try
            {
                var c = DnsHelper.Create(IPAddress.Parse(r.Ip), timeoutMs: 3000, dnssec: false);
                var res = await c.QueryAsync(domain, QueryType.A, cancellationToken: ct);
                return (r.Name, string.Join(", ", res.Answers.ARecords().Select(x => x.Address.ToString()).OrderBy(x => x)));
            }
            catch { return (r.Name, (string)null); }
        }));
        var majority = answers.Where(a => !string.IsNullOrEmpty(a.Item2)).GroupBy(a => a.Item2).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        foreach (var (name, ans) in answers)
            prop.Add(new CheckItem(name, ans ?? L.T("keine Antwort"), ans == null ? Level.Warn : ans == majority ? Level.Ok : Level.Info));
        prop.Add(new CheckItem("", L.T("Unterschiede sind bei CDN/GeoDNS normal; direkt nach Änderungen zeigen sie, dass alte Einträge noch im Cache sind (TTL)."), Level.Info));
        Add("Propagation (A-Record weltweit)", prop);

        // ---------------------------------------------------------- Blacklists
        if (blacklists)
        {
            sink.Status("Blacklists werden geprüft …");
            var ips = new HashSet<string>(a4);
            foreach (var m in mx.Take(3))
            {
                try { foreach (var ip in (await Dns.GetHostAddressesAsync(m.Exchange.Value.TrimEnd('.'), ct)).Where(x => x.AddressFamily == AddressFamily.InterNetwork)) ips.Add(ip.ToString()); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            var bl = new List<CheckItem>();
            foreach (var ip in ips.Take(6)) bl.Add(await BlacklistItemAsync(sys, ip, ct));
            Add("Blacklists (Web- und Mailserver-IPs)", bl);
        }

        // ---------------------------------------------------------- Zusammenfassung
        int bad = all.Count(i => i.Level == Level.Bad), warn = all.Count(i => i.Level == Level.Warn), ok = all.Count(i => i.Level == Level.Ok);
        return new CheckSection(L.F("Ergebnis für {0}", domain), new[]
        {
            new CheckItem("Gesamtbewertung", bad > 0 ? L.F("{0} Fehler, {1} Warnungen", bad, warn) : warn > 0 ? L.F("Gut – {0} Hinweise/Warnungen", warn) : L.T("Sehr gut – keine Probleme gefunden"),
                bad > 0 ? Level.Bad : warn > 0 ? Level.Warn : Level.Ok),
            new CheckItem("Prüfpunkte", L.F("{0} OK · {1} Warnung · {2} Fehler", ok, warn, bad)),
        });
    }

    /// <summary>Optionale Abfrage: Zeitüberschreitung/DNS-Fehler liefern den Ersatzwert statt die Prüfung abzubrechen.</summary>
    private static async Task<T> Try<T>(Func<Task<T>> f, T fallback)
    {
        try { return await f(); }
        catch (DnsResponseException) { return fallback; }
    }

    /// <summary>Eine IPv4-Adresse gegen alle DNS-Blacklists prüfen.</summary>
    public static async Task<CheckItem> BlacklistItemAsync(LookupClient client, string ip, CancellationToken ct = default)
    {
        var rev = string.Join(".", ip.Split('.').Reverse());
        var hits = await Task.WhenAll(Dnsbls.Select(async z =>
        {
            try
            {
                var r = await client.QueryAsync($"{rev}.{z}", QueryType.A, cancellationToken: ct);
                var a = r.Answers.ARecords().FirstOrDefault()?.Address.ToString();
                if (a == null) return (z, 0);
                if (a.StartsWith("127.255.255.") || a == "127.0.0.1" && z.Contains("spamhaus")) return (z, -1); // Abfrage blockiert
                return (z, 1);
            }
            catch { return (z, 0); }
        }));
        var listed = hits.Where(h => h.Item2 == 1).Select(h => h.z).ToList();
        var blocked = hits.Where(h => h.Item2 == -1).Select(h => h.z).ToList();
        return new CheckItem(ip,
            (listed.Count > 0 ? L.F("GELISTET auf: {0}", string.Join(", ", listed)) : L.F("auf keiner von {0} Listen ✓", Dnsbls.Length - blocked.Count))
            + (blocked.Count > 0 ? L.F(" · nicht prüfbar über öffentlichen Resolver: {0}", string.Join(", ", blocked)) : ""),
            listed.Count > 0 ? Level.Bad : Level.Ok);
    }
}
