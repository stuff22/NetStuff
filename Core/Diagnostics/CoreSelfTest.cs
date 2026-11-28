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

            // Derselbe Rahmen in einer klassischen pcap-Datei (macOS: tcpdump -w), zweimal hintereinander
            var pcap = new List<byte>();
            pcap.AddRange(new byte[] { 0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 1, 0, 0, 0 });
            for (int k = 0; k < 2; k++)
            {
                pcap.AddRange(new byte[8]);
                pcap.AddRange(BitConverter.GetBytes(f.Count));
                pcap.AddRange(BitConverter.GetBytes(f.Count));
                pcap.AddRange(f);
            }
            var frames = PcapNg.ReadFrames(pcap.ToArray());
            var decoded = LldpCapture.Decode(frames);
            if (frames.Count != 2 || decoded.Count != 1 || decoded[0].DeviceName != "core-sw01") throw new Exception($"pcap: {frames.Count} Rahmen, {decoded.Count} Nachbarn");
            return Task.FromResult($"{n.DeviceName} {n.PortId} VLAN {n.Vlan} · pcap ✓");
        });

        // DHCP über Mitschnitt (macOS: tcpdump, weil der System-Client Port 68 belegt)
        await T("DHCP-Mitschnitt", () =>
        {
            const uint xid = 0x12345678;
            byte[] Frame(bool offer)
            {
                var d = new List<byte> { (byte)(offer ? 2 : 1), 1, 6, 0, 0x12, 0x34, 0x56, 0x78, 0, 0, 0x80, 0 };
                d.AddRange(new byte[4]);                                            // ciaddr
                d.AddRange(offer ? new byte[] { 10, 0, 0, 50 } : new byte[4]);     // yiaddr
                d.AddRange(new byte[8]);                                            // siaddr, giaddr
                d.AddRange(new byte[16 + 192]);                                     // chaddr, sname, file
                d.AddRange(new byte[] { 99, 130, 83, 99, 53, 1, (byte)(offer ? 2 : 1) });
                if (offer) d.AddRange(new byte[] { 54, 4, 10, 0, 0, 1, 1, 4, 255, 255, 255, 0, 3, 4, 10, 0, 0, 1, 51, 4, 0, 1, 81, 128 });
                d.Add(255);
                var udp = new List<byte> { 0, (byte)(offer ? 67 : 68), 0, (byte)(offer ? 68 : 67), (byte)((d.Count + 8) >> 8), (byte)(d.Count + 8), 0, 0 };
                udp.AddRange(d);
                var ip = new List<byte> { 0x45, 0, (byte)((udp.Count + 20) >> 8), (byte)(udp.Count + 20), 0, 0, 0, 0, 64, 17, 0, 0 };
                ip.AddRange(offer ? new byte[] { 10, 0, 0, 1 } : new byte[] { 10, 0, 0, 74 });
                ip.AddRange(new byte[] { 255, 255, 255, 255 });
                ip.AddRange(udp);
                var eth = new List<byte>();
                eth.AddRange(Enumerable.Repeat((byte)0xFF, 6)); eth.AddRange(new byte[] { 0, 0x11, 0x22, 0x33, 0x44, 0x55, 0x08, 0x00 });
                eth.AddRange(ip);
                return eth.ToArray();
            }
            // klassisches pcap mit Zeitstempeln: DISCOVER bei 1,000 s, OFFER bei 1,012 s
            var pcap = new List<byte> { 0xD4, 0xC3, 0xB2, 0xA1, 2, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0, 1, 0, 0, 0 };
            foreach (var (f, usec) in new[] { (Frame(false), 0), (Frame(true), 12000) })
            {
                pcap.AddRange(BitConverter.GetBytes(1)); pcap.AddRange(BitConverter.GetBytes(usec));
                pcap.AddRange(BitConverter.GetBytes(f.Length)); pcap.AddRange(BitConverter.GetBytes(f.Length));
                pcap.AddRange(f);
            }
            var offers = DhcpTester.OffersFromFrames(PcapNg.ReadFramesTimed(pcap.ToArray()), xid);
            if (offers.Count != 1) throw new Exception($"{offers.Count} Angebote");
            var o = offers[0];
            if (o.Server != "10.0.0.1" || o.OfferedIp != "10.0.0.50" || o.SubnetMask != "255.255.255.0" || Math.Abs(o.Ms - 12) > 0.5)
                throw new Exception($"falsch: {o.Server} {o.OfferedIp} {o.SubnetMask} {o.Ms} ms");
            return Task.FromResult($"Server {o.Server} bietet {o.OfferedIp} an · {o.Ms:0} ms · Lease {o.Lease}");
        });

        // Sitemap-Parser (urlset mit CDATA, Entities, lastmod; Index)
        await T("Sitemap-Parser", () =>
        {
            const string set = "<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
                + "<url><loc>https://example.com/b?x=1&amp;y=2</loc><lastmod>2026-11-20T10:00:00+01:00</lastmod></url>"
                + "<url>\n <loc><![CDATA[ https://example.com/%C3%BCber-uns ]]></loc>\n</url></urlset>";
            const string index = "<sitemapindex><sitemap><loc>https://example.com/s1.xml</loc></sitemap><sitemap><loc>https://example.com/s2.xml</loc></sitemap></sitemapindex>";
            var a = SitemapReader.Locs(set);
            var b = SitemapReader.Locs(index);
            if (a.Count != 2 || a[0].Loc != "https://example.com/b?x=1&y=2" || a[0].LastMod != "2026-11-20" || a[1].Loc != "https://example.com/%C3%BCber-uns" || b.Count != 2)
                throw new Exception($"falsch: {string.Join(" | ", a)} · Index {b.Count}");
            return Task.FromResult($"{a.Count} Links (lastmod {a[0].LastMod}) · Index mit {b.Count} Sitemaps");
        });

        // E-Mail-Header (Beispiel, offline)
        await T("E-Mail-Header", async () =>
        {
            var h = MailHeaderAnalyzer.Parse(MailHeaderAnalyzer.Sample + "\nHallo, das ist der Text: kein Header\n");
            var hops = MailHeaderAnalyzer.Hops(h);
            if (hops.Count != 3) throw new Exception($"{hops.Count} Stationen");
            if (hops[0].Ip != "203.0.113.7" || hops[0].With != "ESMTPSA" || hops[1].Ip != "198.51.100.25" || hops[1].Tls != "TLS 1.3" || hops[1].Encrypted != true)
                throw new Exception($"Stationen falsch: {hops[0].Ip} {hops[0].With} · {hops[1].Ip} {hops[1].Tls}");
            if (hops[1].Delay != TimeSpan.FromSeconds(3)) throw new Exception($"Verzögerung {hops[1].Delay}");
            var subject = MailHeaderAnalyzer.Decode(h.First(x => x.Name == "Subject").Value);
            if (subject != "Angebot für das neue Netzwerk") throw new Exception("Betreff: " + subject);
            if (MailHeaderAnalyzer.Decode("=?iso-8859-1?B?R3L832U=?=") != "Grüße") throw new Exception("Latin-1-Dekodierung");
            var sink = new CheckCollector();
            await MailHeaderAnalyzer.AnalyzeAsync(MailHeaderAnalyzer.Sample, sink, lookupLocations: false);
            var verdict = sink.Sections[0].Items[0];
            if (verdict.Level != Level.Ok) throw new Exception("Urteil: " + verdict.Value);
            if (!sink.AllItems.Any(i => i.Name == "Antwortadresse weicht ab")) throw new Exception("Reply-To-Warnung fehlt");
            return $"{sink.Sections.Count} Abschnitte · {hops.Count} Stationen · {verdict.Value[..Math.Min(40, verdict.Value.Length)]} …";
        });

        // Speicher-Checker (Testordner im Temp-Verzeichnis)
        await T("Speicher-Checker", async () =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "netstuff-selftest-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var data = new byte[2 * 1024 * 1024 + 123];
                new Random(42).NextBytes(data);
                Directory.CreateDirectory(Path.Combine(dir, "Fotos", "2024"));
                Directory.CreateDirectory(Path.Combine(dir, "Kopie"));
                Directory.CreateDirectory(Path.Combine(dir, "Archiv"));
                Directory.CreateDirectory(Path.Combine(dir, "Temp"));
                Directory.CreateDirectory(Path.Combine(dir, "Leer"));
                File.WriteAllBytes(Path.Combine(dir, "Fotos", "2024", "bild.jpg"), data);
                File.WriteAllBytes(Path.Combine(dir, "Kopie", "bild (1).jpg"), data);
                data[^1] ^= 0xFF;   // gleiche Größe, anderes Ende → kein Duplikat
                File.WriteAllBytes(Path.Combine(dir, "anders.jpg"), data);
                File.WriteAllBytes(Path.Combine(dir, "Archiv.zip"), new byte[1024 * 1024 + 5]);
                File.WriteAllBytes(Path.Combine(dir, "Temp", "x.tmp"), new byte[1000]);
                var old = Path.Combine(dir, "alt.txt");
                File.WriteAllText(old, "alt");
                File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-6));

                var scanner = new DiskAnalyzer();
                var root = await scanner.ScanAsync(dir, CancellationToken.None);
                long expected = 3L * data.Length + 1024 * 1024 + 5 + 1000 + 3;
                if (root.Size != expected || root.Files != 6 || root.Dirs != 6)
                    throw new Exception($"Summen falsch: {root.Size} B (erwartet {expected}), {root.Files} Dateien, {root.Dirs} Ordner");
                if (root.SubFolders[0].Name != "Fotos" && root.SubFolders[0].Name != "Kopie") throw new Exception("Sortierung: " + root.SubFolders[0].Name);
                var report = DiskAnalyzer.Analyze(root);
                if (!report.Hints.Any(h => h.Title == "Bereits entpackte Archive")) throw new Exception("entpacktes Archiv nicht erkannt");
                if (!report.Hints.Any(h => h.Title == "Temporäre Dateien")) throw new Exception("Temp-Ordner nicht erkannt");
                if (report.EmptyFolders != 2) throw new Exception($"{report.EmptyFolders} leere Ordner");
                var oldFiles = DiskAnalyzer.OldFiles(root, 5);
                if (oldFiles.Count != 1) throw new Exception($"{oldFiles.Count} alte Dateien");
                var dups = await DiskAnalyzer.FindDuplicatesAsync(root, 1024, null, CancellationToken.None);
                if (dups.Count != 1 || dups[0].Files.Count != 2 || dups[0].Wasted != data.Length)
                    throw new Exception($"Duplikate: {dups.Count} Gruppen " + string.Join(" | ", dups.Select(g => g.PathsText)));
                return $"{root.SizeText} in {root.Files} Dateien · {report.Hints.Count} Hinweise · 1 Duplikat ({dups[0].WastedText})";
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
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
        // Öffentliche iPerf3-Server sind oft ausgelastet: der Reihe nach probieren, Fehler nur wenn keiner funktioniert
        await T("iPerf3 (öffentlicher Server)", async () =>
        {
            var errors = new List<string>();
            foreach (var host in new[] { "speedtest.wtnet.de", "ping.online.net", "iperf3.moji.fr" })
            {
                foreach (var port in PortCatalog.Parse("5200-5209"))
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(40000);
                        var up = await IperfClient.RunAsync(host, port, 4, 2, false, null, cts.Token);
                        var down = await IperfClient.RunAsync(host, port, 4, 2, true, null, cts.Token);
                        return $"{host}:{port}: ↑ {up.Mbps:0.0} (client {up.ClientMbps:0.0}) / ↓ {down.Mbps:0.0} Mbit/s, Retr {up.Retransmits}"
                               + (errors.Count > 0 ? $" (vorher: {string.Join("; ", errors)})" : "");
                    }
                    catch (IperfBusyException) { }
                    catch (Exception ex) { errors.Add($"{host}: {ex.GetType().Name}"); break; }
                }
            }
            throw new Exception("kein Server verfügbar: " + string.Join("; ", errors));
        });

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
        await T("ICMP-Parser (macOS-Sender)", () =>
        {
            // Echo-Request wie gesendet (Seq 0x1234) – Prüfsumme muss sich selbst zu 0 ergänzen
            var req = new byte[] { 8, 0, 0, 0, 0xAB, 0xCD, 0x12, 0x34, 1, 2, 3, 4 };
            var cs = DatagramIcmpSender.Checksum(req);
            req[2] = (byte)(cs >> 8); req[3] = (byte)cs;
            if (DatagramIcmpSender.Checksum(req) != 0) throw new Exception("Prüfsumme falsch");

            static byte[] Ip(byte ttl, byte[] body) { var h = new byte[20 + body.Length]; h[0] = 0x45; h[8] = ttl; h[9] = 1; body.CopyTo(h, 20); return h; }
            var reply = Ip(57, new byte[] { 0, 0, 0, 0, 0xAB, 0xCD, 0x12, 0x34, 1, 2, 3, 4 });
            var r1 = DatagramIcmpSender.Parse4(reply, 0x1234);
            if (r1 is not { Status: System.Net.NetworkInformation.IPStatus.Success, Ttl: 57 }) throw new Exception("Echo-Antwort: " + r1);
            if (DatagramIcmpSender.Parse4(reply, 0x1235) != null) throw new Exception("fremde Sequenz akzeptiert");
            // Time Exceeded vom Router: äußerer IP-Kopf + ICMP 11 + innerer IP-Kopf + erste 8 Bytes des Echo-Requests
            var inner = Ip(1, req[..8]);
            var te = Ip(254, new byte[] { 11, 0, 0, 0, 0, 0, 0, 0 }.Concat(inner).ToArray());
            var r2 = DatagramIcmpSender.Parse4(te, 0x1234);
            if (r2 is not { Status: System.Net.NetworkInformation.IPStatus.TtlExpired }) throw new Exception("Time Exceeded: " + r2);
            // Linux-Variante ohne IP-Kopf
            var r3 = DatagramIcmpSender.Parse4(reply[20..], 0x1234);
            if (r3 is not { Status: System.Net.NetworkInformation.IPStatus.Success }) throw new Exception("ohne IP-Kopf: " + r3);
            return Task.FromResult("Echo, TTL, Time Exceeded und Sequenzprüfung korrekt");
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
            if (hops.Count >= 3 && hops.Take(hops.Count - 1).Where(h => h.Address != null).All(h => h.Address.ToString() == "1.1.1.1"))
                throw new Exception("alle Hops melden die Zieladresse – Router-Adressen fehlen (" + Icmp.Sender.GetType().Name + ")");
            var pts = Traceroute.MapPoints(null, hops);
            var echo = await Icmp.Sender.SendAsync(System.Net.IPAddress.Parse("1.1.1.1"), 2000, new byte[32], 128, false);
            if (echo.Status == System.Net.NetworkInformation.IPStatus.Success && echo.Ttl >= 128)
                throw new Exception($"Antwort-TTL {echo.Ttl} unplausibel (gesendeter statt empfangener Wert?)");
            return $"{hops.Count} Hops, Ziel {(hops[^1].Reached ? "erreicht" : "nicht erreicht")}, erster Hop {hops[0].Ip} {hops[0].Host}, Antwort-TTL {echo.Ttl} [{Icmp.Sender.GetType().Name}]";
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

        // Lokales Netz & Werkzeuge
        await T("Subnetzrechner", () =>
        {
            var v4 = SubnetCalculator.Calculate("192.168.10.77/22");
            string V(SubnetInfo s, string name) => s.Sections[0].Items.First(i => i.Name == L.T(name)).Value;
            if (V(v4, "Netzadresse") != "192.168.8.0/22" || V(v4, "Broadcast") != "192.168.11.255" || new string(V(v4, "Nutzbare Hosts").Where(char.IsDigit).ToArray()) != "1022")
                throw new Exception($"IPv4 falsch: {V(v4, "Netzadresse")} / {V(v4, "Broadcast")} / {V(v4, "Nutzbare Hosts")} Hosts");
            var mask = SubnetCalculator.Calculate("10.1.2.3 255.255.255.0");
            if (mask.Prefix != 24) throw new Exception("Maske → Präfix falsch");
            var (rows, _) = SubnetCalculator.Split(v4, 24);
            if (rows.Count != 4 || rows[3].Network != "192.168.11.0/24") throw new Exception("Aufteilen falsch");
            var v6 = SubnetCalculator.Calculate("2001:db8:abcd:12::1/48");
            if (V(v6, "Netz") != "2001:db8:abcd::/48") throw new Exception("IPv6 falsch: " + V(v6, "Netz"));
            if (SubnetCalculator.ReverseZone(NetUtil.ToUInt(System.Net.IPAddress.Parse("10.20.0.0")), 15) != "20–21.10.in-addr.arpa  " + L.F("({0} Zonen)", 2))
                throw new Exception("Reverse-Zone falsch");
            return Task.FromResult("IPv4, Maske, Aufteilen, IPv6, Reverse-Zone korrekt");
        });
        await T("Wake-on-LAN Paket", () =>
        {
            var p = WakeOnLan.MagicPacket(WakeOnLan.ParseMac("c8-5e-a9:8c.e0 38"));
            if (p.Length != 102 || p[0] != 0xFF || p[5] != 0xFF || p[6] != 0xC8 || p[101] != 0x38) throw new Exception("Paket falsch");
            try { WakeOnLan.ParseMac("12:34"); throw new Exception("ungültige MAC akzeptiert"); } catch (Exception ex) when (!ex.Message.Contains("akzeptiert")) { }
            return Task.FromResult("Magic Packet korrekt, ungültige MAC erkannt");
        });
        await T("Resolver-Vergleich", async () =>
        {
            var rows = ResolverBench.DefaultResolvers().Where(r => r.Address is "1.1.1.1" or "8.8.8.8" or "9.9.9.9" or "https://cloudflare-dns.com/dns-query").ToList();
            var summary = await ResolverBench.RunAsync(rows, "example.com", DnsClient.QueryType.A, 2);
            var ok = rows.Count(r => r.Level == Level.Ok);
            if (ok < 3) throw new Exception(string.Join("; ", rows.Select(r => $"{r.Name}: {r.Verdict} {r.Answer}")));
            return $"{ok}/{rows.Count} übereinstimmend · {summary}";
        });
        await T("Whois/RDAP", async () =>
        {
            var w = await WhoisLookup.WhoisAsync("example.com", null, true);
            var r = await WhoisLookup.RdapAsync("example.com");
            if (!w.Raw.Contains("whois", StringComparison.OrdinalIgnoreCase) || r.Summary.Count == 0) throw new Exception("leer");
            return $"Whois {w.Raw.Length} Zeichen, {w.Summary.Count} Felder · RDAP {r.Summary.Count} Felder";
        });
        await T("Port-Scanner 1.1.1.1", async () =>
        {
            using var cts = new CancellationTokenSource(30000);
            var open = await PortScanner.ScanAsync(System.Net.IPAddress.Parse("1.1.1.1"), new[] { 22, 53, 80, 443, 3389 },
                new PortScanSettings { TimeoutMs = 1500, Parallel = 5 }, null, null, cts.Token);
            if (!open.Any(p => p.Port == 443)) throw new Exception("443 nicht offen: " + string.Join(",", open.Select(p => p.Port)));
            var https = open.First(p => p.Port == 443);
            return $"offen: {string.Join(", ", open.Select(p => $"{p.Port}/{p.Service}"))} · 443: {https.Banner}";
        });

        // Übersicht, Netzwerkscanner, Auswertungen
        await T("Übersicht (Adapter)", () =>
        {
            var a = NetworkOverview.Adapters();
            var up = a.Where(x => x.Up).ToList();
            if (up.Count == 0) throw new Exception("kein aktiver Adapter");
            var main = up.First();
            return Task.FromResult($"{a.Count} Adapter, {up.Count} aktiv · {main.Name}: {string.Join(", ", main.Items.Where(i => i.Name is "IPv4" or "Gateway").Select(i => i.Value))}");
        });
        await T("Übersicht (öffentlich)", async () =>
        {
            var items = await NetworkOverview.PublicInfoAsync();
            var v4 = items.First(i => i.Name == L.T("Öffentliche IPv4"));
            if (v4.Level != Level.Ok) throw new Exception(v4.Value);
            return string.Join(" · ", items.Where(i => i.Name != L.T("Reverse-DNS")).Select(i => i.Value));
        });
        await T("Netzwerkscanner (Gateway)", async () =>
        {
            var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).First(g => g != null);
            using var cts = new CancellationTokenSource(30000);
            var hosts = await NetworkScanner.ScanAsync(new[] { gw }, new NetScanSettings { CheckPorts = true }, null, null, null, cts.Token);
            if (hosts.Count != 1) throw new Exception("Gateway nicht gefunden");
            var h = hosts[0];
            return $"{h.Ip} · {h.PingText} · MAC {(h.Mac == "" ? (Platform.HasArp ? "–" : "– (ARP auf dieser Plattform nicht verfügbar)") : h.Mac)} · {h.Vendor} · Typ „{h.Type}“ · Ports {h.Ports}";
        });
        await T("Geräteerkennung", () =>
        {
            ScanHost H(string vendor, string name, params int[] ports) { var h = new ScanHost { Address = System.Net.IPAddress.Parse("10.0.0.9"), Vendor = vendor, Hostname = name }; h.OpenPorts.AddRange(ports); return h; }
            var cases = new (ScanHost Host, string Expect)[]
            {
                (H("Brother Industries", "", 80, 9100), "Drucker"), (H("Synology", "diskstation", 5000, 445), "NAS"),
                (H("", "pc01.local", 135, 445, 3389), "Windows-PC/Server"), (H("Apple, Inc.", "christophs-iphone", 62078), "iPhone"),
                (H("Espressif", "shelly1"), "IoT-Gerät"), (H("", "", 22), "Linux / Netzwerkgerät"),
            };
            var fails = cases.Where(c => DeviceClassifier.Classify(c.Host) != c.Expect).Select(c => $"{c.Host.Vendor}/{c.Host.Hostname} → {DeviceClassifier.Classify(c.Host)}").ToList();
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
            return Task.FromResult($"{cases.Length} Fälle korrekt");
        });
        await T("DHCP-Auswertung", () =>
        {
            var one = DhcpCheck.Evaluate(new List<DhcpOffer> { new() { Server = "10.0.0.1", RelayOrSource = "10.0.0.1", OfferedIp = "10.0.0.50", SubnetMask = "255.255.255.0", Router = "10.0.0.1", Dns = "10.0.0.1", Lease = "1 Tage" } });
            var two = DhcpCheck.Evaluate(new List<DhcpOffer> { new() { Server = "10.0.0.1", RelayOrSource = "10.0.0.1" }, new() { Server = "192.168.1.1", RelayOrSource = "192.168.1.1" } });
            if (one[0].Items[0].Level != Level.Ok || two[0].Items[0].Level != Level.Bad || one.Count != 2 || two.Count != 3) throw new Exception("Bewertung falsch");
            if (DhcpCheck.Evaluate(new()).Single().Items[0].Level != Level.Warn) throw new Exception("leere Antwort falsch");
            var mac = DhcpCheck.RandomMac();
            if ((mac[0] & 0x01) != 0 || (mac[0] & 0x02) == 0) throw new Exception("Zufalls-MAC nicht lokal/unicast");
            return Task.FromResult("1 Server ✓, 2 Server ✗ (Rogue), keine Antwort ⚠, Zufalls-MAC korrekt");
        });
        await T("WLAN-Auswertung", () =>
        {
            WlanBss B(string ssid, int mhz, int rssi, int width = 20, bool conn = false)
            {
                var b = new WlanBss { Ssid = ssid, Bssid = $"00:11:22:33:44:{mhz % 100:00}", FrequencyMhz = mhz, Rssi = rssi, WidthMhz = width, CenterMhz = mhz, Connected = conn };
                (b.Channel, b.Band) = WlanParser.ChannelOf(mhz);
                return b;
            }
            var nets = new List<WlanBss> { B("A", 2412, -40), B("B", 2417, -55), B("C", 2437, -45, conn: true), B("D", 5180, -60, 80) };
            var best = WlanAnalysis.BestChannels(nets, "2,4 GHz");
            if (best[0] != 11) throw new Exception("beste Kanalwahl falsch: " + string.Join(",", best));
            var advice = WlanAnalysis.ChannelAdvice(nets, "2,4 GHz");
            if (!advice.Contains("11") || !advice.Contains("„C“")) throw new Exception(advice);
            if (WlanAnalysis.ChannelAdvice(nets, "6 GHz") != "") throw new Exception("leeres Band");
            var bss = new WlanBss { FrequencyMhz = 5500 };
            (bss.Channel, bss.Band) = WlanParser.ChannelOf(5500);
            // RSN mit SAE + PSK, VHT 80 MHz um Kanal 106
            var ies = new byte[] { 48, 22, 1, 0, 0, 0x0F, 0xAC, 4, 1, 0, 0, 0x0F, 0xAC, 4, 2, 0, 0, 0x0F, 0xAC, 2, 0, 0x0F, 0xAC, 8, 192, 5, 1, 106, 0, 0, 0 };
            WlanParser.ParseIes(bss, ies, 0x11, 8);
            if (bss.Security != "WPA2/WPA3-Personal" || bss.WidthMhz != 80 || bss.Standard != "Wi-Fi 5 (ac)") throw new Exception($"{bss.Security} / {bss.WidthMhz} / {bss.Standard}");
            return Task.FromResult($"Kanalempfehlung 2,4 GHz: {string.Join(", ", best)} · Beacon-Parser: {bss.Security}, {bss.WidthMhz} MHz, {bss.Standard} · WLAN-Scan {(Platform.Wlan != null ? "verfügbar" : "auf dieser Plattform nicht verfügbar")}");
        });
        await T("SNMP-/LLDP-Auswertung", () =>
        {
            var sup = SnmpReport.Supplies(new List<(string, int, int)> { ("Toner Schwarz", 5, 100), ("Toner Cyan", 60, 100), ("Resttonerbehälter", -3, -2) });
            if (sup.Items[0].Level != Level.Bad || sup.Items[1].Level != Level.Ok || sup.Items[2].Level != Level.Info) throw new Exception("Tonerbewertung falsch");
            var lldp = LldpReport.Sections(new List<NeighborInfo> { new() { Protocol = "LLDP", DeviceName = "core-sw01", PortId = "Gi1/0/12", Vlan = "20", SourceMac = "00:11:22:33:44:55" } });
            if (lldp.Count != 1 || !lldp[0].Items.Any(i => i.Value == "20")) throw new Exception("LLDP-Bericht falsch");
            return Task.FromResult("Toner 5 % ✗ / 60 % ✓ / nicht messbar ℹ · LLDP-Bericht korrekt");
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

        // Router-Hostliste: nachgebaute FRITZ!Box (TR-064 mit HTTP-Digest) – neue und alte FRITZ!OS-Variante
        foreach (var modern in new[] { true, false })
            await T(modern ? "TR-064 Hostliste (HostListPath)" : "TR-064 Hostliste (GetGenericHostEntry)", async () =>
            {
                using var fake = new FakeFritzBox(modern, "admin", "geheim");
                var noAuth = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "", "", default, fake.Port);
                if (noAuth.Hosts.Count != 0 || !noAuth.Summary.Contains("Kennwort")) throw new Exception("ohne Anmeldung: " + noAuth.Summary);
                var wrong = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "admin", "falsch", "", default, fake.Port);
                if (wrong.Hosts.Count != 0 || !wrong.Summary.Contains("abgelehnt")) throw new Exception("falsches Kennwort: " + wrong.Summary);
                var r = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "admin", "geheim", "", default, fake.Port);
                if (r.Hosts.Count != 2) throw new Exception($"{r.Hosts.Count} Einträge: {r.Summary}");
                var nas = r.Hosts["192.168.178.20"];
                if (NetUtil.FormatMac(nas.Mac) != NetUtil.FormatMac(new byte[] { 0x00, 0x11, 0x32, 0xAA, 0xBB, 0xCC }) || nas.Name != "nas" || !nas.Active)
                    throw new Exception($"NAS falsch: {NetUtil.FormatMac(nas.Mac)} {nas.Name} {nas.Active}");
                if (r.Hosts["192.168.178.30"] is not { Active: true, Name: "raspi" }) throw new Exception("Inaktiver Eintrag hat aktiven überschrieben");
                return r.Summary;
            });

        await T("SNMP-ARP-Zeile", () =>
        {
            var res = new RouterHostResult();
            RouterHosts.AddSnmp(res, new Lextm.SharpSnmpLib.Variable(new Lextm.SharpSnmpLib.ObjectIdentifier("1.3.6.1.2.1.4.22.1.2.12.10.0.0.42"),
                new Lextm.SharpSnmpLib.OctetString(new byte[] { 0xB8, 0x27, 0xEB, 1, 2, 3 })), 4);
            if (!res.Hosts.TryGetValue("10.0.0.42", out var h) || h.Mac[0] != 0xB8) throw new Exception("nicht erkannt");
            return Task.FromResult("10.0.0.42 → " + NetUtil.FormatMac(h.Mac));
        });

        // Router-Web-APIs gegen nachgebaute Router (HTTP statt HTTPS, sonst identische Abläufe)
        await T("UniFi OS (lokales Konto)", async () =>
        {
            using var r = new FakeRouter();
            r.Route("GET /", _ => (200, "<title>UniFi OS</title>"));
            r.Route("POST /api/auth/login", c => c.Body.Contains("\"geheim\"") ? (200, "{}", new() { ["X-Csrf-Token"] = "t1", ["Set-Cookie"] = "TOKEN=abc; Path=/" }) : (401, "{}", null));
            r.Route("GET /proxy/network/api/self/sites", c => c.Header("Cookie").Contains("TOKEN=abc") ? (200, "{\"data\":[{\"name\":\"default\"}]}") : (401, ""));
            r.Route("GET /proxy/network/api/s/default/stat/sta", c => c.Header("X-Csrf-Token") == "t1"
                ? (200, "{\"data\":[{\"mac\":\"aa:bb:cc:00:00:01\",\"ip\":\"10.0.0.50\",\"hostname\":\"laptop\"},{\"mac\":\"aa:bb:cc:00:00:02\",\"ip\":\"10.0.0.51\",\"name\":\"Drucker\"}]}") : (401, ""));
            r.Route("GET /proxy/network/api/s/default/stat/device", _ => (200, "{\"data\":[{\"mac\":\"18:e8:29:5c:71:2d\",\"ip\":\"10.0.0.12\",\"name\":\"AP Büro\",\"model\":\"U6LR\",\"serial\":\"18E8295C712D\"}]}"));
            var none = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "", "", default, kind: RouterKind.UniFi, webBase: r.Base);
            if (!none.Summary.Contains("UniFi OS erkannt")) throw new Exception("ohne Anmeldung: " + none.Summary);
            var res = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "admin", "geheim", "", default, kind: RouterKind.UniFi, webBase: r.Base);
            if (res.Hosts.Count != 3 || res.Hosts["10.0.0.51"].Name != "Drucker" || res.Hosts["10.0.0.12"].Serial != "18E8295C712D")
                throw new Exception($"{res.Hosts.Count} Einträge: {res.Summary}");
            return res.Summary;
        });

        await T("UniFi (API-Schlüssel)", async () =>
        {
            using var r = new FakeRouter();
            r.Route("GET /", _ => (200, "UniFi OS"));
            r.Route("GET /proxy/network/integration/v1/sites", c => c.Header("X-API-KEY") == "KEY1" ? (200, "{\"data\":[{\"id\":\"s1\"}]}") : (401, ""));
            r.Route("GET /proxy/network/integration/v1/sites/s1/clients", _ => (200, "{\"data\":[{\"name\":\"tv\",\"ipAddress\":\"10.0.0.60\",\"macAddress\":\"aa:bb:cc:00:00:60\"}]}"));
            r.Route("GET /proxy/network/integration/v1/sites/s1/devices", _ => (200, "{\"data\":[]}"));
            var res = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "KEY1", "", default, kind: RouterKind.UniFi, webBase: r.Base);
            if (res.Hosts.Count != 1 || res.Hosts["10.0.0.60"].Name != "tv") throw new Exception(res.Summary);
            return res.Summary;
        });

        await T("OpenWrt (ubus)", async () =>
        {
            using var r = new FakeRouter();
            r.Route("POST /ubus", c =>
            {
                if (c.Body.Contains("\"login\""))
                    return (200, c.Body.Contains("\"geheim\"") ? "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[0,{\"ubus_rpc_session\":\"S1\"}]}" : "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":[6]}");
                if (c.Body.Contains("getHostHints"))
                    return (200, "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":[0,{\"aa:bb:cc:00:00:70\":{\"ipaddrs\":[\"192.168.1.70\"],\"name\":\"nas\"},\"aa:bb:cc:00:00:71\":{\"ipaddrs\":[\"192.168.1.71\"]}}]}");
                if (c.Body.Contains("getDHCPLeases"))
                    return (200, "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":[0,{\"dhcp_leases\":[{\"hostname\":\"handy\",\"ipaddr\":\"192.168.1.72\",\"macaddr\":\"aa:bb:cc:00:00:72\"}]}]}");
                return (200, "{\"jsonrpc\":\"2.0\",\"id\":9,\"result\":[6]}");
            });
            var bad = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "falsch", "", default, kind: RouterKind.OpenWrt, webBase: r.Base);
            if (!bad.Summary.Contains("abgelehnt")) throw new Exception("falsches Kennwort: " + bad.Summary);
            var res = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "geheim", "", default, kind: RouterKind.OpenWrt, webBase: r.Base);
            if (res.Hosts.Count != 3 || res.Hosts["192.168.1.70"].Name != "nas" || res.Hosts["192.168.1.72"].Name != "handy") throw new Exception(res.Summary);
            return res.Summary;
        });

        await T("MikroTik (REST)", async () =>
        {
            using var r = new FakeRouter();
            var auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:geheim"));
            (int, string) Guard(Ctx c, string ok) => c.Header("Authorization") == auth ? (200, ok) : (401, "{\"error\":401,\"message\":\"Unauthorized\"}");
            r.Route("GET /rest/system/identity", c => Guard(c, "{\"name\":\"hEX\"}"));
            r.Route("GET /rest/ip/dhcp-server/lease", c => Guard(c, "[{\"address\":\"192.168.88.10\",\"mac-address\":\"AA:BB:CC:00:00:10\",\"host-name\":\"pc\",\"status\":\"bound\"}]"));
            r.Route("GET /rest/ip/arp", c => Guard(c, "[{\"address\":\"192.168.88.10\",\"mac-address\":\"AA:BB:CC:00:00:10\",\"complete\":\"true\"},{\"address\":\"192.168.88.11\",\"mac-address\":\"AA:BB:CC:00:00:11\",\"complete\":\"true\"}]"));
            var none = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "", "", default, kind: RouterKind.MikroTik, webBase: r.Base);
            if (!none.Summary.Contains("MikroTik erkannt")) throw new Exception("ohne Anmeldung: " + none.Summary);
            var res = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "admin", "geheim", "", default, kind: RouterKind.MikroTik, webBase: r.Base);
            if (res.Hosts.Count != 2 || res.Hosts["192.168.88.10"].Name != "pc") throw new Exception(res.Summary);
            return res.Summary;
        });

        await T("Automatisch: keine falschen Treffer", async () =>
        {
            using var r = new FakeRouter();
            r.Route("GET /", _ => (200, "<title>Irgendein Router</title>"));
            var res = await RouterHosts.QueryAsync(System.Net.IPAddress.Loopback, "", "", "", default, tr064Port: 1, webBase: r.Base);
            if (res.Notes.Count != 0 || res.Hosts.Count != 0) throw new Exception(res.Summary);
            return "nichts erkannt (richtig)";
        });

        // Ubiquiti-Discovery: echte Antwort einer UNAS Pro (gekürzt)
        await T("Ubiquiti-Discovery", () =>
        {
            byte[] Tlv(int t, byte[] v) => new[] { (byte)t, (byte)(v.Length >> 8), (byte)v.Length }.Concat(v).ToArray();
            var body = Tlv(0x02, Convert.FromHexString("6c63f8100afd0a000006")).Concat(Tlv(0x01, Convert.FromHexString("6c63f8100afc")))
                .Concat(Tlv(0x0B, Encoding.ASCII.GetBytes("UNAS-Pro"))).Concat(Tlv(0x06, Encoding.ASCII.GetBytes("UNAS Pro")))
                .Concat(Tlv(0x0C, Encoding.ASCII.GetBytes("UNASPRO"))).Concat(Tlv(0x03, Encoding.ASCII.GetBytes("6.0.18"))).ToArray();
            var pkt = new byte[] { 1, 0, (byte)(body.Length >> 8), (byte)body.Length }.Concat(body).ToArray();
            var f = new DeviceFacts();
            DeviceProbe.ParseUbiquiti(pkt, System.Net.IPAddress.Parse("10.0.0.6"), f);
            if (NetUtil.FormatMac(f.Mac) != NetUtil.FormatMac(Convert.FromHexString("6c63f8100afd")) || f.Model != "UNAS Pro" || f.Firmware != "6.0.18")
                throw new Exception($"{NetUtil.FormatMac(f.Mac)} {f.Model} {f.Firmware}");
            return Task.FromResult($"{NetUtil.FormatMac(f.Mac)} · {f.Model} · {f.Firmware}");
        });

        // mDNS: nachgebauter Responder (Unicast) mit AirPlay- und iRobot-ähnlichen Einträgen
        await T("mDNS (Unicast an Gerät)", async () =>
        {
            using var fake = new FakeMdns();
            var f = new DeviceFacts();
            await DeviceProbe.MdnsAsync(System.Net.IPAddress.Loopback, f, default, fake.Port);
            if (f.Hostname != "Wohnzimmer.local" || NetUtil.FormatMac(f.Mac) != NetUtil.FormatMac(Convert.FromHexString("A4CF99112233"))
                || f.Model != "AppleTV14,1" || f.Serial != "593D25BE3F" || !f.Services.Contains("airplay"))
                throw new Exception($"{f.Hostname} {NetUtil.FormatMac(f.Mac)} {f.Model} {f.Serial} [{string.Join(",", f.Services)}]");
            return $"{f.Hostname} · {NetUtil.FormatMac(f.Mac)} ({f.MacSource}) · {f.Model} · SN {f.Serial}";
        });

        await T("UPnP-Beschreibung", () =>
        {
            var d = new SsdpDevice();
            SsdpDiscovery.ParseDescription(d, System.Xml.Linq.XDocument.Parse(
                "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>Küche</friendlyName><manufacturer>Sonos, Inc.</manufacturer>" +
                "<modelName>Sonos One</modelName><modelNumber>S18</modelNumber><serialNum>48-A6-B8-11-22-33:4</serialNum><MACAddress>48:A6:B8:11:22:33</MACAddress>" +
                "<UDN>uuid:RINCON_48A6B8112233</UDN></device></root>"));
            var f = new DeviceFacts();
            DeviceProbe.ApplySsdp(d, f);
            if (f.Serial != "48-A6-B8-11-22-33:4" || NetUtil.FormatMac(f.Mac) != NetUtil.FormatMac(Convert.FromHexString("48A6B8112233")) || f.Model != "Sonos One S18")
                throw new Exception($"{f.Serial} {NetUtil.FormatMac(f.Mac)} {f.Model}");
            return Task.FromResult($"{f.Name} · {f.Model} · SN {f.Serial}");
        });

        await T("Modelltext und MAC aus Seriennummer", () =>
        {
            var a = NetworkScanner.ModelText("Loxone Electronics GmbH", "Loxone Miniserver Loxone Miniserver");
            var b = NetworkScanner.ModelText("Synology Inc.", "DS412+");
            var c = NetworkScanner.ModelText("HP", "HP LaserJet M203dw");
            var d = NetworkScanner.ModelText("Hewlett Packard Enterprise", "iLO 5 in ProLiant ML350 Gen10");
            if (a != "Loxone Miniserver" || b != "Synology DS412+" || c != "HP LaserJet M203dw" || d != "Hewlett Packard Enterprise iLO 5 in ProLiant ML350 Gen10")
                throw new Exception($"{a} | {b} | {c} | {d}");
            string mac = "";
            if (OuiDatabase.Count > 0)
            {
                mac = NetUtil.FormatMac(NetworkScanner.MacFromId("0011321f7d4c", "Synology Inc."));
                if (mac != NetUtil.FormatMac(Convert.FromHexString("0011321f7d4c"))) throw new Exception("Synology-MAC: " + mac);
                if (NetworkScanner.MacFromId("0011321f7d4c", "Loxone") != null) throw new Exception("falscher Hersteller akzeptiert");
            }
            return Task.FromResult($"{a} · {b} · {c} · {mac}");
        });

        // Exif-Checker: Foto mit GPS, Namen, Seriennummer und Ausrichtung → erkennen, Metadaten entfernen, Ausrichtung behalten
        await T("Exif-Checker (JPEG mit GPS)", () =>
        {
            var jpeg = TestImages.JpegWithExif();
            var r = ExifChecker.Analyze(jpeg, "foto.jpg");
            string Item(string name) => r.Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Name == L.T(name))?.Value ?? "";
            if (!r.HasLocation || Math.Abs(r.Latitude!.Value - 48.208333) > 0.0001 || Math.Abs(r.Longitude!.Value - 16.370833) > 0.0001)
                throw new Exception($"Standort: {r.Latitude} {r.Longitude}");
            if (!Item("Namen").Contains("Max Muster") || !Item("Seriennummern").Contains("123456789") || Item("Kamera") != "Canon EOS R6")
                throw new Exception($"Namen={Item("Namen")} SN={Item("Seriennummern")} Kamera={Item("Kamera")}");
            if (r.Sections[0].Items[0].Level != Level.Bad) throw new Exception("Gesamturteil nicht rot");

            var clean = ExifChecker.StripMetadata(jpeg);
            var c = ExifChecker.Analyze(clean, "foto.jpg");
            if (c.HasLocation || c.Sections.SelectMany(s => s.Items).Any(i => i.Value.Contains("Max Muster") || i.Value.Contains("123456789")))
                throw new Exception("nach dem Entfernen noch Metadaten vorhanden");
            var orient = c.Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.Name == L.T("Ausrichtung"))?.Value ?? "";
            if (orient != L.T("um 90° im Uhrzeigersinn gedreht")) throw new Exception("Ausrichtung verloren: " + orient);
            if (clean[^2] != 0xFF || clean[^1] != 0xD9 || clean.Length >= jpeg.Length) throw new Exception("JPEG-Struktur");
            return Task.FromResult($"{r.TagCount} Einträge → {c.TagCount}; Standort {r.Latitude:0.0000}, {r.Longitude:0.0000}; Ausrichtung „{orient}“ erhalten");
        });

        await T("Exif-Checker (PNG)", () =>
        {
            var png = TestImages.PngWithText();
            var r = ExifChecker.Analyze(png, "bild.png");
            if (!r.Sections.Last().Items.Any(i => i.Value.Contains("Erika Beispiel"))) throw new Exception("tEXt nicht erkannt");
            var clean = ExifChecker.StripMetadata(png);
            var c = ExifChecker.Analyze(clean, "bild.png");
            if (c.Sections.Last().Items.Any(i => i.Value.Contains("Erika Beispiel"))) throw new Exception("tEXt nicht entfernt");
            if (ExifChecker.StripMetadata(Encoding.ASCII.GetBytes("....ftypheic........")) != null) throw new Exception("HEIC sollte nicht unterstützt sein");
            return Task.FromResult($"{png.Length} → {clean.Length} Bytes");
        });

        return sb.ToString();
    }
}

