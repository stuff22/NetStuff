using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Input;
using DnsClient;
using DnsClient.Protocol;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DomainCheckPage : ToolPage
{
    private readonly StringBuilder _text = new();
    private readonly List<CheckItem> _all = new();

    private static readonly string[] Dnsbls =
    {
        "zen.spamhaus.org", "bl.spamcop.net", "b.barracudacentral.org", "psbl.surriel.com",
        "dnsbl-1.uceprotect.net", "bl.mailspike.net", "all.s5h.net", "dnsbl.dronebl.org",
    };

    public DomainCheckPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) DomainBox.Text = s;
    }

    private void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private void Add(string title, List<CheckItem> items)
    {
        if (items.Count == 0) return;
        ResultsPanel.Children.Add(UiFactory.Section(title, items));
        _text.Append(UiFactory.ToText(title, items));
        _all.AddRange(items);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var domain = NetUtil.CleanHost(DomainBox.Text).ToLowerInvariant().TrimEnd('.');
        if (domain.StartsWith("www.")) domain = domain[4..];
        if (domain == "" || !domain.Contains('.')) return;
        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        _text.Clear();
        _all.Clear();
        var progress = UiFactory.Message("Prüfung läuft …");
        ResultsPanel.Children.Add(progress);

        try
        {
            var sys = DnsHelper.Create(timeoutMs: 4000);
            var cf = DnsHelper.Create(IPAddress.Parse("1.1.1.1"), timeoutMs: 4000);

            // ---------------------------------------------------------- Delegation
            var deleg = new List<CheckItem>();
            var zoneNs = (await sys.QueryAsync(domain, QueryType.NS)).Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.').ToLowerInvariant()).OrderBy(x => x).ToList();
            var parent = domain[(domain.IndexOf('.') + 1)..];
            var parentNs = new List<string>();
            try
            {
                var pns = (await sys.QueryAsync(parent, QueryType.NS)).Answers.NsRecords().Select(n => n.NSDName.Value.TrimEnd('.')).FirstOrDefault();
                if (pns != null)
                {
                    var pip = (await Dns.GetHostAddressesAsync(pns)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
                    var pr = await DnsHelper.Create(pip, timeoutMs: 4000, dnssec: false).QueryAsync(domain, QueryType.NS);
                    parentNs = pr.Authorities.NsRecords().Concat(pr.Answers.NsRecords()).Select(n => n.NSDName.Value.TrimEnd('.').ToLowerInvariant()).Distinct().OrderBy(x => x).ToList();
                    deleg.Add(new CheckItem("Delegation (Registry)", L.F("{0}  (laut {1})", string.Join(", ", parentNs), pns), parentNs.Count > 0 ? Level.Ok : Level.Bad));
                }
            }
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
                    var addrs = await Dns.GetHostAddressesAsync(ns);
                    var v4 = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    bool v6 = addrs.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6);
                    if (v4 == null) { nsItems.Add(new CheckItem(ns, "keine IPv4-Adresse", Level.Bad)); continue; }
                    nets.Add(string.Join(".", v4.GetAddressBytes().Take(3)));
                    var sw = Stopwatch.StartNew();
                    var r = await DnsHelper.Create(v4, timeoutMs: 4000, dnssec: false).QueryAsync(domain, QueryType.SOA);
                    var s = r.Answers.SoaRecords().FirstOrDefault();
                    bool aa = r.Header.HasAuthorityAnswer;
                    if (s != null) { serials.Add(s.Serial); soa ??= s; }
                    nsItems.Add(new CheckItem(ns, L.F("{0}{1} · {2} ms · {3} · Serial {4}", v4, v6 ? " + IPv6" : "", sw.ElapsedMilliseconds,
                        aa ? L.T("autoritativ ✓") : L.T("NICHT autoritativ (lame delegation)"), s?.Serial.ToString() ?? "–"),
                        s != null && aa ? Level.Ok : Level.Bad));
                }
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
            var ds = (await cf.QueryAsync(domain, QueryType.DS)).Answers.Where(a => a.RecordType == ResourceRecordType.DS).ToList();
            var keys = (await cf.QueryAsync(domain, QueryType.DNSKEY)).Answers.Where(a => a.RecordType == ResourceRecordType.DNSKEY).ToList();
            var adResp = await cf.QueryAsync(domain, QueryType.SOA);
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
            async Task<List<string>> Q(string name, QueryType t) =>
                (await sys.QueryAsync(name, t)).Answers.Where(a => a.RecordType.ToString() == t.ToString()).Select(DnsHelper.ValueOf).ToList();
            var a4 = await Q(domain, QueryType.A);
            var a6 = await Q(domain, QueryType.AAAA);
            var w4 = await Q("www." + domain, QueryType.A);
            var w6 = await Q("www." + domain, QueryType.AAAA);
            web.Add(new CheckItem(domain, a4.Count + a6.Count > 0 ? string.Join(", ", a4.Concat(a6)) : L.T("keine A/AAAA-Einträge"), a4.Count > 0 ? Level.Ok : Level.Warn));
            web.Add(new CheckItem("www." + domain, w4.Count + w6.Count > 0 ? string.Join(", ", w4.Concat(w6)) : L.T("keine Einträge"), w4.Count > 0 ? Level.Ok : Level.Warn));
            web.Add(new CheckItem("IPv6-bereit", a6.Count > 0 || w6.Count > 0 ? L.T("ja (AAAA vorhanden)") : L.T("nein – keine AAAA-Einträge"), a6.Count > 0 || w6.Count > 0 ? Level.Ok : Level.Warn));
            var aRes = await sys.QueryAsync(domain, QueryType.A);
            var ttl = aRes.Answers.ARecords().Select(r => r.TimeToLive).DefaultIfEmpty(0).Min();
            if (ttl > 0) web.Add(new CheckItem("TTL A-Record", L.F("{0} s", ttl), ttl is >= 60 and <= 86400 ? Level.Info : Level.Warn));
            var wild = await Q($"nx-{Guid.NewGuid():N}".Substring(0, 20) + "." + domain, QueryType.A);
            web.Add(new CheckItem("Wildcard-DNS", wild.Count > 0 ? L.F("aktiv (*.{0} → {1})", domain, wild[0]) : L.T("nicht aktiv"), Level.Info));
            Add("Web", web);

            // ---------------------------------------------------------- Mail kurz
            var mail = new List<CheckItem>();
            var mx = (await sys.QueryAsync(domain, QueryType.MX)).Answers.MxRecords().OrderBy(m => m.Preference).ToList();
            mail.Add(new CheckItem("MX", mx.Count > 0 ? string.Join(", ", mx.Select(m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}")) : L.T("keine"), mx.Count > 0 ? Level.Ok : Level.Warn));
            var txt = await DnsHelper.TxtAsync(sys, domain);
            var spf = txt.FirstOrDefault(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase));
            mail.Add(new CheckItem("SPF", spf ?? L.T("fehlt"), spf != null ? Level.Ok : Level.Bad));
            var dmarc = (await DnsHelper.TxtAsync(sys, "_dmarc." + domain)).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
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
                    var res = await c.QueryAsync(domain, QueryType.A);
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
            if (BlacklistBox.IsChecked == true)
            {
                var ips = new HashSet<string>(a4);
                foreach (var m in mx.Take(3))
                {
                    try { foreach (var ip in (await Dns.GetHostAddressesAsync(m.Exchange.Value.TrimEnd('.'))).Where(x => x.AddressFamily == AddressFamily.InterNetwork)) ips.Add(ip.ToString()); }
                    catch { }
                }
                var bl = new List<CheckItem>();
                foreach (var ip in ips.Take(6))
                {
                    var rev = string.Join(".", ip.Split('.').Reverse());
                    var hits = await Task.WhenAll(Dnsbls.Select(async z =>
                    {
                        try
                        {
                            var r = await sys.QueryAsync($"{rev}.{z}", QueryType.A);
                            var a = r.Answers.ARecords().FirstOrDefault()?.Address.ToString();
                            if (a == null) return (z, 0);
                            if (a.StartsWith("127.255.255.") || a == "127.0.0.1" && z.Contains("spamhaus")) return (z, -1); // Abfrage blockiert
                            return (z, 1);
                        }
                        catch { return (z, 0); }
                    }));
                    var listed = hits.Where(h => h.Item2 == 1).Select(h => h.z).ToList();
                    var blocked = hits.Where(h => h.Item2 == -1).Select(h => h.z).ToList();
                    bl.Add(new CheckItem(ip,
                        listed.Count > 0 ? L.F("GELISTET auf: {0}", string.Join(", ", listed)) : L.F("auf keiner von {0} Listen ✓", Dnsbls.Length - blocked.Count)
                        + (blocked.Count > 0 ? L.F(" · nicht prüfbar über öffentlichen Resolver: {0}", string.Join(", ", blocked)) : ""),
                        listed.Count > 0 ? Level.Bad : Level.Ok));
                }
                Add("Blacklists (Web- und Mailserver-IPs)", bl);
            }

            // ---------------------------------------------------------- Zusammenfassung
            ResultsPanel.Children.Remove(progress);
            int bad = _all.Count(i => i.Level == Level.Bad), warn = _all.Count(i => i.Level == Level.Warn), ok = _all.Count(i => i.Level == Level.Ok);
            var summary = UiFactory.Section(L.F("Ergebnis für {0}", domain), new[]
            {
                new CheckItem("Gesamtbewertung", bad > 0 ? L.F("{0} Fehler, {1} Warnungen", bad, warn) : warn > 0 ? L.F("Gut – {0} Hinweise/Warnungen", warn) : L.T("Sehr gut – keine Probleme gefunden"),
                    bad > 0 ? Level.Bad : warn > 0 ? Level.Warn : Level.Ok),
                new CheckItem("Prüfpunkte", L.F("{0} OK · {1} Warnung · {2} Fehler", ok, warn, bad)),
            });
            ResultsPanel.Children.Insert(0, summary);
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Remove(progress);
            ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Bad));
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_text.ToString());
}
