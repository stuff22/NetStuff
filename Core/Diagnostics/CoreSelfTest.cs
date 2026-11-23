using System.IO;
using System.Text;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Diagnostics;

/// <summary>
/// Plattformunabhängiger Selbsttest des Kerns: Protokoll-Parser und Dienste ohne Oberfläche.
/// Aufruf: NetStuff.exe --selftest datei.txt (Windows) oder dotnet run --project CoreTests (jedes System).
/// </summary>
public static class CoreSelfTest
{
    /// <summary>Führt alle Tests aus; <paramref name="live"/> = zusätzlich echte Website-Kopie. Ergebnis: eine Zeile je Test (PASS/FAIL).</summary>
    public static async Task<string> RunAsync(bool live = false, Action<string> progress = null)
    {
        var sb = new StringBuilder();
        void Log(string s) { sb.AppendLine(s); progress?.Invoke(s); }
        async Task T(string name, Func<Task<string>> f)
        {
            try { Log($"PASS {name}: {await f()}"); }
            catch (Exception ex) { Log($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // LLDP-Parser mit synthetischem Frame
        await T("LLDP-Parser", () =>
        {
            var f = new List<byte>();
            f.AddRange(new byte[] { 0x01, 0x80, 0xC2, 0, 0, 0x0E, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x88, 0xCC });
            void Tlv(int type, byte[] v) { int h = (type << 9) | v.Length; f.Add((byte)(h >> 8)); f.Add((byte)h); f.AddRange(v); }
            Tlv(1, new byte[] { 4, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });
            Tlv(2, new byte[] { 5 }.Concat(Encoding.ASCII.GetBytes("Gi1/0/12")).ToArray());
            Tlv(3, new byte[] { 0, 120 });
            Tlv(5, Encoding.ASCII.GetBytes("core-sw01"));
            Tlv(127, new byte[] { 0x00, 0x80, 0xC2, 1, 0, 20 });
            Tlv(0, Array.Empty<byte>());
            var n = LldpCapture.Parse(f.ToArray());
            if (n?.DeviceName != "core-sw01" || n.PortId != "Gi1/0/12" || n.Vlan != "20") throw new Exception($"falsch: {n?.DeviceName}/{n?.PortId}/{n?.Vlan}");
            return Task.FromResult($"{n.DeviceName} {n.PortId} VLAN {n.Vlan}");
        });

        // CDP-Parser
        await T("CDP-Parser", () =>
        {
            var payload = new List<byte> { 2, 180, 0, 0 };
            void Tlv(int t, byte[] v) { int l = v.Length + 4; payload.AddRange(new[] { (byte)(t >> 8), (byte)t, (byte)(l >> 8), (byte)l }); payload.AddRange(v); }
            Tlv(1, Encoding.ASCII.GetBytes("access-sw2"));
            Tlv(3, Encoding.ASCII.GetBytes("FastEthernet0/7"));
            Tlv(0x0A, new byte[] { 0, 30 });
            var f = new List<byte> { 0x01, 0x00, 0x0C, 0xCC, 0xCC, 0xCC, 0, 1, 2, 3, 4, 5 };
            int len = payload.Count + 8;
            f.Add((byte)(len >> 8)); f.Add((byte)len);
            f.AddRange(new byte[] { 0xAA, 0xAA, 0x03, 0x00, 0x00, 0x0C, 0x20, 0x00 });
            f.AddRange(payload);
            var n = LldpCapture.Parse(f.ToArray());
            if (n?.DeviceName != "access-sw2" || n.PortId != "FastEthernet0/7" || n.Vlan != "30") throw new Exception("falsch");
            return Task.FromResult($"{n.DeviceName} {n.PortId} VLAN {n.Vlan}");
        });

        // Übersetzung
        await T("Übersetzung", () =>
        {
            var a = L.T("Übersicht");
            var b = L.T("63 Geräte gefunden");
            return Task.FromResult($"{L.Language}: '{a}' / '{b}'");
        });

        // iPerf3 gegen öffentliche Server
        foreach (var (host, ports) in new[] { ("speedtest.wtnet.de", "5200-5209"), ("ping.online.net", "5200-5209"), ("iperf3.moji.fr", "5200-5209") })
        {
            await T($"iPerf3 {host}", async () =>
            {
                foreach (var port in PortCatalog.Parse(ports))
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(40000);
                        var up = await IperfClient.RunAsync(host, port, 4, 2, false, null, cts.Token);
                        var down = await IperfClient.RunAsync(host, port, 4, 2, true, null, cts.Token);
                        return $"Port {port}: ↑ {up.Mbps:0.0} (client {up.ClientMbps:0.0}) / ↓ {down.Mbps:0.0} Mbit/s, Retr {up.Retransmits}";
                    }
                    catch (IperfBusyException) { }
                }
                throw new Exception("alle Ports belegt");
            });
        }

        // SNMP gegen Gateway
        await T("SNMP Gateway", async () =>
        {
            var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).First(g => g != null);
            var s = new SnmpService(gw, 161, "public", false);
            try
            {
                var info = await s.SystemInfoAsync();
                return string.Join(" | ", info.Take(3).Select(i => $"{i.Name}={i.Value}"));
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException
                                       || ex.GetType().Name.Contains("Timeout"))
            {
                // Die meisten Heimrouter sprechen kein SNMP – das ist kein Fehler des Programms
                return $"übersprungen – Gateway {gw} antwortet nicht auf SNMP ({ex.GetType().Name})";
            }
        });

        // Erreichbarkeit (ICMP über Platform.Icmp)
        await T("Ping 1.1.1.1", async () =>
        {
            var t = new PingStats { Host = "1.1.1.1" };
            using var cts = new CancellationTokenSource(3500);
            await PingRunner.RunAsync(t, new PingSettings { IntervalMs = 1000, TimeoutMs = 1500 }, cts.Token);
            if (t.Received == 0) throw new Exception($"keine Antwort ({t.Status})");
            return $"{t.Received}/{t.Sent} Antworten, Ø {t.AvgText}, Jitter {t.JitterText}";
        });
        await T("Path-MTU 1.1.1.1", async () =>
        {
            var r = await PathMtu.DiscoverAsync(System.Net.IPAddress.Parse("1.1.1.1"), 1500, null);
            if (!r.Reachable || r.Mtu < 1200 || r.Mtu > 1500) throw new Exception($"unplausibel: {r.Mtu}");
            return $"MTU {r.Mtu}, MSS {r.Mss}";
        });
        await T("Traceroute 1.1.1.1", async () =>
        {
            var hops = new List<TraceHop>();
            using var cts = new CancellationTokenSource(60000);
            await Traceroute.RunAsync(System.Net.IPAddress.Parse("1.1.1.1"), 20, 1000, hops, null, cts.Token);
            if (hops.Count == 0) throw new Exception("keine Hops");
            var pts = Traceroute.MapPoints(null, hops);
            return $"{hops.Count} Hops, Ziel {(hops[^1].Reached ? "erreicht" : "nicht erreicht")}, erster Hop {hops[0].Ip} {hops[0].Host}";
        });

        // DNS & Web (echte Abfragen)
        static string Sum(CheckCollector c) =>
            $"{c.Sections.Count} Abschnitte, {c.AllItems.Count(i => i.Level == Level.Ok)} OK / {c.AllItems.Count(i => i.Level == Level.Warn)} Warn / {c.AllItems.Count(i => i.Level == Level.Bad)} Fehler";
        await T("DNS-Abfrage example.com", async () =>
        {
            var r = await DnsLookup.QueryAsync("example.com", "1.1.1.1", false, new[] { DnsClient.QueryType.A, DnsClient.QueryType.NS, DnsClient.QueryType.SOA });
            if (!r.Rows.Any(x => x.Type == "A") || !r.Rows.Any(x => x.Type == "NS")) throw new Exception("A/NS fehlen");
            return $"{r.Rows.Count} Einträge, DNSSEC {(r.Authenticated ? "validiert" : "nicht validiert")}";
        });
        await T("SSL/TLS cloudflare.com", async () =>
        {
            var c = new CheckCollector();
            await SslChecker.CheckAsync("cloudflare.com", 443, null, c);
            if (!c.Sections.Any(s => s.Title.Contains("Zertifikat")) || !c.AllItems.Any(i => i.Value.StartsWith("TLS 1.3") || i.Name == "TLS 1.3"))
                throw new Exception("Zertifikat/TLS 1.3 fehlt: " + Sum(c));
            return Sum(c);
        });
        await T("HTTP-Header example.com", async () =>
        {
            var c = new CheckCollector();
            await HttpHeaderChecker.CheckAsync(HttpHeaderChecker.ParseUrl("example.com"), new HttpHeaderOptions(), c);
            var grade = c.Sections.FirstOrDefault(s => s.Title.Contains("Note") || s.Title.Contains("grade"))?.Title;
            if (grade == null) throw new Exception("keine Sicherheitsnote: " + Sum(c));
            return $"{grade}; {Sum(c)}";
        });
        await T("Mail-Check gmail.com", async () =>
        {
            var c = new CheckCollector();
            await MailChecker.CheckAsync("gmail.com", new MailCheckOptions { DkimSelectors = new[] { "20230601", "google" }, SmtpTest = false }, c);
            if (!c.AllItems.Any(i => i.Name.StartsWith("MX ")) || !c.AllItems.Any(i => i.Value.StartsWith("v=spf1"))) throw new Exception("MX/SPF fehlen: " + Sum(c));
            return Sum(c);
        });
        await T("Domain-Check example.com", async () =>
        {
            var c = new CheckCollector();
            var summary = await DomainChecker.CheckAsync("example.com", false, c);
            if (c.Sections.Count < 6) throw new Exception("zu wenige Abschnitte: " + Sum(c));
            return $"{summary.Items[0].Value}; {Sum(c)}";
        });

        // Website-Kopierer: URL → lokaler Pfad
        await T("Kopierer Pfade", () =>
        {
            void Eq(string url, string mt, bool html, string expected)
            {
                var p = SiteCopier.MapPath(new Uri(url), mt, html).Replace('\\', '/');
                if (p != expected) throw new Exception($"{url} → {p} (erwartet {expected})");
            }
            Eq("https://www.ex.at/", "text/html", true, "www.ex.at/index.html");
            Eq("https://www.ex.at/kontakt", "text/html", true, "www.ex.at/kontakt.html");
            Eq("https://www.ex.at/a/b/", "text/html", true, "www.ex.at/a/b/index.html");
            Eq("https://www.ex.at/index.php", "text/html", true, "www.ex.at/index.html");
            Eq("https://www.ex.at/img/logo.png", "image/png", false, "www.ex.at/img/logo.png");
            Eq("https://www.ex.at/style.php", "text/css", false, "www.ex.at/style.css");
            Eq("https://ex.at:8080/x/con", "image/png", false, "ex.at_8080/x/_con.png");
            var q = SiteCopier.MapPath(new Uri("https://ex.at/wp/style.css?ver=6.1"), "text/css", false).Replace('\\', '/');
            if (!System.Text.RegularExpressions.Regex.IsMatch(q, @"^ex\.at/wp/style_[0-9a-f]{8}\.css$")) throw new Exception(q);
            return Task.FromResult("8 Zuordnungen korrekt");
        });

        // Website-Kopierer: Links umschreiben
        await T("Kopierer Links", () =>
        {
            var html = "<html><head><base href=\"/\"><link rel=\"stylesheet\" href=\"css/a.css?v=1\" integrity=\"sha-x\" crossorigin=\"anonymous\">" +
                       "<link rel=\"canonical\" href=\"https://ex.at/\"><style>body{background:url('img/bg.jpg')}</style>" +
                       "<script src=\"js/app.js\"></script><script>var u = \"<a href='x'>\";</script></head><body>" +
                       "<a href=\"/kontakt#form\">K</a> <a href=\"mailto:a@b.c\">M</a> <a href=\"https://andere.at/seite\">X</a>" +
                       "<img src=\"img/a.png\" srcset=\"img/a.png 1x, img/a@2x.png 2x\" alt=\"a &amp; b\"><div style=\"background-image:url(img/c.jpg)\"></div>" +
                       "</body></html>";
            var docUrl = new Uri("https://ex.at/de/start/");
            var local = new Dictionary<string, string>
            {
                ["https://ex.at/css/a.css?v=1"] = "css/a_1.css",
                ["https://ex.at/kontakt"] = "kontakt.html",
                ["https://ex.at/img/a.png"] = "img/a.png",
                ["https://ex.at/img/a@2x.png"] = "img/a%402x.png",
                ["https://ex.at/img/bg.jpg"] = "img/bg.jpg",
                ["https://ex.at/img/c.jpg"] = "img/c.jpg",
                ["https://ex.at/js/app.js"] = "js/app.js",
            };
            var found = new List<string>();
            var outp = SiteCopier.TestRewriteHtml(html, docUrl, u =>
            {
                if (u == null) return "";
                var k = u.GetLeftPart(UriPartial.Query);
                return local.TryGetValue(k, out var l) ? l + u.Fragment : u.AbsoluteUri;
            }, found);
            string[] must =
            {
                "href=\"css/a_1.css\"", "url(\"img/bg.jpg\")", "src=\"js/app.js\"", "href=\"kontakt.html#form\"", "href=\"mailto:a@b.c\"",
                "href=\"https://andere.at/seite\"", "srcset=\"img/a.png 1x, img/a%402x.png 2x\"", "url(&quot;img/c.jpg&quot;)",
                "<a href='x'>", "alt=\"a &amp; b\"",
            };
            foreach (var m in must) if (!outp.Contains(m)) throw new Exception($"fehlt: {m}\n{outp}");
            if (outp.Contains("integrity") || outp.Contains("crossorigin") || outp.Contains("base href")) throw new Exception("integrity/crossorigin/base nicht entfernt");
            if (!found.Contains("Page https://ex.at/kontakt#form") || !found.Contains("Resource https://ex.at/img/a@2x.png") || found.Any(f => f.Contains("canonical") || f.EndsWith("ex.at/")))
                throw new Exception("Erkennung: " + string.Join(" | ", found));
            return Task.FromResult($"{found.Count} Links erkannt und umgeschrieben");
        });

        // Website-Kopierer live (nur mit --copytest, lädt echte Daten)
        if (live)
            await T("Kopierer live", async () =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "ns-copytest");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                var c = new SiteCopier(new SiteCopyOptions { MaxDepth = 2, MaxFiles = 80, Parallel = 4, Foreign = ForeignMode.Ask }, dir);
                var asked = new List<string>();
                c.ForeignHostFound += h => { lock (asked) asked.Add(h.Host); c.Decide(h.Host, true); };
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await c.RunAsync(new Uri("https://books.toscrape.com/"), cts.Token);
                if (c.StartFile == null) throw new Exception("Startseite fehlt");
                var start = File.ReadAllText(c.StartFile);
                var rx = new System.Text.RegularExpressions.Regex(@"(?:href|src)=""([^""]+)""");
                var links = rx.Matches(start).Select(m => m.Groups[1].Value).Where(l => !l.StartsWith("http") && !l.StartsWith("#")).ToList();
                var broken = links.Where(l => !File.Exists(Path.Combine(Path.GetDirectoryName(c.StartFile)!, Uri.UnescapeDataString(l.Split('#')[0])))).ToList();
                var absOwn = rx.Matches(start).Count(m => m.Groups[1].Value.StartsWith("https://books.toscrape.com"));
                return $"{c.FilesDone} Dateien, {c.PagesDone} Seiten, {SiteCopier.FormatSize(c.BytesDone)}, {c.Errors} Fehler; Start {Path.GetRelativePath(dir, c.StartFile)}; " +
                       $"{links.Count} lokale Links, {broken.Count} ohne Datei ({string.Join(", ", broken.Take(5))}), {absOwn} online belassen; gefragt: {string.Join(", ", asked)}";
            });

        await T("robots.txt", () =>
        {
            var r = RobotsRules.Parse("User-agent: Googlebot\nDisallow: /\n\nUser-agent: *\nDisallow: /intern/\nAllow: /intern/frei\nDisallow: /*.pdf$\n");
            if (!r.Allows("/") || r.Allows("/intern/x") || !r.Allows("/intern/frei/a") || r.Allows("/doc/a.pdf") || !r.Allows("/doc/a.pdf?x"))
                throw new Exception("falsch");
            return Task.FromResult("Regeln korrekt");
        });

        return sb.ToString();
    }
}