/// <summary>Minimale FRITZ!Box für den Selbsttest: tr64desc.xml, Hosts-Dienst, HTTP-Digest (MD5, qop=auth).</summary>
internal sealed class FakeFritzBox : IDisposable
{
    private readonly System.Net.HttpListener _listener = new();
    private readonly bool _modern;
    private readonly string _user, _pass;
    private const string Realm = "F!Box SOAP-Auth", Nonce = "4F2A9C11D0E3B577";
    public int Port { get; }

    private static readonly (string Ip, string Mac, string Name, string Active)[] Entries =
    {
        ("192.168.178.20", "00:11:32:AA:BB:CC", "nas", "1"),
        ("192.168.178.30", "B8:27:EB:01:02:03", "raspi", "1"),
        ("192.168.178.30", "AA:AA:AA:AA:AA:AA", "altes-geraet", "0"),
        ("", "11:22:33:44:55:66", "ohne-ip", "0"),
    };

    public FakeFritzBox(bool modern, string user, string pass)
    {
        _modern = modern; _user = user; _pass = pass;
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); Port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            System.Net.HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); } catch { return; }
            try { Handle(c); } catch { c.Response.StatusCode = 500; }
            try { c.Response.Close(); } catch { }
        }
    }

    private void Write(System.Net.HttpListenerContext c, string body, int status = 200)
    {
        c.Response.StatusCode = status;
        c.Response.ContentType = "text/xml";
        var b = Encoding.UTF8.GetBytes(body);
        c.Response.OutputStream.Write(b, 0, b.Length);
    }

    private static string Md5(string s) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private bool Authorized(System.Net.HttpListenerContext c)
    {
        var h = c.Request.Headers["Authorization"] ?? "";
        if (!h.StartsWith("Digest ")) return false;
        var p = System.Text.RegularExpressions.Regex.Matches(h, @"(\w+)=(?:""([^""]*)""|([^,\s]+))")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
        if (p.GetValueOrDefault("username") != _user) return false;
        var ha1 = Md5($"{_user}:{Realm}:{_pass}");
        var ha2 = Md5($"{c.Request.HttpMethod}:{p.GetValueOrDefault("uri")}");
        var expect = Md5($"{ha1}:{Nonce}:{p.GetValueOrDefault("nc")}:{p.GetValueOrDefault("cnonce")}:{p.GetValueOrDefault("qop")}:{ha2}");
        return p.GetValueOrDefault("response") == expect;
    }

    private void Handle(System.Net.HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        if (path == "/tr64desc.xml")
        {
            Write(c, "<?xml version=\"1.0\"?><root xmlns=\"urn:dslforum-org:device-1-0\"><device><modelName>FRITZ!Box Test</modelName><serviceList>" +
                     "<service><serviceType>urn:dslforum-org:service:Hosts:1</serviceType><controlURL>/upnp/control/hosts</controlURL></service>" +
                     "</serviceList></device></root>");
            return;
        }
        if (path == "/devicehostlist.lua")
        {
            if (c.Request.QueryString["sid"] != "abc123") { Write(c, "", 403); return; }
            var items = string.Join("", Entries.Select((e, i) =>
                $"<Item><Index>{i + 1}</Index><IPAddress>{e.Ip}</IPAddress><MACAddress>{e.Mac}</MACAddress><Active>{e.Active}</Active><HostName>{e.Name}</HostName></Item>"));
            Write(c, $"<?xml version=\"1.0\"?><List>{items}</List>");
            return;
        }
        if (path != "/upnp/control/hosts") { Write(c, "", 404); return; }

        // Wie die FRITZ!Box: ohne gültige Digest-Anmeldung 401 mit Aufforderung
        if (!Authorized(c))
        {
            c.Response.AddHeader("WWW-Authenticate", $"Digest realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=MD5, qop=\"auth\"");
            Write(c, "", 401);
            return;
        }
        var action = (c.Request.Headers["SOAPAction"] ?? "").Trim('"').Split('#').Last();
        var body = new StreamReader(c.Request.InputStream).ReadToEnd();
        string Resp(string inner) => "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>" +
                                     $"<u:{action}Response xmlns:u=\"urn:dslforum-org:service:Hosts:1\">{inner}</u:{action}Response></s:Body></s:Envelope>";
        switch (action)
        {
            case "X_AVM-DE_GetHostListPath" when _modern:
                Write(c, Resp("<NewX_AVM-DE_HostListPath>/devicehostlist.lua?sid=abc123</NewX_AVM-DE_HostListPath>"));
                break;
            case "GetHostNumberOfEntries":
                Write(c, Resp($"<NewHostNumberOfEntries>{Entries.Length}</NewHostNumberOfEntries>"));
                break;
            case "GetGenericHostEntry":
                var idx = int.Parse(System.Text.RegularExpressions.Regex.Match(body, @"<NewIndex>(\d+)</NewIndex>").Groups[1].Value);
                var e = Entries[idx];
                Write(c, Resp($"<NewIPAddress>{e.Ip}</NewIPAddress><NewAddressSource>DHCP</NewAddressSource><NewLeaseTimeRemaining>0</NewLeaseTimeRemaining>" +
                              $"<NewMACAddress>{e.Mac}</NewMACAddress><NewInterfaceType>Ethernet</NewInterfaceType><NewActive>{e.Active}</NewActive><NewHostName>{e.Name}</NewHostName>"));
                break;
            default: // unbekannte Aktion wie bei älteren FRITZ!OS-Versionen
                Write(c, "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>" +
                         "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:dslforum-org:control-1-0\"><errorCode>401</errorCode>" +
                         "<errorDescription>Invalid Action</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>", 500);
                break;
        }
    }

    public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
}

