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

        return sb.ToString();
    }
}
