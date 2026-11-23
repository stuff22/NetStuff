using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DnsClient;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class MailPage : ToolPage
{
    private readonly StringBuilder _text = new();

    public MailPage()
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

    private void AddSection(string title, List<CheckItem> items)
    {
        ResultsPanel.Children.Add(UiFactory.Section(title, items));
        _text.Append(UiFactory.ToText(title, items));
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var domain = NetUtil.CleanHost(DomainBox.Text).ToLowerInvariant();
        if (domain.Contains('@')) domain = domain[(domain.IndexOf('@') + 1)..];
        if (domain == "") return;
        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        _text.Clear();
        var client = DnsHelper.Create();

        try
        {
            // MX
            var mxItems = new List<CheckItem>();
            var mxRes = await client.QueryAsync(domain, QueryType.MX);
            var mxs = mxRes.Answers.MxRecords().OrderBy(m => m.Preference).ToList();
            if (mxs.Count == 0)
            {
                var a = await client.QueryAsync(domain, QueryType.A);
                mxItems.Add(new CheckItem("MX-Einträge", a.Answers.ARecords().Any()
                    ? "Keine MX-Einträge – Mails werden an den A-Record zugestellt (Fallback)."
                    : "Keine MX-Einträge – Domain kann keine E-Mails empfangen.", Level.Warn));
            }
            foreach (var mx in mxs)
            {
                var host = mx.Exchange.Value.TrimEnd('.');
                if (host == "") { mxItems.Add(new CheckItem("Null-MX", "Domain empfängt bewusst keine E-Mails (RFC 7505).", Level.Info)); continue; }
                string ips = "", ptr = "";
                try
                {
                    var addrs = await Dns.GetHostAddressesAsync(host);
                    ips = string.Join(", ", addrs.Select(x => x.ToString()));
                    var v4 = addrs.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
                    if (v4 != null) ptr = await NetUtil.ReverseDnsAsync(v4);
                }
                catch { ips = "nicht auflösbar"; }
                mxItems.Add(new CheckItem($"MX {mx.Preference}", $"{host}  →  {ips}" + (ptr != "" ? $"  (PTR: {ptr})" : "  (kein PTR)"),
                    ips == "nicht auflösbar" ? Level.Bad : ptr == "" ? Level.Warn : Level.Ok));
            }
            AddSection("MX – Mailserver", mxItems);

            // SPF
            var txt = await DnsHelper.TxtAsync(client, domain);
            var spf = txt.Where(t => t.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase)).ToList();
            var spfItems = new List<CheckItem>();
            if (spf.Count == 0) spfItems.Add(new CheckItem("SPF", "Kein SPF-Eintrag – Absender können gefälscht werden.", Level.Bad));
            else
            {
                if (spf.Count > 1) spfItems.Add(new CheckItem("Mehrere SPF-Einträge", "Ungültig! Es darf nur einen v=spf1-Eintrag geben.", Level.Bad));
                var rec = spf[0];
                spfItems.Add(new CheckItem("Eintrag", rec, Level.Ok));
                int lookups = Regex.Matches(rec, @"(?:^|\s)[+\-~?]?(include:|a(?=[\s:/]|$)|mx(?=[\s:/]|$)|ptr|exists:|redirect=)", RegexOptions.IgnoreCase).Count;
                spfItems.Add(new CheckItem("DNS-Lookups (oberste Ebene)", $"{lookups} von max. 10" + (lookups > 10 ? " – Limit überschritten!" : ""), lookups > 10 ? Level.Bad : lookups > 8 ? Level.Warn : Level.Ok));
                var all = Regex.Match(rec, @"([+\-~?]?)all\b", RegexOptions.IgnoreCase);
                if (!all.Success && !rec.Contains("redirect=")) spfItems.Add(new CheckItem("Abschluss", "Kein „all“-Mechanismus", Level.Warn));
                else if (all.Success)
                {
                    var q = all.Groups[1].Value;
                    spfItems.Add(q switch
                    {
                        "-" => new CheckItem("Abschluss", "-all (hart ablehnen) – empfohlen", Level.Ok),
                        "~" => new CheckItem("Abschluss", "~all (Softfail) – üblich, zusammen mit DMARC in Ordnung", Level.Ok),
                        "?" => new CheckItem("Abschluss", "?all (neutral) – bietet keinen Schutz", Level.Warn),
                        _ => new CheckItem("Abschluss", "+all – erlaubt JEDEM Server das Senden!", Level.Bad),
                    });
                }
                if (rec.Contains("ptr", StringComparison.OrdinalIgnoreCase)) spfItems.Add(new CheckItem("ptr-Mechanismus", "veraltet, sollte nicht verwendet werden", Level.Warn));
            }
            AddSection("SPF", spfItems);

            // DMARC
            var dmarcItems = new List<CheckItem>();
            var dmarc = (await DnsHelper.TxtAsync(client, "_dmarc." + domain)).FirstOrDefault(t => t.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase));
            if (dmarc == null) dmarcItems.Add(new CheckItem("DMARC", "Kein DMARC-Eintrag unter _dmarc." + domain, Level.Bad));
            else
            {
                dmarcItems.Add(new CheckItem("Eintrag", dmarc, Level.Ok));
                var tags = dmarc.Split(';').Select(p => p.Trim().Split('=', 2)).Where(p => p.Length == 2)
                    .GroupBy(p => p[0].Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim());
                var p = tags.GetValueOrDefault("p", "");
                dmarcItems.Add(p switch
                {
                    "reject" => new CheckItem("Richtlinie (p)", "reject – gefälschte Mails werden abgelehnt", Level.Ok),
                    "quarantine" => new CheckItem("Richtlinie (p)", "quarantine – gefälschte Mails landen im Spam", Level.Ok),
                    "none" => new CheckItem("Richtlinie (p)", "none – nur Monitoring, kein Schutz", Level.Warn),
                    _ => new CheckItem("Richtlinie (p)", "fehlt/ungültig", Level.Bad),
                });
                if (tags.TryGetValue("sp", out var sp)) dmarcItems.Add(new CheckItem("Subdomains (sp)", sp));
                if (tags.TryGetValue("pct", out var pct) && pct != "100") dmarcItems.Add(new CheckItem("Anteil (pct)", pct + " %", Level.Warn));
                dmarcItems.Add(tags.TryGetValue("rua", out var rua)
                    ? new CheckItem("Berichte (rua)", rua, Level.Ok)
                    : new CheckItem("Berichte (rua)", "keine Aggregat-Berichte konfiguriert", Level.Warn));
            }
            AddSection("DMARC", dmarcItems);

            // DKIM
            var dkimItems = new List<CheckItem>();
            var selectors = SelectorBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            var dkimTasks = selectors.Select(async s =>
            {
                try
                {
                    var r = await DnsHelper.TxtAsync(client, $"{s}._domainkey.{domain}");
                    return (s, r.FirstOrDefault(t => t.Contains("p=")));
                }
                catch { return (s, (string)null); }
            });
            foreach (var (sel, rec) in await Task.WhenAll(dkimTasks))
            {
                if (rec == null) continue;
                var pm = Regex.Match(rec, @"p=([A-Za-z0-9+/=]*)");
                var key = pm.Success ? pm.Groups[1].Value : "";
                int bytes = key.Length * 3 / 4;
                string bits = key == "" ? "widerrufen (leerer Schlüssel)" : bytes > 500 ? "~4096 Bit" : bytes > 250 ? "~2048 Bit" : bytes > 130 ? "~1024 Bit" : "kurz";
                var algo = Regex.Match(rec, @"k=(\w+)").Groups[1].Value;
                dkimItems.Add(new CheckItem($"Selektor „{sel}“", $"{(algo == "" ? "rsa" : algo)}, Schlüssel {bits}",
                    key == "" ? Level.Warn : bytes > 130 && bytes <= 250 && algo != "ed25519" ? Level.Warn : Level.Ok));
            }
            if (dkimItems.Count == 0)
                dkimItems.Add(new CheckItem("DKIM", "Keiner der getesteten Selektoren gefunden. Den Selektor findest du im Header einer gesendeten Mail (DKIM-Signature: s=…).", Level.Warn));
            AddSection("DKIM", dkimItems);

            // Weitere
            var more = new List<CheckItem>();
            var sts = (await DnsHelper.TxtAsync(client, "_mta-sts." + domain)).FirstOrDefault(t => t.StartsWith("v=STSv1"));
            if (sts != null)
            {
                string mode = "";
                try
                {
                    using var cts = new CancellationTokenSource(6000);
                    var policy = await NetUtil.Http.GetStringAsync($"https://mta-sts.{domain}/.well-known/mta-sts.txt", cts.Token);
                    mode = Regex.Match(policy, @"mode:\s*(\w+)").Groups[1].Value;
                }
                catch { mode = "Richtliniendatei nicht abrufbar"; }
                more.Add(new CheckItem("MTA-STS", $"{sts}  ·  Modus: {mode}", mode == "enforce" ? Level.Ok : Level.Warn));
            }
            else more.Add(new CheckItem("MTA-STS", "nicht konfiguriert (optional, schützt vor TLS-Downgrade)", Level.Info));

            var tlsrpt = (await DnsHelper.TxtAsync(client, "_smtp._tls." + domain)).FirstOrDefault(t => t.StartsWith("v=TLSRPTv1"));
            more.Add(new CheckItem("TLS-RPT", tlsrpt ?? "nicht konfiguriert (optional)", tlsrpt != null ? Level.Ok : Level.Info));
            var bimi = (await DnsHelper.TxtAsync(client, "default._bimi." + domain)).FirstOrDefault(t => t.StartsWith("v=BIMI1"));
            more.Add(new CheckItem("BIMI", bimi ?? "nicht konfiguriert (optional, Markenlogo im Postfach)", bimi != null ? Level.Ok : Level.Info));
            AddSection("Weitere Einträge", more);

            // SMTP
            if (SmtpBox.IsChecked == true && mxs.Count > 0 && mxs[0].Exchange.Value.TrimEnd('.') != "")
            {
                ResultsPanel.Children.Add(UiFactory.Message("SMTP-Test läuft …"));
                var smtp = await SmtpTestAsync(mxs[0].Exchange.Value.TrimEnd('.'));
                ResultsPanel.Children.RemoveAt(ResultsPanel.Children.Count - 1);
                AddSection("SMTP – " + mxs[0].Exchange.Value.TrimEnd('.'), smtp);
            }
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Bad));
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static async Task<List<CheckItem>> SmtpTestAsync(string host)
    {
        var items = new List<CheckItem>();
        try
        {
            using var cts = new CancellationTokenSource(20000);
            using var client = new TcpClient();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await client.ConnectAsync(host, 25, cts.Token); }
            catch
            {
                items.Add(new CheckItem("Verbindung Port 25", "keine Verbindung – viele Internetanbieter sperren ausgehenden Port 25.", Level.Warn));
                return items;
            }
            items.Add(new CheckItem("Verbindung Port 25", $"erfolgreich ({sw.ElapsedMilliseconds} ms)", Level.Ok));

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
                    items.Add(new CheckItem("STARTTLS", $"unterstützt · {SslPage.ProtoName(ssl.SslProtocol)} · {ssl.NegotiatedCipherSuite}", Level.Ok));
                    if (cert != null)
                    {
                        var days = (cert.NotAfter - DateTime.Now).TotalDays;
                        items.Add(new CheckItem("Zertifikat", $"{cert.GetNameInfo(X509NameType.SimpleName, false)} · Aussteller {cert.GetNameInfo(X509NameType.SimpleName, true)} · gültig bis {cert.NotAfter:dd.MM.yyyy}",
                            errors == SslPolicyErrors.None && days > 14 ? Level.Ok : Level.Warn));
                        if (errors != SslPolicyErrors.None) items.Add(new CheckItem("Zertifikatsprüfung", errors.ToString(), Level.Warn));
                    }
                    var sw2 = new StreamWriter(ssl, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    try { await sw2.WriteLineAsync("QUIT"); } catch { }
                }
                else items.Add(new CheckItem("STARTTLS", "angekündigt, aber abgelehnt: " + string.Join(" ", st), Level.Bad));
            }
            else
            {
                items.Add(new CheckItem("STARTTLS", "nicht unterstützt – Mails werden unverschlüsselt übertragen!", Level.Bad));
                try { await writer.WriteLineAsync("QUIT"); } catch { }
            }
        }
        catch (Exception ex)
        {
            items.Add(new CheckItem("SMTP-Test", "Fehler: " + ex.Message, Level.Warn));
        }
        return items;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_text.ToString());
}