/// <summary>Anfrage an <see cref="FakeRouter"/>.</summary>
internal sealed class Ctx(System.Net.HttpListenerRequest req, string body)
{
    public string Body { get; } = body;
    public string Header(string n) => req.Headers[n] ?? "";
}

/// <summary>Kleiner HTTP-Server mit festen Antworten für die Router-API-Tests.</summary>
internal sealed class FakeRouter : IDisposable
{
    private readonly System.Net.HttpListener _l = new();
    private readonly Dictionary<string, Func<Ctx, (int Status, string Body, Dictionary<string, string> Headers)>> _routes = new();
    public string Base { get; }

    public FakeRouter()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start(); var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Base = $"http://127.0.0.1:{port}";
        _l.Prefixes.Add(Base + "/");
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                System.Net.HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                try
                {
                    var body = new StreamReader(c.Request.InputStream).ReadToEnd();
                    var key = c.Request.HttpMethod + " " + c.Request.Url!.AbsolutePath;
                    var (status, text, headers) = _routes.TryGetValue(key, out var f) ? f(new Ctx(c.Request, body)) : (404, "not found", null);
                    c.Response.StatusCode = status;
                    foreach (var (k, v) in headers ?? new()) c.Response.AddHeader(k, v);
                    var b = Encoding.UTF8.GetBytes(text);
                    c.Response.OutputStream.Write(b, 0, b.Length);
                }
                catch { c.Response.StatusCode = 500; }
                try { c.Response.Close(); } catch { }
            }
        });
    }

    public void Route(string key, Func<Ctx, (int, string, Dictionary<string, string>)> f) => _routes[key] = f;
    public void Route(string key, Func<Ctx, (int, string)> f) => _routes[key] = c => { var (s, b) = f(c); return (s, b, null); };
    public void Dispose() { try { _l.Stop(); _l.Close(); } catch { } }
}

/// <summary>mDNS-Responder für den Test: beantwortet Unicast-Anfragen wie ein Apple TV (Port 5353 → beliebiger Port).</summary>
internal sealed class FakeMdns : IDisposable
{
    private readonly System.Net.Sockets.UdpClient _u = new(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
    public int Port => ((System.Net.IPEndPoint)_u.Client.LocalEndPoint!).Port;

    public FakeMdns() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        while (true)
        {
            System.Net.Sockets.UdpReceiveResult r;
            try { r = await _u.ReceiveAsync(); } catch { return; }
            var q = r.Buffer;
            int p = 12;
            var labels = new List<string>();
            while (q[p] != 0) { labels.Add(Encoding.UTF8.GetString(q, p + 1, q[p])); p += q[p] + 1; }
            var name = string.Join(".", labels);
            int type = q[p + 1] << 8 | q[p + 2];
            var answers = new List<byte[]>();
            byte[] N(string n) => n.Split('.').SelectMany(l => new[] { (byte)Encoding.UTF8.GetByteCount(l) }.Concat(Encoding.UTF8.GetBytes(l))).Concat(new byte[] { 0 }).ToArray();
            byte[] RR(string owner, int t, byte[] data) => N(owner).Concat(new byte[] { (byte)(t >> 8), (byte)t, 0x80, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length }).Concat(data).ToArray();
            byte[] Txt(params string[] kv) => kv.SelectMany(s => new[] { (byte)Encoding.UTF8.GetByteCount(s) }.Concat(Encoding.UTF8.GetBytes(s))).ToArray();
            const string air = "Wohnzimmer._airplay._tcp.local", raop = "A4CF99112233@Wohnzimmer._raop._tcp.local";
            if (name == "1.0.0.127.in-addr.arpa") answers.Add(RR(name, 12, N("Wohnzimmer.local")));
            else if (name == "_services._dns-sd._udp.local")
            {
                answers.Add(RR(name, 12, N("_airplay._tcp.local")));
                answers.Add(RR(name, 12, N("_raop._tcp.local")));
                answers.Add(RR(name, 12, N("_amzn-alexa._tcp.local")));
            }
            else if (name == "_airplay._tcp.local")
            {
                answers.Add(RR(name, 12, N(air)));
                answers.Add(RR(air, 16, Txt("deviceid=A4:CF:99:11:22:33", "model=AppleTV14,1", "srcvers=770.8.1")));
            }
            else if (name == "_raop._tcp.local") answers.Add(RR(name, 12, N(raop)));
            else if (name == raop && type == 16) answers.Add(RR(raop, 16, Txt("am=AppleTV14,1")));
            else if (name == "_amzn-alexa._tcp.local")
            {
                answers.Add(RR(name, 12, N("Roboter._amzn-alexa._tcp.local")));
                answers.Add(RR("Roboter._amzn-alexa._tcp.local", 16, Txt("version=1", "dsn=593D25BE3F")));
            }
            if (answers.Count == 0) continue;
            var head = new byte[] { q[0], q[1], 0x84, 0, 0, 0, 0, (byte)answers.Count, 0, 0, 0, 0 };
            var resp = head.Concat(answers.SelectMany(a => a)).ToArray();
            await _u.SendAsync(resp, resp.Length, r.RemoteEndPoint);
        }
    }

    public void Dispose() => _u.Dispose();
}

/// <summary>Testbilder für den Exif-Checker (synthetisch, damit der Test überall gleich läuft).</summary>
public static class TestImages
{
    // 8×8-JPEG ohne Metadaten (mit sips erzeugt)
    private const string CleanJpeg = "/9j/4AAQSkZJRgABAQAASABIAAD/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9sAQwAJCQkJCQkQCQkQFhAQEBYeFhYWFh4mHh4eHh4mLiYmJiYmJi4uLi4uLi4uNzc3Nzc3QEBAQEBISEhISEhISEhI/9sAQwELDAwSERIfEREfSzMqM0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tL/90ABAAB/9oADAMBAAIRAxEAPwDN03w30+Wtz/hG/wDZrb03tW5XPWxlXmepWW4+t7COp//Z";

    private record E(ushort Tag, ushort Type, int Count, byte[] Data);
    private static E Ascii(ushort tag, string v) { var b = Encoding.ASCII.GetBytes(v + "\0"); return new(tag, 2, b.Length, b); }
    private static E Short(ushort tag, int v) => new(tag, 3, 1, new[] { (byte)(v >> 8), (byte)v });
    private static E Long(ushort tag, uint v) => new(tag, 4, 1, BitConverter.GetBytes(v).Reverse().ToArray());
    private static E Rationals(ushort tag, params (uint N, uint D)[] v) =>
        new(tag, 5, v.Length, v.SelectMany(x => BitConverter.GetBytes(x.N).Reverse().Concat(BitConverter.GetBytes(x.D).Reverse())).ToArray());

    /// <summary>TIFF (Big Endian) mit IFD0, Exif-IFD und GPS-IFD; Zeiger werden hier eingesetzt.</summary>
    private static byte[] Tiff(List<E> ifd0, List<E> exif, List<E> gps)
    {
        int Size(List<E> l) => 2 + l.Count * 12 + 4 + l.Where(e => e.Data.Length > 4).Sum(e => e.Data.Length + (e.Data.Length & 1));
        ifd0.Add(Long(0x8769, 0)); ifd0.Add(Long(0x8825, 0));
        int o0 = 8, oExif = o0 + Size(ifd0), oGps = oExif + Size(exif);
        ifd0[^2] = Long(0x8769, (uint)oExif); ifd0[^1] = Long(0x8825, (uint)oGps);
        var outp = new List<byte> { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 };
        void Write(List<E> l, int start)
        {
            var sorted = l.OrderBy(e => e.Tag).ToList();
            int data = start + 2 + sorted.Count * 12 + 4;
            var extra = new List<byte>();
            outp.Add((byte)(sorted.Count >> 8)); outp.Add((byte)sorted.Count);
            foreach (var e in sorted)
            {
                outp.AddRange(new[] { (byte)(e.Tag >> 8), (byte)e.Tag, (byte)(e.Type >> 8), (byte)e.Type });
                outp.AddRange(BitConverter.GetBytes((uint)e.Count).Reverse());
                if (e.Data.Length <= 4) outp.AddRange(e.Data.Concat(new byte[4 - e.Data.Length]));
                else
                {
                    outp.AddRange(BitConverter.GetBytes((uint)(data + extra.Count)).Reverse());
                    extra.AddRange(e.Data);
                    if ((e.Data.Length & 1) == 1) extra.Add(0);
                }
            }
            outp.AddRange(new byte[4]);
            outp.AddRange(extra);
        }
        Write(ifd0, o0); Write(exif, oExif); Write(gps, oGps);
        return outp.ToArray();
    }

    public static byte[] JpegWithExif()
    {
        var tiff = Tiff(
            new() { Ascii(0x010F, "Canon"), Ascii(0x0110, "Canon EOS R6"), Short(0x0112, 6), Ascii(0x013B, "Max Muster"), Ascii(0x0131, "Lightroom 12.0") },
            new() { Ascii(0x9003, "2026:11:20 14:30:00"), Ascii(0xA431, "123456789") },
            new() { Ascii(0x0001, "N"), Rationals(0x0002, (48, 1), (12, 1), (30, 1)), Ascii(0x0003, "E"), Rationals(0x0004, (16, 1), (22, 1), (15, 1)) });
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        int len = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload);
        var clean = Convert.FromBase64String(CleanJpeg);
        return clean.Take(2).Concat(app1).Concat(clean.Skip(2)).ToArray();
    }

    private static uint Crc32(byte[] b)
    {
        uint c = 0xFFFFFFFF;
        foreach (var x in b)
        {
            c ^= x;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }
        return ~c;
    }

    public static byte[] PngWithText()
    {
        byte[] Chunk(string type, byte[] data)
        {
            var t = Encoding.ASCII.GetBytes(type);
            var crc = BitConverter.GetBytes(Crc32(t.Concat(data).ToArray())).Reverse().ToArray();
            return BitConverter.GetBytes((uint)data.Length).Reverse().Concat(t).Concat(data).Concat(crc).ToArray();
        }
        var ihdr = new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 0, 0, 0, 0 };
        var idat = new byte[] { 0x78, 0x9C, 0x63, 0x60, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01 };
        return new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Chunk("IHDR", ihdr)).Concat(Chunk("tEXt", Encoding.Latin1.GetBytes("Author\0Erika Beispiel")))
            .Concat(Chunk("IDAT", idat)).Concat(Chunk("IEND", Array.Empty<byte>())).ToArray();
    }
}
