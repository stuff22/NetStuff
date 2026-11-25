using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>Ein Header-Feld (Wert entfaltet, noch nicht dekodiert).</summary>
public sealed record MailHeaderField(string Name, string Value);

/// <summary>Eine Station des Zustellwegs (aus einer „Received:“-Zeile).</summary>
public sealed class MailHop
{
    public int Number { get; set; }
    public string From { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Helo { get; set; } = "";
    public string Ip { get; set; } = "";
    public string By { get; set; } = "";
    public string With { get; set; } = "";
    public string Via { get; set; } = "";
    public string Tls { get; set; } = "";
    public string Cipher { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public TimeSpan? Delay { get; set; }
    public bool IsPrivate { get; set; }
    public GeoInfo Geo { get; set; }
    public string Raw { get; set; } = "";

    /// <summary>true = verschlüsselt, false = unverschlüsselt, null = unbekannt/intern.</summary>
    public bool? Encrypted
    {
        get
        {
            if (Tls != "") return true;
            var w = With.ToUpperInvariant().Replace("UTF8", "");
            if (w is "ESMTPS" or "ESMTPSA" or "SMTPS" or "LMTPS" or "HTTPS" or "SMTPSA") return true;
            if (w is "SMTP" or "ESMTP" or "ESMTPA" or "SMTPA") return false;
            return null;
        }
    }
}

/// <summary>
/// Wertet E-Mail-Header aus und erklärt sie in verständlicher Sprache: Absender, Zustellweg mit Zeiten und
/// Verschlüsselung, SPF/DKIM/DMARC/ARC, Spam-Bewertungen (SpamAssassin, Rspamd, Microsoft 365), Versanddienst,
/// Mail-Programm und typische Warnsignale für gefälschte Nachrichten. Arbeitet offline; optional werden die
/// Standorte der Mailserver über ip-api.com ermittelt.
/// </summary>
public static class MailHeaderAnalyzer
{
    static MailHeaderAnalyzer()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    private static CultureInfo Culture => new(L.IsGerman ? "de-AT" : "en-GB");

    /// <summary>Anleitung: Wo man den Header in gängigen Mail-Programmen findet (über L.T übersetzt anzeigen).</summary>
    public const string HowTo =
        "Outlook (Windows): Nachricht per Doppelklick öffnen → Datei → Eigenschaften → Feld „Internetkopfzeilen“ (Strg+A, Strg+C).\n" +
        "Neues Outlook / Outlook im Web (Microsoft 365): Nachricht öffnen → „…“ (Weitere Aktionen) → Ansicht → Nachrichtendetails anzeigen.\n" +
        "Gmail: Nachricht öffnen → ⋮ (Mehr) → „Original anzeigen“ → „In Zwischenablage kopieren“.\n" +
        "Apple Mail (Mac): Nachricht auswählen → Darstellung → Nachricht → Alle Header (oder „Original-E-Mail“, ⌥⌘U).\n" +
        "Thunderbird: Nachricht auswählen → Ansicht → Nachrichtenquelltext (Strg+U).\n" +
        "GMX / WEB.DE: Nachricht öffnen → „…“ → „Quelltext anzeigen“.\n" +
        "iPhone/iPad: Die Mail-App zeigt keine Header – die Nachricht am Computer oder im Webmail öffnen.\n" +
        "Alternativ die Nachricht als .eml-Datei speichern (z. B. per Ziehen auf den Schreibtisch) und hier mit „Datei öffnen“ laden.";

    /// <summary>Beispiel-Header (erfundene Daten, Dokumentations-Domains und -IP-Adressen) zum Ausprobieren.</summary>
    public const string Sample =
        "Received: from AM9PR01MB1234.eurprd01.prod.outlook.com (2603:10a6:20b:1::10) by AM9PR01MB5678.eurprd01.prod.outlook.com with HTTPS; Wed, 25 Nov 2026 09:17:58 +0000\n" +
        "Received: from mail.example.com (198.51.100.25) by AM4PEPF00000001.mail.protection.outlook.com (10.167.16.1) with Microsoft SMTP Server\n" +
        " (version=TLS1_3, cipher=TLS_AES_256_GCM_SHA384) id 15.20.8000.1; Wed, 25 Nov 2026 09:17:56 +0000\n" +
        "Received: from [192.168.1.23] (dsl-203-0-113-7.example.net [203.0.113.7]) (Authenticated sender: max@example.com)\n" +
        " by mail.example.com (Postfix) with ESMTPSA id 4AbCdE for <erika@example.org>; Wed, 25 Nov 2026 10:17:53 +0100 (CET)\n" +
        "Authentication-Results: spf=pass (sender IP is 198.51.100.25) smtp.mailfrom=example.com; dkim=pass (signature was verified)\n" +
        " header.d=example.com;dmarc=pass action=none header.from=example.com;compauth=pass reason=100\n" +
        "Received-SPF: Pass (protection.outlook.com: domain of example.com designates 198.51.100.25 as permitted sender)\n" +
        " receiver=protection.outlook.com; client-ip=198.51.100.25; helo=mail.example.com;\n" +
        "DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed; d=example.com; s=mail2026; t=1795598273;\n" +
        "\th=From:To:Subject:Date:Message-ID:Content-Type; bh=47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=; b=dGVzdA==\n" +
        "From: \"Max Mustermann\" <max@example.com>\n" +
        "To: Erika Musterfrau <erika@example.org>\n" +
        "Reply-To: Angebote <angebote@example.net>\n" +
        "Subject: =?UTF-8?Q?Angebot_f=C3=BCr_das_neue_Netzwerk?=\n" +
        "Date: Wed, 25 Nov 2026 10:17:50 +0100\n" +
        "Message-ID: <a1b2c3d4e5@mail.example.com>\n" +
        "MIME-Version: 1.0\n" +
        "Content-Type: multipart/alternative; boundary=\"b1\"; charset=\"utf-8\"\n" +
        "X-Mailer: Microsoft Outlook 16.0\n" +
        "X-Priority: 1\n" +
        "X-MS-Exchange-Organization-AuthAs: Anonymous\n" +
        "X-MS-Exchange-Organization-SCL: 1\n" +
        "X-Forefront-Antispam-Report: CIP:198.51.100.25;CTRY:AT;LANG:de;SCL:1;SRV:;IPV:NLI;SFV:NSPM;H:mail.example.com;PTR:mail.example.com;CAT:NONE;SFS:;DIR:INB;\n" +
        "X-Microsoft-Antispam: BCL:0;\n";

    // ===================================================================== Einlesen

    private static readonly Regex HeaderLine = new(@"^([!-9;-~]+):[ \t]?(.*)$", RegexOptions.Compiled);

    /// <summary>Zerlegt den Kopfbereich in Felder (Fortsetzungszeilen werden angehängt). Ein evtl. mitkopierter Text-Teil wird ignoriert.</summary>
    public static List<MailHeaderField> Parse(string raw)
    {
        var lines = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var list = new List<MailHeaderField>();
        string name = null;
        var sb = new StringBuilder();

        void Flush()
        {
            if (name != null) list.Add(new MailHeaderField(name, sb.ToString().Trim()));
            name = null;
            sb.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Trim().Length == 0)
            {
                if (name == null && list.Count == 0) continue;
                // Leerzeile = Ende des Kopfbereichs – außer es folgt erkennbar ein weiteres Header-Feld (Kopierfehler)
                var next = lines.Skip(i + 1).FirstOrDefault(l => l.Trim().Length > 0);
                if (next != null && HeaderLine.Match(next) is { Success: true } nm && LooksLikeHeaderName(nm.Groups[1].Value)) continue;
                break;
            }
            if ((line[0] == ' ' || line[0] == '\t') && name != null)
            {
                sb.Append(' ').Append(line.Trim());
                continue;
            }
            var m = HeaderLine.Match(line);
            if (m.Success)
            {
                Flush();
                name = m.Groups[1].Value;
                sb.Append(m.Groups[2].Value.Trim());
            }
            else if (name != null) sb.Append(' ').Append(line.Trim());   // falsch umbrochene Zeile
        }
        Flush();
        return list;
    }

    private static bool LooksLikeHeaderName(string n) =>
        n.Contains('-') || Explanations.ContainsKey(n) || n.StartsWith("X", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dekodiert MIME-kodierte Wörter (=?utf-8?B?…?= / =?iso-8859-1?Q?…?=).</summary>
    public static string Decode(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains("=?")) return s ?? "";
        s = Regex.Replace(s, @"(\?=)\s+(?==\?)", "$1");
        return Regex.Replace(s, @"=\?([^?]+)\?([BbQq])\?([^?]*)\?=", m =>
        {
            try
            {
                var cs = m.Groups[1].Value;
                var star = cs.IndexOf('*');
                if (star >= 0) cs = cs[..star];
                var enc = Encoding.GetEncoding(cs);
                var data = m.Groups[3].Value;
                var bytes = m.Groups[2].Value is "B" or "b" ? Convert.FromBase64String(data) : QDecode(data);
                return enc.GetString(bytes);
            }
            catch { return m.Value; }
        });
    }

    private static byte[] QDecode(string s)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '_') bytes.Add(0x20);
            else if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else bytes.Add((byte)s[i]);
        }
        return bytes.ToArray();
    }

    /// <summary>Datum nach RFC 5322 („Wed, 25 Nov 2026 09:17:53 +0100 (CET)“).</summary>
    public static DateTimeOffset? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s, @"\([^)]*\)", " ").Trim();
        var m = Regex.Match(s, @"(\d{1,2})\s+([A-Za-z]{3})[a-z]*\.?\s+(\d{2,4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?\s*([+-]\d{2}:?\d{2}|[A-Za-z]{1,5})?");
        if (m.Success)
        {
            var month = "janfebmaraprmayjunjulaugsepoctnovdec".IndexOf(m.Groups[2].Value.ToLowerInvariant(), StringComparison.Ordinal);
            if (month < 0 || month % 3 != 0) return null;
            var year = int.Parse(m.Groups[3].Value);
            if (year < 100) year += year < 50 ? 2000 : 1900;
            var zone = m.Groups[7].Value.ToUpperInvariant().Replace(":", "");
            var offset = zone.Length == 5 && (zone[0] == '+' || zone[0] == '-')
                ? new TimeSpan(int.Parse(zone.Substring(1, 2)), int.Parse(zone.Substring(3, 2)), 0) * (zone[0] == '-' ? -1 : 1)
                : TimeSpan.FromHours(zone switch
                {
                    "EST" => -5, "EDT" => -4, "CST" => -6, "CDT" => -5, "MST" => -7, "MDT" => -6, "PST" => -8, "PDT" => -7,
                    "CET" => 1, "CEST" => 2, "MEZ" => 1, "MESZ" => 2, "BST" => 1, "EET" => 2, "EEST" => 3,
                    _ => 0,
                });
            try
            {
                return new DateTimeOffset(year, month / 3 + 1, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value),
                    int.Parse(m.Groups[5].Value), m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0, offset);
            }
            catch { return null; }
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    // ===================================================================== Zustellweg

    public static List<MailHop> Hops(List<MailHeaderField> headers)
    {
        var hops = headers.Where(h => h.Name.Equals("Received", StringComparison.OrdinalIgnoreCase))
            .Select(h => ParseReceived(h.Value)).ToList();
        hops.Reverse();   // oben steht die letzte Station – chronologisch umdrehen
        for (int i = 0; i < hops.Count; i++)
        {
            hops[i].Number = i + 1;
            if (i > 0 && hops[i].Date is { } d && hops[i - 1].Date is { } p) hops[i].Delay = d - p;
        }
        return hops;
    }

    internal static MailHop ParseReceived(string v)
    {
        var hop = new MailHop { Raw = v };
        var semi = LastTopLevelSemicolon(v);
        var main = semi >= 0 ? v[..semi] : v;
        if (semi >= 0) hop.Date = ParseDate(v[(semi + 1)..]);

        var fm = Regex.Match(main, @"\bfrom\s+(.*?)(?=\s+by\s|\s+with\s|\s+id\s|\s+via\s|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (fm.Success)
        {
            var part = fm.Groups[1].Value;
            hop.From = Regex.Match(part, @"^[^\s(]+").Value.Trim('[', ']');
            // bevorzugt die vom Server festgestellte Verbindung „(name [ip])“, nicht den vom Client genannten HELO-Namen
            var ip = Regex.Match(part, @"\([^()\[]*\[(?:IPv6:)?([0-9a-fA-F:.]{3,})\]");
            if (!ip.Success) ip = Regex.Match(part, @"\[(?:IPv6:)?([0-9a-fA-F:.]{3,})\]");
            if (!ip.Success) ip = Regex.Match(part, @"(?<![\d.])(\d{1,3}(?:\.\d{1,3}){3})(?![\d.])");
            if (!ip.Success) ip = Regex.Match(part, @"\(([0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){2,7})\)");
            if (ip.Success && IPAddress.TryParse(ip.Groups[1].Value, out var addr))
            {
                hop.Ip = addr.ToString();
                hop.IsPrivate = NetUtil.IsPrivate(addr);
            }
            var rn = Regex.Match(part, @"\(\s*([A-Za-z0-9][A-Za-z0-9.-]*\.[A-Za-z]{2,})\s*\[");
            if (rn.Success) hop.FromName = rn.Groups[1].Value;
            var helo = Regex.Match(part, @"helo[= ]\s*\[?([^\s\])]+)", RegexOptions.IgnoreCase);
            if (helo.Success) hop.Helo = helo.Groups[1].Value;
            else if (hop.From != "" && hop.From != hop.Ip) hop.Helo = hop.From;   // „from <HELO> (<rDNS> [<IP>])“
        }
        var by = Regex.Match(main, @"\bby\s+([^\s;()]+)", RegexOptions.IgnoreCase);
        if (by.Success) hop.By = by.Groups[1].Value;
        var with = Regex.Match(main, @"\bwith\s+([^\s;()]+)", RegexOptions.IgnoreCase);
        if (with.Success) hop.With = with.Groups[1].Value;
        if (Regex.IsMatch(main, @"Microsoft SMTP Server", RegexOptions.IgnoreCase) && hop.With.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            hop.With = "Microsoft SMTP Server";
        var via = Regex.Match(main, @"\bvia\s+(.+?)(?=\s+id\s|\s+for\s|$)", RegexOptions.IgnoreCase);
        if (via.Success) hop.Via = via.Groups[1].Value.Trim();
        var tls = Regex.Match(main, @"\b(TLS\s?v?1[._]?[0-3]|TLSv1|SSLv3)\b", RegexOptions.IgnoreCase);
        if (tls.Success)
        {
            var t = Regex.Replace(tls.Groups[1].Value.ToUpperInvariant(), @"TLS\s?V?", "TLS ").Replace('_', '.');
            if (Regex.IsMatch(t, @"^TLS 1[0-3]$")) t = "TLS 1." + t[^1];
            if (t == "TLS 1") t = "TLS 1.0";
            hop.Tls = t;
        }
        var cipher = Regex.Match(main, @"cipher=([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase);
        if (cipher.Success) hop.Cipher = cipher.Groups[1].Value;
        return hop;
    }

    private static int LastTopLevelSemicolon(string v)
    {
        int depth = 0, last = -1;
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '(') depth++;
            else if (v[i] == ')' && depth > 0) depth--;
            else if (v[i] == ';' && depth == 0) last = i;
        }
        return last;
    }

    private static string ProtocolText(MailHop h)
    {
        var w = h.With.ToUpperInvariant();
        if (w is "HTTP" or "HTTPS" && (h.By.EndsWith(".outlook.com", StringComparison.OrdinalIgnoreCase) || h.From.EndsWith(".outlook.com", StringComparison.OrdinalIgnoreCase)))
            return L.T("Weitergabe innerhalb von Microsoft 365");
        var utf8 = w.StartsWith("UTF8");
        w = w.Replace("UTF8", "");
        var text = w switch
        {
            "SMTP" => "klassisches SMTP, unverschlüsselt",
            "ESMTP" => "SMTP, unverschlüsselt",
            "ESMTPS" or "SMTPS" => "SMTP, verschlüsselt",
            "ESMTPSA" or "SMTPSA" => "SMTP, verschlüsselt, Absender angemeldet (typisch: E-Mail-Programm → eigener Mailserver)",
            "ESMTPA" or "SMTPA" => "SMTP, Absender angemeldet, unverschlüsselt",
            "LMTP" or "LMTPS" => "interne Zustellung ins Postfach (LMTP)",
            "LOCAL" => "lokal auf dem Server übergeben",
            "MAPI" => "aus Outlook/Exchange heraus gesendet (MAPI)",
            "HTTP" or "HTTPS" or "HTTPREST" => "über Web-Oberfläche oder Programmschnittstelle (HTTP) eingeliefert",
            "MICROSOFT SMTP SERVER" => "Weitergabe zwischen Microsoft-Exchange-Servern",
            "" => "",
            _ => h.With,
        };
        return text != "" && utf8 ? text + L.T(", mit internationalen Adressen (SMTPUTF8)") : L.T(text);
    }

    // ===================================================================== Auswertung

    /// <summary>Komplette Auswertung als Abschnitte (für die Oberfläche schrittweise über <paramref name="sink"/>).</summary>
    public static async Task AnalyzeAsync(string raw, ICheckSink sink, bool lookupLocations = true, CancellationToken ct = default)
    {
        var headers = Parse(raw);
        if (headers.Count == 0)
            throw new InvalidOperationException(L.T("Keine E-Mail-Header erkannt. Bitte den vollständigen Kopfbereich einfügen (Zeilen wie „Received:“, „From:“, „Subject:“)."));
        var c = new Ctx(headers);

        if (lookupLocations)
        {
            var ips = c.Hops.Where(h => h.Ip != "" && !h.IsPrivate).Select(h => IPAddress.Parse(h.Ip)).ToList();
            if (c.OriginatingIp != null && !NetUtil.IsPrivate(c.OriginatingIp)) ips.Add(c.OriginatingIp);
            if (ips.Count > 0)
            {
                sink.Status(L.T("Standorte der Mailserver werden ermittelt …"));
                try
                {
                    var geo = await GeoIpService.LookupAsync(ips.Distinct(), ct);
                    foreach (var h in c.Hops) if (h.Ip != "" && geo.TryGetValue(h.Ip, out var g) && g.Ok) h.Geo = g;
                    if (c.OriginatingIp != null && geo.TryGetValue(c.OriginatingIp.ToString(), out var og) && og.Ok) c.OriginatingGeo = og;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
        }

        sink.Add(Overview(c));
        sink.Add(Authentication(c));
        sink.Add(Warnings(c));
        if (c.Hops.Count > 0) sink.Add(Route(c));
        if (Spam(c) is { } spam) sink.Add(spam);
        sink.Add(Technical(c));
        sink.Add(AllHeaders(c));
    }

    /// <summary>Kontext: Felder, Adressen, Prüfergebnisse.</summary>
    private sealed class Ctx
    {
        public readonly List<MailHeaderField> Headers;
        public readonly List<MailHop> Hops;
        public readonly List<AuthResult> Auth;
        public readonly MailAddr From;
        public readonly List<MailAddr> ReplyTo, To, Cc;
        public readonly MailAddr Sender, ReturnPath;
        public readonly IPAddress OriginatingIp;
        public GeoInfo OriginatingGeo;
        public readonly List<Dictionary<string, string>> DkimSignatures;
        public readonly string Service;

        public Ctx(List<MailHeaderField> headers)
        {
            Headers = headers;
            Hops = MailHeaderAnalyzer.Hops(headers);
            From = Addresses(Get("From")).FirstOrDefault();
            ReplyTo = Addresses(Get("Reply-To"));
            To = Addresses(Get("To"));
            Cc = Addresses(Get("Cc"));
            Sender = Addresses(Get("Sender")).FirstOrDefault();
            ReturnPath = Addresses(Get("Return-Path")).FirstOrDefault();
            var oip = Regex.Match(Get("X-Originating-IP") ?? Get("X-Sender-IP") ?? "", @"[0-9a-fA-F:.]{7,}");
            if (oip.Success && IPAddress.TryParse(oip.Value, out var a)) OriginatingIp = a;

            // Prüfergebnisse: oberstes Authentication-Results mit Ergebnissen (vom Empfangsserver)
            Auth = All("Authentication-Results").Select(ParseAuthResults).FirstOrDefault(l => l.Count > 0) ?? new List<AuthResult>();
            if (!Auth.Any(r => r.Method == "spf") && Get("Received-SPF") is { } rspf)
            {
                var m = Regex.Match(rspf, @"^\s*(\w+)");
                if (m.Success)
                {
                    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var mf = Regex.Match(rspf, @"envelope-from=""?<?([^\s;>""]+)");
                    if (mf.Success) props["smtp.mailfrom"] = mf.Groups[1].Value;
                    Auth.Add(new AuthResult("spf", m.Groups[1].Value.ToLowerInvariant(), props, Regex.Match(rspf, @"\(([^)]*)\)").Groups[1].Value));
                }
            }
            DkimSignatures = All("DKIM-Signature").Select(Tags).ToList();
            Service = DetectService(this);
        }

        public string Get(string name) => Headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        public IEnumerable<string> All(string name) => Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
        public string FromDomain => From?.Domain ?? "";
        public AuthResult Result(string method) => Auth.FirstOrDefault(r => r.Method == method);
        public IEnumerable<AuthResult> Results(string method) => Auth.Where(r => r.Method == method);
    }

    // ------------------------------------------------------------------ Adressen

    private sealed record MailAddr(string Name, string Address)
    {
        public string Domain => Address.Contains('@') ? Address[(Address.LastIndexOf('@') + 1)..].Trim().TrimEnd('.').ToLowerInvariant() : "";
        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
    }

    private static List<MailAddr> Addresses(string value)
    {
        var list = new List<MailAddr>();
        if (string.IsNullOrWhiteSpace(value)) return list;
        foreach (var part in SplitOutside(value, ','))
        {
            var p = part.Trim();
            if (p == "") continue;
            var m = Regex.Match(p, @"^(.*?)<\s*([^<>\s]*)\s*>");
            if (m.Success)
                list.Add(new MailAddr(Decode(m.Groups[1].Value).Trim().Trim('"', '\'', ' ').Replace("\\\"", "\""), m.Groups[2].Value));
            else
            {
                var a = Regex.Match(p, @"[^\s<>()""]+@[^\s<>()""]+");
                if (a.Success) list.Add(new MailAddr(Decode(Regex.Match(p, @"\(([^)]*)\)").Groups[1].Value), a.Value));
                else if (p.Contains(':')) continue;                      // Gruppe wie „undisclosed-recipients:;“
                else list.Add(new MailAddr("", p));
            }
        }
        return list;
    }

    private static IEnumerable<string> SplitOutside(string s, char sep)
    {
        int depth = 0;
        bool quote = false, angle = false;
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch == '"') quote = !quote;
            else if (!quote && ch == '(') depth++;
            else if (!quote && ch == ')' && depth > 0) depth--;
            else if (!quote && ch == '<') angle = true;
            else if (!quote && ch == '>') angle = false;
            if (ch == sep && !quote && depth == 0 && !angle) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(ch);
        }
        yield return sb.ToString();
    }

    private static readonly HashSet<string> TwoLevelSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "co.at", "or.at", "ac.at", "gv.at", "com.au", "net.au", "org.au",
        "co.nz", "co.za", "com.br", "co.jp", "ne.jp", "com.cn", "com.tr", "co.in", "com.mx", "com.ar", "co.il",
    };

    /// <summary>Organisationsdomain (vereinfachtes „Public Suffix“-Verfahren): mail.firma.co.at → firma.co.at.</summary>
    internal static string OrgDomain(string d)
    {
        if (string.IsNullOrEmpty(d)) return "";
        d = d.Trim().TrimEnd('.').ToLowerInvariant();
        var p = d.Split('.');
        if (p.Length <= 2) return d;
        var last2 = p[^2] + "." + p[^1];
        return TwoLevelSuffixes.Contains(last2) ? p[^3] + "." + last2 : last2;
    }

    private static bool SameOrg(string a, string b) => a != "" && b != "" && OrgDomain(a) == OrgDomain(b);

    private static string DomainOf(string mailOrDomain) =>
        string.IsNullOrEmpty(mailOrDomain) ? "" : mailOrDomain[(mailOrDomain.LastIndexOf('@') + 1)..].Trim('<', '>', ' ', '"').ToLowerInvariant();

    // ------------------------------------------------------------------ Authentication-Results

    private sealed record AuthResult(string Method, string Result, Dictionary<string, string> Props, string Comment)
    {
        public string Prop(string key) => Props.TryGetValue(key, out var v) ? v.Trim('"') : "";
    }

    private static List<AuthResult> ParseAuthResults(string v)
    {
        var list = new List<AuthResult>();
        foreach (var seg in SplitOutside(v, ';'))
        {
            var s = seg.Trim();
            var m = Regex.Match(s, @"^([A-Za-z][\w-]*)\s*=\s*([A-Za-z]+)");
            if (!m.Success) continue;   // Kennung des prüfenden Servers
            var comment = string.Join(" ", Regex.Matches(s, @"\(([^)]*)\)").Select(x => x.Groups[1].Value));
            var rest = Regex.Replace(s[m.Length..], @"\([^)]*\)", " ");
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match p in Regex.Matches(rest, @"([A-Za-z][\w.-]*)\s*=\s*(""[^""]*""|[^\s;]+)"))
                props.TryAdd(p.Groups[1].Value, p.Groups[2].Value);
            list.Add(new AuthResult(m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.ToLowerInvariant(), props, comment));
        }
        return list;
    }

    private static Dictionary<string, string> Tags(string v)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in v.Split(';'))
        {
            var i = part.IndexOf('=');
            if (i > 0) d.TryAdd(part[..i].Trim(), Regex.Replace(part[(i + 1)..], @"\s+", ""));
        }
        return d;
    }

    // ===================================================================== Abschnitte

    private static string Local(DateTimeOffset d) =>
        d.ToLocalTime().ToString(L.IsGerman ? "dddd, dd.MM.yyyy, HH:mm:ss" : "dddd, d MMM yyyy, HH:mm:ss", Culture);

    private static string Duration(TimeSpan t)
    {
        var neg = t < TimeSpan.Zero;
        t = t.Duration();
        var s = t.TotalSeconds < 1 ? L.T("unter 1 s")
            : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s"
            : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
            : t.TotalHours < 48 ? $"{(int)t.TotalHours} h {t.Minutes} min"
            : L.F("{0} Tage", (int)t.TotalDays);
        return neg ? "−" + s : s;
    }

    private static (Level Level, string Text) Verdict(Ctx c)
    {
        var dmarc = c.Result("dmarc");
        var spf = c.Result("spf");
        var dkimOk = c.Results("dkim").Where(r => r.Result == "pass").Select(r => r.Prop("header.d") is { Length: > 0 } d ? d : DomainOf(r.Prop("header.i"))).ToList();
        var dkimAligned = dkimOk.Any(d => SameOrg(d, c.FromDomain));
        var spfAligned = spf?.Result == "pass" && SameOrg(DomainOf(spf.Prop("smtp.mailfrom")), c.FromDomain);

        if (dmarc?.Result == "pass")
            return (Level.Ok, L.F("Absender bestätigt: Die Nachricht stammt nachweislich von der Domain {0} (DMARC bestanden).", c.FromDomain));
        if (dmarc?.Result == "fail")
            return (Level.Bad, L.F("Absender NICHT bestätigt: Die Nachricht gibt sich als {0} aus, ist aber weder durch SPF noch durch DKIM dieser Domain gedeckt – möglicherweise gefälscht.", c.FromDomain));
        if (spf?.Result == "fail" && !dkimAligned)
            return (Level.Bad, L.T("Verdächtig: Der sendende Server ist laut SPF für diese Absenderdomain ausdrücklich NICHT berechtigt."));
        if (dkimAligned || spfAligned)
            return (Level.Ok, L.F("Wahrscheinlich echt: {0} bestätigt die Absenderdomain {1} (keine DMARC-Bewertung vorhanden).", dkimAligned ? "DKIM" : "SPF", c.FromDomain));
        if (c.Auth.Count == 0)
            return (Level.Info, L.T("Keine Prüfergebnisse im Header – entweder wurde nur ein Teil kopiert, oder der Empfangsserver prüft nicht (z. B. interne Nachricht)."));
        return (Level.Warn, L.T("Nicht eindeutig: Die Absenderdomain ist weder durch SPF noch durch DKIM klar bestätigt. Inhalt und Links besonders kritisch prüfen."));
    }

    private static CheckSection Overview(Ctx c)
    {
        var items = new List<CheckItem>();
        var (level, text) = Verdict(c);
        items.Add(new CheckItem("Gesamturteil", text, level));
        if (c.From != null)
            items.Add(new CheckItem("Absender", c.From.ToString()));
        if (c.Sender != null && !c.Sender.Address.Equals(c.From?.Address, StringComparison.OrdinalIgnoreCase))
            items.Add(new CheckItem("Gesendet durch", L.F("{0} (im Auftrag des Absenders)", c.Sender)));
        if (c.ReplyTo.Count > 0 && !c.ReplyTo.All(r => r.Address.Equals(c.From?.Address, StringComparison.OrdinalIgnoreCase)))
            items.Add(new CheckItem("Antworten gehen an", string.Join(", ", c.ReplyTo)));
        if (c.To.Count > 0) items.Add(new CheckItem("Empfänger", Shorten(string.Join(", ", c.To), 400)));
        else if (c.Get("To") is { } toRaw) items.Add(new CheckItem("Empfänger", Decode(toRaw)));
        if (c.Cc.Count > 0) items.Add(new CheckItem("Kopie an (Cc)", Shorten(string.Join(", ", c.Cc), 400)));
        if (c.Get("Subject") is { } subj) items.Add(new CheckItem("Betreff", Decode(subj)));

        var date = ParseDate(c.Get("Date"));
        if (date is { } d)
            items.Add(new CheckItem("Gesendet", L.F("{0} (Zeitzone des Absenders: UTC{1})", Local(d), d.Offset < TimeSpan.Zero ? "−" + d.Offset.ToString(@"hh\:mm") : "+" + d.Offset.ToString(@"hh\:mm"))));
        var lastHop = c.Hops.LastOrDefault(h => h.Date != null);
        if (lastHop?.Date is { } recv)
        {
            var start = date ?? c.Hops.First(h => h.Date != null).Date!.Value;
            items.Add(new CheckItem("Zugestellt", L.F("{0} – Laufzeit insgesamt {1}", Local(recv), Duration(recv - start))));
        }

        var origin = c.Hops.FirstOrDefault(h => h.Ip != "" && !h.IsPrivate);
        if (origin != null)
        {
            // Mit Anmeldung (ESMTPSA/ESMTPA) eingeliefert: die erste öffentliche IP ist der Internetanschluss des Absenders
            var authed = origin.With.ToUpperInvariant().Replace("UTF8", "") is "ESMTPSA" or "ESMTPA" or "SMTPSA" or "SMTPA";
            items.Add(authed
                ? new CheckItem("Internetanschluss des Absenders", HopHost(origin) + GeoText(origin.Geo) + " · " + L.F("hat sich bei {0} angemeldet und dort eingeliefert", origin.By))
                : new CheckItem("Absender-Server", HopHost(origin) + GeoText(origin.Geo)));
        }
        if (c.OriginatingIp != null)
            items.Add(new CheckItem("Absender-Computer", c.OriginatingIp + GeoText(c.OriginatingGeo) + " · " + L.T("IP-Adresse des Geräts, von dem aus geschrieben wurde")));
        if (c.Service != null) items.Add(new CheckItem("Versanddienst", c.Service));
        if (MailerText(c) is { } mailer) items.Add(new CheckItem("Mail-Programm", mailer));
        return new CheckSection("Auf einen Blick", items);
    }

    private static string HopHost(MailHop h)
    {
        var name = h.FromName != "" ? h.FromName : h.From;
        if (name == "" || name == h.Ip) return h.Ip;
        return h.Ip != "" ? $"{name} ({h.Ip})" : name;
    }

    private static string GeoText(GeoInfo g)
    {
        if (g == null) return "";
        var parts = new[] { g.Location, string.IsNullOrEmpty(g.Org) ? g.Isp : g.Org }.Where(s => !string.IsNullOrEmpty(s));
        var t = string.Join(" · ", parts);
        return t == "" ? "" : " – " + t;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    // ------------------------------------------------------------------ SPF / DKIM / DMARC

    private static CheckSection Authentication(Ctx c)
    {
        var items = new List<CheckItem>();
        if (c.Auth.Count == 0)
            items.Add(new CheckItem("Prüfergebnisse", "Kein „Authentication-Results“-Header vorhanden – der Empfangsserver hat keine Prüfergebnisse eingetragen oder der Header wurde nicht vollständig kopiert.", Level.Info));

        foreach (var r in c.Results("spf"))
        {
            var dom = DomainOf(r.Prop("smtp.mailfrom"));
            if (dom == "") dom = DomainOf(r.Prop("smtp.helo"));
            var ip = Regex.Match(r.Comment, @"(?:sender IP is|designates|client-ip=)\s*([0-9a-fA-F:.]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var (lv, t) = r.Result switch
            {
                "pass" => (Level.Ok, L.F("Bestanden: Der sendende Server{0} darf laut SPF-Eintrag für {1} E-Mails versenden.", ip != "" ? $" ({ip})" : "", dom)),
                "fail" or "hardfail" => (Level.Bad, L.F("Fehlgeschlagen: Der sendende Server{0} ist für {1} ausdrücklich NICHT berechtigt – starkes Anzeichen für eine Fälschung.", ip != "" ? $" ({ip})" : "", dom)),
                "softfail" => (Level.Warn, L.F("Schwach fehlgeschlagen: Der Server ist für {0} eigentlich nicht vorgesehen (~all), die Domain lässt es aber zu. Verdächtig, wenn auch DKIM fehlt.", dom)),
                "neutral" => (Level.Info, L.F("Neutral: {0} macht keine Aussage über diesen Server.", dom)),
                "none" => (Level.Warn, L.F("Kein SPF-Eintrag: {0} legt nicht fest, welche Server senden dürfen.", dom)),
                "temperror" => (Level.Info, "Vorübergehender DNS-Fehler bei der Prüfung – kein Urteil möglich."),
                "permerror" => (Level.Warn, L.F("Der SPF-Eintrag von {0} ist fehlerhaft (z. B. zu viele DNS-Abfragen oder Syntaxfehler).", dom)),
                _ => (Level.Info, r.Result),
            };
            items.Add(new CheckItem("SPF – Server berechtigt?", t + Aligned(dom, c.FromDomain), lv));
        }

        var dkims = c.Results("dkim").ToList();
        foreach (var r in dkims)
        {
            var d = r.Prop("header.d") is { Length: > 0 } hd ? hd : DomainOf(r.Prop("header.i"));
            var sel = r.Prop("header.s");
            var who = d + (sel != "" ? L.F(" (Selektor {0})", sel) : "");
            var (lv, t) = r.Result switch
            {
                "pass" => (Level.Ok, L.F("Bestanden: Die digitale Signatur von {0} ist gültig – die Nachricht wurde unterwegs nicht verändert.", who)),
                "fail" => (Level.Bad, L.F("Fehlgeschlagen: Die Signatur von {0} passt nicht – die Nachricht wurde unterwegs verändert (z. B. Weiterleitung, Mailingliste, Virenscanner) oder gefälscht.", who)),
                "none" => (Level.Warn, "Die Nachricht ist nicht digital signiert (kein DKIM)."),
                "neutral" => (Level.Info, L.F("Neutral: Signatur von {0} konnte nicht bewertet werden (z. B. Syntaxfehler).", who)),
                "policy" => (Level.Warn, L.F("Signatur von {0} gültig, aber vom Empfänger nicht akzeptiert (z. B. zu schwacher Schlüssel).", who)),
                "temperror" => (Level.Info, L.F("Vorübergehender Fehler beim Abruf des Schlüssels von {0}.", who)),
                "permerror" => (Level.Warn, L.F("Signatur von {0} nicht prüfbar (Schlüssel fehlt oder ist fehlerhaft).", who)),
                _ => (Level.Info, r.Result),
            };
            items.Add(new CheckItem("DKIM – Signatur echt?", t + (r.Result == "pass" ? Aligned(d, c.FromDomain) : ""), lv));
        }
        if (dkims.Count == 0 && c.DkimSignatures.Count > 0)
            items.Add(new CheckItem("DKIM – Signatur echt?", L.F("Signiert von {0}, das Prüfergebnis fehlt aber im Header.", string.Join(", ", c.DkimSignatures.Select(s => s.GetValueOrDefault("d", "?"))))));

        foreach (var r in c.Results("dmarc"))
        {
            var dom = r.Prop("header.from") is { Length: > 0 } hf ? hf : c.FromDomain;
            var p = r.Prop("p");
            if (p == "") p = Regex.Match(r.Comment, @"\bp=(\w+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var action = r.Prop("action");
            if (action == "") action = r.Prop("dis");
            var policy = p.ToLowerInvariant() switch
            {
                "reject" => L.T("abweisen"),
                "quarantine" => L.T("in den Spam-Ordner"),
                "none" => L.T("nur beobachten"),
                _ => p,
            };
            var (lv, t) = r.Result switch
            {
                "pass" => (Level.Ok, L.F("Bestanden: Die sichtbare Absenderadresse (@{0}) ist durch SPF oder DKIM dieser Domain gedeckt.", dom)),
                "fail" => (Level.Bad, L.F("Fehlgeschlagen: Die sichtbare Absenderadresse (@{0}) ist durch keine bestandene Prüfung gedeckt – sie kann gefälscht sein.", dom)),
                "bestguesspass" => (Level.Info, L.F("{0} hat keine DMARC-Richtlinie, die Prüfung wäre aber bestanden.", dom)),
                "none" => (Level.Warn, L.F("{0} hat keine DMARC-Richtlinie veröffentlicht – Fälschungen dieser Domain werden nicht verhindert.", dom)),
                "temperror" => (Level.Info, "Vorübergehender Fehler bei der DMARC-Prüfung."),
                "permerror" => (Level.Warn, L.F("Die DMARC-Richtlinie von {0} ist fehlerhaft.", dom)),
                _ => (Level.Info, r.Result),
            };
            if (policy != "") t += " " + L.F("Richtlinie der Domain: {0}.", policy);
            if (action != "" && action != "none") t += " " + L.F("Angewendet: {0}.", action);
            items.Add(new CheckItem("DMARC – Absender echt?", t, lv));
        }

        if (c.Result("compauth") is { } ca)
        {
            var reason = ca.Prop("reason");
            var (lv, t) = ca.Result switch
            {
                "pass" => (Level.Ok, "Microsoft hält den Absender für echt."),
                "softpass" => (Level.Info, "Microsoft hält den Absender für wahrscheinlich echt."),
                "fail" => (Level.Bad, "Microsoft hält den Absender für gefälscht (Spoofing)."),
                "none" => (Level.Info, "Microsoft hat den Absender nicht bewertet."),
                _ => (Level.Info, ca.Result),
            };
            if (reason != "") t = L.T(t) + " " + L.F("Grund-Code {0}: {1}", reason, L.T(CompAuthReason(reason)));
            items.Add(new CheckItem("Microsoft-Gesamtprüfung", t, lv));
        }

        foreach (var r in c.Results("arc"))
            items.Add(new CheckItem("ARC – Weiterleitung", r.Result switch
            {
                "pass" => "Die Nachricht wurde unterwegs weitergeleitet (z. B. Mailingliste, Weiterleitung); die Prüfergebnisse vor der Weiterleitung sind vertrauenswürdig erhalten.",
                "fail" => "Die Weiterleitungskette ist beschädigt – Ergebnisse früherer Server sind nicht vertrauenswürdig.",
                "none" => "Keine Weiterleitungskette (ARC) – direkt zugestellt.",
                _ => r.Result,
            }, r.Result == "fail" ? Level.Warn : Level.Info));

        foreach (var r in c.Results("bimi"))
            items.Add(new CheckItem("BIMI – Markenlogo", r.Result == "pass"
                ? L.F("Das Logo von {0} ist hinterlegt und darf angezeigt werden.", r.Prop("header.d"))
                : L.F("Kein geprüftes Markenlogo ({0}).", r.Result)));

        foreach (var s in c.DkimSignatures)
        {
            var d = s.GetValueOrDefault("d", "?");
            var parts = new List<string> { L.F("Domain {0}", d) };
            if (s.TryGetValue("s", out var sel)) parts.Add(L.F("Selektor {0}", sel));
            if (s.TryGetValue("a", out var alg)) parts.Add(alg.ToLowerInvariant() switch
            {
                "rsa-sha256" => L.T("Verfahren RSA-SHA256 (Standard)"),
                "ed25519-sha256" => L.T("Verfahren Ed25519 (modern)"),
                "rsa-sha1" => L.T("Verfahren RSA-SHA1 (veraltet)"),
                _ => alg,
            });
            if (s.TryGetValue("t", out var ts) && long.TryParse(ts, out var unix))
                parts.Add(L.F("signiert {0}", Local(DateTimeOffset.FromUnixTimeSeconds(unix))));
            if (s.TryGetValue("h", out var hl)) parts.Add(L.F("schützt {0} Header-Felder", hl.Split(':', StringSplitOptions.RemoveEmptyEntries).Length));
            items.Add(new CheckItem("DKIM-Signatur", string.Join(" · ", parts)));
        }
        return new CheckSection("Echtheit des Absenders (SPF, DKIM, DMARC)", items,
            "SPF prüft, ob der Server senden darf, DKIM, ob die Nachricht unverändert und signiert ist, DMARC, ob die sichtbare Absenderadresse dazu passt.");
    }

    private static string Aligned(string domain, string fromDomain)
    {
        if (domain == "" || fromDomain == "") return "";
        return SameOrg(domain, fromDomain)
            ? " " + L.T("Passt zur Absenderadresse.")
            : " " + L.F("Gehört nicht zur sichtbaren Absenderdomain {0} (bei Versanddiensten normal).", fromDomain);
    }

    private static string CompAuthReason(string code)
    {
        if (code.Length != 3 || !int.TryParse(code, out var n)) return "";
        return n switch
        {
            000 => "DMARC fehlgeschlagen, Domain verlangt Abweisung oder Quarantäne",
            001 => "Absender nicht bestätigt (implizite Prüfung)",
            002 => "Absenderdomain vom Administrator gesperrt",
            010 => "DMARC fehlgeschlagen (Richtlinie reject/quarantine), Empfangsdomain akzeptiert das",
            < 200 and >= 100 => "bestanden (SPF, DKIM oder DMARC passen)",
            < 300 and >= 200 => "bestanden über implizite Prüfung",
            < 400 and >= 300 => "nicht geprüft",
            < 500 and >= 400 => "Prüfung übersprungen (Ausnahmeregel)",
            < 700 and >= 600 => "interne Nachricht / Sonderfall",
            _ => "",
        };
    }

    // ------------------------------------------------------------------ Warnsignale

    private static CheckSection Warnings(Ctx c)
    {
        var w = new List<CheckItem>();
        var from = c.From;

        if (from != null)
        {
            var nameMail = Regex.Match(from.Name ?? "", @"[\w.+-]+@[\w-]+(?:\.[\w-]+)+");
            if (nameMail.Success && !nameMail.Value.Equals(from.Address, StringComparison.OrdinalIgnoreCase))
                w.Add(new CheckItem("Täuschender Anzeigename", L.F("Der Anzeigename enthält die Adresse {0}, tatsächlich gesendet wurde aber von {1} – ein klassischer Trick in Betrugsmails.", nameMail.Value, from.Address), Level.Bad));

            if (from.Domain.Split('.').Any(l => l.StartsWith("xn--")))
            {
                string uni;
                try { uni = new IdnMapping().GetUnicode(from.Domain); } catch { uni = from.Domain; }
                w.Add(new CheckItem("Internationale Zeichen", L.F("Die Absenderdomain {0} lautet ausgeschrieben „{1}“ – Vorsicht vor Verwechslung mit ähnlich aussehenden Domains.", from.Domain, uni), Level.Warn));
            }

            foreach (var rt in c.ReplyTo.Where(r => r.Domain != "" && !SameOrg(r.Domain, from.Domain)))
                w.Add(new CheckItem("Antwortadresse weicht ab", L.F("Antworten gehen an {0}, nicht an die Absenderdomain {1}. Bei Betrugsmails häufig; bei Newslettern und Ticketsystemen aber oft normal.", rt.Address, from.Domain), Level.Warn));

            if (c.ReturnPath != null && c.ReturnPath.Domain != "" && !SameOrg(c.ReturnPath.Domain, from.Domain))
                w.Add(new CheckItem("Technischer Absender", L.F("Rückläufer gehen an {0} (Return-Path) – bei Versanddiensten und Newslettern üblich, sonst ungewöhnlich.", c.ReturnPath.Domain), Level.Info));

            var dkimPassDomains = c.Results("dkim").Where(r => r.Result == "pass").Select(r => r.Prop("header.d")).Where(d => d != "").ToList();
            if (dkimPassDomains.Count > 0 && !dkimPassDomains.Any(d => SameOrg(d, from.Domain)))
                w.Add(new CheckItem("Fremde Signatur", L.F("Signiert hat {0}, nicht {1} – typisch für Versanddienstleister; ohne DMARC-Bestätigung ist die Absenderdomain aber nicht bewiesen.", string.Join(", ", dkimPassDomains.Distinct()), from.Domain), Level.Info));
        }
        else w.Add(new CheckItem("Kein Absender", "Die Nachricht enthält kein „From:“-Feld.", Level.Warn));

        if (c.Get("Message-ID") == null)
            w.Add(new CheckItem("Keine Message-ID", "Seriöse Mailserver vergeben jeder Nachricht eine eindeutige Kennung – fehlt sie, ist das auffällig.", Level.Warn));

        var date = ParseDate(c.Get("Date"));
        var first = c.Hops.FirstOrDefault(h => h.Date != null)?.Date;
        var last = c.Hops.LastOrDefault(h => h.Date != null)?.Date;
        if (date is { } sentAt && last is { } lastAt && sentAt - lastAt > TimeSpan.FromMinutes(15))
            w.Add(new CheckItem("Datum in der Zukunft", L.F("Das Sendedatum liegt {0} nach der Zustellung – falsch gestellte Uhr oder manipuliertes Datum.", Duration(sentAt - lastAt)), Level.Warn));
        if (date is { } sentAt2 && first is { } firstAt && firstAt - sentAt2 > TimeSpan.FromHours(24))
            w.Add(new CheckItem("Lange unterwegs", L.F("Zwischen Sendedatum und erstem Server liegen {0} – die Nachricht wurde verzögert oder viel später verschickt als geschrieben.", Duration(firstAt - sentAt2)), Level.Info));

        foreach (var h in c.Hops.Where(h => h.Encrypted == false && h.Ip != "" && !h.IsPrivate))
            w.Add(new CheckItem("Unverschlüsselt", L.F("Station {0} ({1} → {2}) wurde ohne Verschlüsselung über das Internet übertragen.", h.Number, HopHost(h), h.By), Level.Warn));
        foreach (var h in c.Hops.Where(h => h.Tls is "TLS 1.0" or "TLS 1.1" or "SSLV3"))
            w.Add(new CheckItem("Veraltete Verschlüsselung", L.F("Station {0} nutzt {1} – veraltet und unsicher.", h.Number, h.Tls), Level.Warn));
        foreach (var h in c.Hops.Where(h => h.Delay is { } dl && dl > TimeSpan.FromMinutes(30)))
            w.Add(new CheckItem("Verzögerung", L.F("Station {0} hat {1} gebraucht (z. B. Greylisting, Warteschlange, Spam-Prüfung).", h.Number, Duration(h.Delay!.Value)), Level.Info));

        if (IsSpamFlagged(c))
            w.Add(new CheckItem("Als Spam eingestuft", "Der Spamfilter des Empfangsservers hat die Nachricht als Spam markiert (Details unten).", Level.Warn));

        if ((c.Get("X-PHP-Originating-Script") ?? c.Get("X-PHP-Script")) is { } php)
            w.Add(new CheckItem("Website-Skript", L.F("Versendet von einem PHP-Skript auf einem Webserver ({0}) – bei Kontaktformularen normal, bei angeblichen Bank- oder Paketmails verdächtig.", php), Level.Info));

        var auto = c.Get("Auto-Submitted");
        if (auto != null && !auto.StartsWith("no", StringComparison.OrdinalIgnoreCase))
            w.Add(new CheckItem("Automatische Nachricht", auto.StartsWith("auto-replied", StringComparison.OrdinalIgnoreCase) ? "Automatische Antwort (z. B. Abwesenheitsnotiz)." : "Automatisch erzeugte Nachricht (z. B. System- oder Benachrichtigungsmail).", Level.Info));
        if (c.Get("Disposition-Notification-To") is { } dnt)
            w.Add(new CheckItem("Lesebestätigung", L.F("Der Absender möchte eine Lesebestätigung an {0}.", Decode(dnt)), Level.Info));

        if (w.Count == 0) w.Add(new CheckItem("Keine Auffälligkeiten", "Keine typischen Warnsignale gefunden.", Level.Ok));
        return new CheckSection("Hinweise & Warnsignale", w);
    }

    // ------------------------------------------------------------------ Zustellweg

    private static CheckSection Route(Ctx c)
    {
        var items = new List<CheckItem>();
        foreach (var h in c.Hops)
        {
            var lines = new List<string>();
            var host = HopHost(h);
            var from = host + GeoText(h.Geo);
            if (h.Helo != "" && !host.Contains(h.Helo, StringComparison.OrdinalIgnoreCase))
                from += L.F(", meldet sich als {0}", h.Helo);
            if (h.IsPrivate && h.Ip != "") from += " · " + L.T("internes Netz");
            lines.Add(host != "" ? L.F("von {0}", from) : L.T("ohne Absenderangabe (lokal eingeliefert)"));
            if (h.By != "") lines.Add(L.F("an {0}", h.By + (h.Via != "" ? $" ({h.Via})" : "")));
            var proto = new List<string>();
            if (ProtocolText(h) is { Length: > 0 } pt) proto.Add(pt);
            if (h.Tls != "") proto.Add(h.Tls + (h.Cipher != "" ? $" ({h.Cipher})" : ""));
            if (proto.Count > 0) lines.Add(string.Join(" · ", proto));
            if (h.Date is { } d) lines.Add(Local(d) + (h.Delay is { } dl ? " · " + L.F("+{0} seit der vorigen Station", Duration(dl)) : ""));

            var lv = h.Encrypted == false && h.Ip != "" && !h.IsPrivate ? Level.Warn
                : h.Delay is { } x && x > TimeSpan.FromMinutes(30) ? Level.Warn
                : h.Encrypted == true ? Level.Ok : Level.Info;
            items.Add(new CheckItem(L.F("{0}. Station", h.Number), string.Join("\n", lines), lv));
        }
        var total = c.Hops.Where(h => h.Date != null).Select(h => h.Date!.Value).ToList();
        var sub = total.Count >= 2
            ? L.F("Chronologisch vom Absender zum Empfänger · {0} Stationen · Laufzeit {1}", c.Hops.Count, Duration(total[^1] - total[0]))
            : L.F("Chronologisch vom Absender zum Empfänger · {0} Stationen", c.Hops.Count);
        return new CheckSection("Zustellweg", items, sub);
    }

    // ------------------------------------------------------------------ Spam

    private static bool IsSpamFlagged(Ctx c)
    {
        if ((c.Get("X-Spam-Flag") ?? "").Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase)) return true;
        if ((c.Get("X-Spam-Status") ?? "").TrimStart().StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) return true;
        var report = Pairs(c.Get("X-Forefront-Antispam-Report"));
        if (report.TryGetValue("SFV", out var sfv) && sfv is "SPM" or "BLK" or "SKS") return true;
        if (int.TryParse(c.Get("X-MS-Exchange-Organization-SCL")?.Trim(), out var scl) && scl >= 5) return true;
        return false;
    }

    private static Dictionary<string, string> Pairs(string v)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(v)) return d;
        foreach (var part in v.Split(';'))
        {
            var i = part.IndexOf(':');
            if (i > 0) d.TryAdd(part[..i].Trim(), part[(i + 1)..].Trim());
        }
        return d;
    }

    private static CheckSection Spam(Ctx c)
    {
        var items = new List<CheckItem>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (c.Get("X-Spam-Status") is { } status)
        {
            handled.Add("X-Spam-Status");
            var yes = status.TrimStart().StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
            var score = Regex.Match(status, @"(?:score|hits)=(-?[\d.]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var req = Regex.Match(status, @"required=(-?[\d.]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var tests = Regex.Match(status, @"tests=([^\s]+(?:,\s*[^\s=]+)*)", RegexOptions.IgnoreCase).Groups[1].Value;
            var text = (yes ? L.T("Spam") : L.T("Kein Spam")) + (score != "" ? " · " + L.F("{0} Punkte (Grenze {1})", score, req != "" ? req : "?") : "");
            items.Add(new CheckItem("SpamAssassin", text, yes ? Level.Warn : Level.Ok));
            if (tests != "")
                items.Add(new CheckItem("Ausgelöste Regeln", string.Join(", ", tests.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(SpamRule))));
        }
        if (c.Get("X-Spam-Flag") is { } flag)
        {
            handled.Add("X-Spam-Flag");
            var yes = flag.Trim().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
            items.Add(new CheckItem("Spam-Markierung", yes ? "JA – als Spam markiert" : "nein", yes ? Level.Warn : Level.Ok));
        }
        foreach (var n in new[] { "X-Spam-Score", "X-Rspamd-Score", "X-Spamd-Bar", "X-Spam-Level" })
            if (c.Get(n) is { } v)
            {
                handled.Add(n);
                items.Add(new CheckItem(n, n == "X-Spam-Level" || n == "X-Spamd-Bar"
                    ? L.F("{0} (je mehr Zeichen, desto spamverdächtiger)", v.Trim())
                    : L.F("{0} Punkte (höher = spamverdächtiger)", v.Trim())));
            }
        if (c.Get("X-Rspamd-Action") is { } act)
        {
            handled.Add("X-Rspamd-Action");
            items.Add(new CheckItem("Rspamd-Entscheidung", act.Trim().ToLowerInvariant() switch
            {
                "no action" => "zugestellt, kein Spam",
                "greylist" => "Greylisting (vorübergehend abgewiesen)",
                "add header" => "als Spam markiert",
                "rewrite subject" => "Betreff als Spam gekennzeichnet",
                "soft reject" => "vorübergehend abgewiesen",
                "reject" => "abgewiesen",
                _ => act,
            }, act.Trim().Equals("no action", StringComparison.OrdinalIgnoreCase) ? Level.Ok : Level.Warn));
        }
        if (c.Get("X-Spamd-Result") is { } sr)
        {
            handled.Add("X-Spamd-Result");
            var m = Regex.Match(sr, @"\[(-?[\d.]+)\s*/\s*(-?[\d.]+)\]");
            var rules = Regex.Matches(sr, @"\b([A-Z][A-Z0-9_]{2,})\(").Select(x => x.Groups[1].Value).Distinct().Take(40);
            items.Add(new CheckItem("Rspamd", (m.Success ? L.F("{0} von {1} Punkten", m.Groups[1].Value, m.Groups[2].Value) + " · " : "") + string.Join(", ", rules)));
        }

        // Microsoft 365 / Exchange Online
        var report = Pairs(c.Get("X-Forefront-Antispam-Report"));
        if (report.Count > 0) handled.Add("X-Forefront-Antispam-Report");
        var sclText = c.Get("X-MS-Exchange-Organization-SCL")?.Trim();
        if (sclText == null && report.TryGetValue("SCL", out var s2)) sclText = s2;
        if (sclText != null && int.TryParse(sclText, out var scl))
        {
            handled.Add("X-MS-Exchange-Organization-SCL");
            items.Add(new CheckItem("Microsoft Spam-Stufe (SCL)", L.F("{0}: {1}", scl, L.T(scl switch
            {
                -1 => "Spamfilter übersprungen (vertrauenswürdiger Absender oder interne Nachricht)",
                0 or 1 => "kein Spam",
                5 or 6 => "Spam – landet im Junk-Ordner",
                7 or 8 or 9 => "sehr wahrscheinlich Spam",
                _ => "unbekannte Stufe",
            })), scl >= 5 ? Level.Warn : scl <= 1 ? Level.Ok : Level.Info));
        }
        if (report.TryGetValue("SFV", out var sfv))
            items.Add(new CheckItem("Microsoft Spamfilter-Urteil", L.F("{0}: {1}", sfv, L.T(sfv switch
            {
                "NSPM" => "kein Spam",
                "SPM" => "Spam",
                "SKN" => "Filter übersprungen (Ausnahmeregel)",
                "SKA" => "Filter übersprungen (Absender auf Zulassungsliste)",
                "SKB" => "Absender auf Sperrliste",
                "SKI" => "Filter übersprungen (interne Nachricht)",
                "SKQ" => "aus der Quarantäne freigegeben",
                "SKS" => "durch Regel als Spam markiert",
                "SFE" => "Absender auf der persönlichen Liste sicherer Absender",
                "BLK" => "Absender auf der persönlichen Sperrliste",
                _ => "",
            })), sfv is "SPM" or "SKB" or "SKS" or "BLK" ? Level.Warn : sfv == "NSPM" ? Level.Ok : Level.Info));
        if (report.TryGetValue("CAT", out var cat) && cat != "")
            items.Add(new CheckItem("Microsoft Kategorie", L.F("{0}: {1}", cat, L.T(cat switch
            {
                "NONE" => "unauffällig",
                "SPM" => "Spam",
                "HSPM" => "Spam mit hoher Sicherheit",
                "BULK" => "Massenmail (Newsletter, Werbung)",
                "PHSH" => "Phishing",
                "HPHSH" => "Phishing mit hoher Sicherheit",
                "MALW" => "Schadsoftware",
                "SPOOF" => "gefälschter Absender (Spoofing)",
                "GIMP" => "Identitätsvortäuschung (Mailbox-Intelligenz)",
                "UIMP" => "Vortäuschung eines Benutzers",
                "DIMP" => "Vortäuschung einer Domain",
                "AMP" => "Schadsoftware-Filter",
                "SAP" => "Sichere Anlagen",
                "OSPM" => "ausgehender Spam",
                "INTOS" => "internes Phishing",
                _ => "",
            })), cat == "NONE" ? Level.Ok : cat == "BULK" ? Level.Info : Level.Warn));
        if (report.TryGetValue("CIP", out var cip))
            items.Add(new CheckItem("Verbindende IP (Microsoft)", cip + (report.TryGetValue("CTRY", out var ctry) && ctry != "" ? " · " + L.F("Land {0}", ctry) : "")));
        if (report.TryGetValue("IPV", out var ipv))
            items.Add(new CheckItem("IP-Bewertung (Microsoft)", ipv switch
            {
                "CAL" => "IP auf der Zulassungsliste",
                "NLI" => "IP auf keiner Reputationsliste",
                _ => ipv,
            }));
        if (report.TryGetValue("PTR", out var ptr) && ptr != "")
            items.Add(new CheckItem("Reverse-DNS (Microsoft)", ptr));
        if (report.TryGetValue("LANG", out var lang) && lang != "")
            items.Add(new CheckItem("Erkannte Sprache", lang));
        var bcl = Regex.Match(c.Get("X-Microsoft-Antispam") ?? "", @"BCL:(\d)");
        if (bcl.Success)
        {
            handled.Add("X-Microsoft-Antispam");
            var b = int.Parse(bcl.Groups[1].Value);
            items.Add(new CheckItem("Massenmail-Stufe (BCL)", L.F("{0}: {1}", b, L.T(b switch
            {
                0 => "keine Massenmail",
                <= 3 => "Massenmail eines Absenders mit wenigen Beschwerden",
                <= 7 => "Massenmail mit gemischten Beschwerden",
                _ => "Massenmail mit vielen Beschwerden",
            }))));
        }
        if (c.Get("X-MS-Exchange-AtpMessageProperties") is { } atp)
        {
            handled.Add("X-MS-Exchange-AtpMessageProperties");
            var parts = atp.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p switch { "SA" => L.T("Sichere Anlagen geprüft"), "SL" => L.T("Sichere Links geprüft"), _ => p });
            items.Add(new CheckItem("Microsoft Defender", string.Join(", ", parts)));
        }

        foreach (var n in new[] { "X-Virus-Scanned", "X-Virus-Status", "X-Amavis-Alert", "X-Antivirus", "X-Antivirus-Status" })
            if (c.Get(n) is { } v)
            {
                handled.Add(n);
                var bad = Regex.IsMatch(v, @"infected|virus found|INFECTED", RegexOptions.IgnoreCase);
                items.Add(new CheckItem("Virenprüfung", L.F("{0}: {1}", n, v), bad ? Level.Bad : Level.Info));
            }

        // weitere Spam-/Phishing-Header unverändert anzeigen
        foreach (var h in c.Headers.Where(h => !handled.Contains(h.Name) && Regex.IsMatch(h.Name, @"^X-.*(spam|phish|virus|bulk)", RegexOptions.IgnoreCase))
                     .GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            items.Add(new CheckItem(h.Name, Shorten(h.Value, 400)));

        return items.Count == 0 ? null : new CheckSection("Spam- und Virenprüfung", items, "Bewertungen der Filter auf dem Weg zum Empfänger.");
    }

    private static string SpamRule(string rule)
    {
        var name = Regex.Replace(rule, @"=.*$", "");
        var text = name switch
        {
            "BAYES_00" or "BAYES_05" => "lernender Filter: sicher kein Spam",
            "BAYES_50" => "lernender Filter: unentschieden",
            "BAYES_80" or "BAYES_95" or "BAYES_99" or "BAYES_999" => "lernender Filter: Spam",
            "DKIM_SIGNED" => "DKIM-signiert",
            "DKIM_VALID" => "DKIM gültig",
            "DKIM_VALID_AU" => "DKIM gültig für Absenderdomain",
            "DKIM_INVALID" => "DKIM ungültig",
            "SPF_PASS" => "SPF bestanden",
            "SPF_FAIL" => "SPF fehlgeschlagen",
            "SPF_SOFTFAIL" => "SPF schwach fehlgeschlagen",
            "SPF_HELO_NONE" => "kein SPF für HELO",
            "HTML_MESSAGE" => "HTML-Nachricht",
            "MIME_HTML_ONLY" => "nur HTML, keine Textversion",
            "RCVD_IN_DNSWL_NONE" or "RCVD_IN_DNSWL_LOW" or "RCVD_IN_DNSWL_MED" or "RCVD_IN_DNSWL_HI" => "Server auf Positivliste (DNSWL)",
            "RCVD_IN_MSPIKE_H2" or "RCVD_IN_MSPIKE_H3" or "RCVD_IN_MSPIKE_H4" or "RCVD_IN_MSPIKE_H5" => "Server mit guter Reputation",
            "RCVD_IN_ZEN_BLOCKED" or "RCVD_IN_SBL" or "RCVD_IN_XBL" or "RCVD_IN_PBL" => "Server auf Spamhaus-Sperrliste",
            "URIBL_BLOCKED" => "Link-Sperrlisten nicht abfragbar",
            "URIBL_ABUSE_SURBL" or "URIBL_DBL_SPAM" or "URIBL_BLACK" => "Link auf Sperrliste",
            "FREEMAIL_FROM" => "Absender ist Freemail-Adresse",
            "FREEMAIL_FORGED_REPLYTO" => "Freemail-Antwortadresse abweichend",
            "MISSING_MID" => "keine Message-ID",
            "MISSING_DATE" => "kein Datum",
            "NO_RELAYS" => "keine Zwischenstationen",
            "SUBJ_ALL_CAPS" => "Betreff in Großbuchstaben",
            "T_SCC_BODY_TEXT_LINE" => "Textzeilen-Prüfung",
            _ => "",
        };
        return text == "" ? rule : $"{rule} ({L.T(text)})";
    }

    // ------------------------------------------------------------------ Technik

    private static readonly (string Pattern, string Name)[] ServiceRules =
    {
        (@"protection\.outlook\.com|outlook\.com|X-MS-Exchange-CrossTenant", "Microsoft 365 / Exchange Online"),
        (@"X-Gm-Message-State|google\.com|googlemail\.com|X-Google-Smtp-Source", "Google (Gmail / Google Workspace)"),
        (@"amazonses\.com", "Amazon SES"),
        (@"sendgrid\.net", "SendGrid (Twilio)"),
        (@"mcsv\.net|mcdlv\.net|mailchimp|rsgsv\.net", "Mailchimp"),
        (@"mandrillapp\.com", "Mandrill (Mailchimp Transactional)"),
        (@"sendinblue\.com|brevo\.com|sib\.|newsletter2go", "Brevo (ehemals Sendinblue)"),
        (@"mailjet\.com", "Mailjet"),
        (@"mailgun\.(org|net|info)", "Mailgun"),
        (@"postmarkapp\.com|pm\.mtasv\.net", "Postmark"),
        (@"sparkpost|sparkpostmail\.com", "SparkPost"),
        (@"hubspot|hubspotemail\.net", "HubSpot"),
        (@"klaviyo", "Klaviyo"),
        (@"exacttarget|salesforce\.com|sfmc", "Salesforce Marketing Cloud"),
        (@"cleverreach", "CleverReach"),
        (@"rapidmail", "rapidmail"),
        (@"mailpoet", "MailPoet (WordPress)"),
        (@"zoho\.(com|eu)", "Zoho Mail"),
        (@"icloud\.com|me\.com|mac\.com", "Apple iCloud Mail"),
        (@"yahoo\.com|yahoodns|ymail", "Yahoo Mail"),
        (@"gmx\.(net|de|at)|web\.de|kundenserver\.de|ionos", "1&1 / IONOS / GMX / WEB.DE"),
        (@"t-online\.de", "Telekom (T-Online)"),
        (@"protonmail|proton\.me", "Proton Mail"),
        (@"mailbox\.org", "mailbox.org"),
        (@"posteo\.de", "Posteo"),
        (@"hetzner\.com|your-server\.de", "Hetzner"),
        (@"world4you", "World4You"),
        (@"easyname", "easyname"),
    };

    private static string DetectService(Ctx c)
    {
        var hay = new StringBuilder();
        foreach (var h in c.Hops) hay.Append(h.From).Append(' ').Append(h.FromName).Append(' ').Append(h.By).Append(' ');
        foreach (var s in c.DkimSignatures) hay.Append(s.GetValueOrDefault("d", "")).Append(' ');
        foreach (var h in c.Headers.Where(h => h.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase))) hay.Append(h.Name).Append(' ');
        hay.Append(c.ReturnPath?.Domain).Append(' ').Append(c.Get("Message-ID")).Append(' ').Append(c.Get("X-Mailer")).Append(' ').Append(c.Get("Feedback-ID"));
        var text = hay.ToString();
        var found = ServiceRules.Where(s => Regex.IsMatch(text, s.Pattern, RegexOptions.IgnoreCase)).Select(s => s.Name).Distinct().ToList();
        return found.Count == 0 ? null : string.Join(", ", found.Take(3));
    }

    private static string MailerText(Ctx c)
    {
        var m = c.Get("X-Mailer") ?? c.Get("User-Agent");
        if (string.IsNullOrWhiteSpace(m)) return null;
        m = Decode(m).Trim();
        var hint = m switch
        {
            _ when m.StartsWith("Microsoft Outlook 16.0") => "Outlook 2016/2019/2021 oder Microsoft 365",
            _ when m.StartsWith("Microsoft Outlook 15.0") => "Outlook 2013",
            _ when m.StartsWith("Microsoft Outlook 14.0") => "Outlook 2010",
            _ when m.Contains("Apple Mail") || m.StartsWith("iPhone Mail") || m.StartsWith("iPad Mail") => "Apple Mail",
            _ when m.Contains("Thunderbird") => "Mozilla Thunderbird",
            _ when m.Contains("PHPMailer") => "PHPMailer – Versand durch ein Website-Skript (z. B. Kontaktformular)",
            _ when m.Contains("WordPress") => "WordPress-Website",
            _ when m.Contains("Roundcube") => "Roundcube Webmail",
            _ when m.Contains("Zimbra") => "Zimbra",
            _ when m.Contains("SOGo") => "SOGo Webmail",
            _ => "",
        };
        return hint == "" ? m : $"{m} ({L.T(hint)})";
    }

    private static CheckSection Technical(Ctx c)
    {
        var items = new List<CheckItem>();
        if (c.Get("Message-ID") is { } mid)
        {
            var dom = Regex.Match(mid, @"@([^>\s]+)").Groups[1].Value;
            items.Add(new CheckItem("Message-ID", mid + (dom != "" ? " · " + L.F("erzeugt von einem Server der Domain {0}", dom) : "")));
        }
        if (c.Get("In-Reply-To") != null || c.Get("References") != null)
        {
            var refs = Regex.Matches(c.Get("References") ?? c.Get("In-Reply-To") ?? "", @"<[^>]+>").Count;
            items.Add(new CheckItem("Gesprächsverlauf", L.F("Antwort auf eine frühere Nachricht ({0} Nachrichten im Verlauf)", Math.Max(refs, 1))));
        }
        if (c.Get("Thread-Topic") is { } topic) items.Add(new CheckItem("Gesprächsthema", Decode(topic)));

        if (c.Get("Content-Type") is { } ct)
        {
            var type = Regex.Match(ct, @"^\s*([\w-]+/[\w.+-]+)").Groups[1].Value.ToLowerInvariant();
            var cs = Regex.Match(ct, @"charset=""?([\w-]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            var text = type switch
            {
                "multipart/alternative" => "Text- und HTML-Version",
                "multipart/mixed" => "mit Anhang (oder mehreren Teilen)",
                "multipart/related" => "HTML mit eingebetteten Bildern",
                "multipart/signed" => "digital signiert (S/MIME oder PGP)",
                "multipart/encrypted" => "verschlüsselt (PGP)",
                "multipart/report" => "Zustellbericht (z. B. Unzustellbarkeitsmeldung)",
                "application/pkcs7-mime" or "application/x-pkcs7-mime" => "verschlüsselt oder signiert (S/MIME)",
                "text/plain" => "reiner Text",
                "text/html" => "nur HTML (keine Textversion)",
                "text/calendar" => "Kalendereinladung",
                _ => "",
            };
            items.Add(new CheckItem("Inhalt", type + (text != "" ? " – " + L.T(text) : "") + (cs != "" ? " · " + L.F("Zeichensatz {0}", cs) : "")));
        }
        if (c.Get("X-MS-Has-Attach") is { } att)
            items.Add(new CheckItem("Anhang (Microsoft)", att.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) ? "ja" : "nein"));
        if (c.Get("Autocrypt") is { } ac)
            items.Add(new CheckItem("Autocrypt", L.F("Der Absender bietet verschlüsselte Antworten an (Schlüssel für {0}).", Regex.Match(ac, @"addr=([^;\s]+)").Groups[1].Value)));

        var prio = c.Get("X-Priority") ?? c.Get("Importance") ?? c.Get("Priority");
        if (prio != null)
        {
            var high = Regex.IsMatch(prio, @"^\s*[12]\b|high|urgent", RegexOptions.IgnoreCase);
            var low = Regex.IsMatch(prio, @"^\s*[45]\b|low|non-urgent", RegexOptions.IgnoreCase);
            items.Add(new CheckItem("Wichtigkeit", high ? "hoch – Absender hat die Nachricht als wichtig markiert (auch ein beliebtes Druckmittel in Betrugsmails)" : low ? "niedrig" : "normal", high ? Level.Info : Level.Info));
        }
        if (c.Get("List-Unsubscribe") is { } unsub)
        {
            var oneClick = c.Get("List-Unsubscribe-Post") != null;
            var links = Regex.Matches(unsub, @"<([^>]+)>").Select(m => m.Groups[1].Value).ToList();
            items.Add(new CheckItem("Abmelden", L.F("Newsletter mit Abmeldemöglichkeit{0}: {1}", oneClick ? L.T(" (Ein-Klick-Abmeldung)") : "", string.Join(" · ", links))));
        }
        if (c.Get("List-Id") is { } lid) items.Add(new CheckItem("Verteilerliste", Decode(lid)));
        if (c.Get("Precedence") is { } prec)
            items.Add(new CheckItem("Versandart", prec.Trim().ToLowerInvariant() switch
            {
                "bulk" => "Massenversand (Newsletter/Werbung)",
                "list" => "Mailingliste",
                "junk" => "als unwichtig gekennzeichnet",
                _ => prec,
            }));
        if (c.Get("Feedback-ID") is { } fb) items.Add(new CheckItem("Feedback-ID", L.F("{0} · Kennung für Beschwerde-Auswertungen beim Versanddienst", fb)));

        foreach (var n in new[] { "Return-Path", "Delivered-To", "X-Original-To", "Envelope-To" })
            if (c.Get(n) is { } v) items.Add(new CheckItem(n, L.F("{0} · {1}", v, L.T(Explanations.GetValueOrDefault(n, "")))));

        if (c.Get("X-MS-Exchange-Organization-AuthAs") is { } authAs)
            items.Add(new CheckItem("Microsoft: Absender angemeldet?", authAs.Trim() switch
            {
                "Internal" => "Intern – von einem Postfach der eigenen Organisation",
                "Anonymous" => "Extern – von außerhalb der Organisation",
                "Partner" => "Partnerorganisation",
                _ => authAs,
            }));
        if (c.Get("X-MS-Exchange-Organization-MessageDirectionality") is { } dir)
            items.Add(new CheckItem("Microsoft: Richtung", dir.Trim() switch
            {
                "Incoming" => "eingehend (von außen)",
                "Originating" => "ausgehend (aus der eigenen Organisation)",
                _ => dir,
            }));
        if (c.Get("X-MS-Exchange-CrossTenant-Id") is { } tenant)
            items.Add(new CheckItem("Microsoft-365-Mandant", L.F("{0} · Kennung der Microsoft-365-Organisation", tenant)));
        if (c.Get("X-Originating-IP") is { } oip)
            items.Add(new CheckItem("X-Originating-IP", oip + GeoText(c.OriginatingGeo) + " · " + L.T("IP-Adresse des Geräts, von dem aus geschrieben wurde")));
        if (c.Get("Organization") is { } org) items.Add(new CheckItem("Organisation", Decode(org)));
        if (c.Get("Content-Language") is { } cl) items.Add(new CheckItem("Sprache", cl));
        if (c.Get("MIME-Version") is { } mv) items.Add(new CheckItem("MIME-Version", mv));
        return new CheckSection("Technische Details", items);
    }

    // ------------------------------------------------------------------ alle Header

    /// <summary>Kurze Erklärungen häufiger Header-Felder.</summary>
    internal static readonly Dictionary<string, string> Explanations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Received"] = "Eine Station auf dem Zustellweg – jeder Mailserver fügt oben eine Zeile hinzu.",
        ["From"] = "Sichtbarer Absender – kann vom Absender frei eingetragen werden.",
        ["To"] = "Empfänger",
        ["Cc"] = "Empfänger in Kopie",
        ["Bcc"] = "Blindkopie – normalerweise nicht sichtbar",
        ["Subject"] = "Betreff",
        ["Date"] = "Zeitpunkt, den das Mail-Programm des Absenders eingetragen hat",
        ["Reply-To"] = "Adresse, an die Antworten gehen",
        ["Sender"] = "Tatsächlicher Absender, wenn im Auftrag eines anderen gesendet wird",
        ["Return-Path"] = "Adresse für Rückläufer (Unzustellbarkeitsmeldungen) – der „Umschlag-Absender“",
        ["Delivered-To"] = "Postfach, in das die Nachricht zugestellt wurde",
        ["X-Original-To"] = "Ursprüngliche Empfängeradresse vor Weiterleitungen/Aliasen",
        ["Envelope-To"] = "Empfänger laut „Umschlag“ der Zustellung",
        ["Message-ID"] = "Weltweit eindeutige Kennung der Nachricht",
        ["In-Reply-To"] = "Kennung der Nachricht, auf die geantwortet wird",
        ["References"] = "Kennungen der früheren Nachrichten im Gesprächsverlauf",
        ["MIME-Version"] = "Version des Formats für Anhänge und Sonderzeichen",
        ["Content-Type"] = "Art des Inhalts (Text, HTML, Anhänge)",
        ["Content-Transfer-Encoding"] = "Wie der Inhalt für den Transport kodiert ist",
        ["Content-Language"] = "Sprache des Inhalts",
        ["Authentication-Results"] = "Prüfergebnisse des Empfangsservers (SPF, DKIM, DMARC)",
        ["ARC-Authentication-Results"] = "Prüfergebnisse vor einer Weiterleitung (ARC)",
        ["ARC-Seal"] = "Siegel der Weiterleitungskette (ARC)",
        ["ARC-Message-Signature"] = "Signatur eines weiterleitenden Servers (ARC)",
        ["DKIM-Signature"] = "Digitale Signatur der Absenderdomain",
        ["Received-SPF"] = "Ergebnis der SPF-Prüfung",
        ["X-Mailer"] = "Programm, mit dem die Nachricht geschrieben wurde",
        ["User-Agent"] = "Programm, mit dem die Nachricht geschrieben wurde",
        ["X-Originating-IP"] = "IP-Adresse des Geräts, von dem aus geschrieben wurde",
        ["List-Unsubscribe"] = "Link oder Adresse zum Abmelden vom Newsletter",
        ["List-Unsubscribe-Post"] = "Ein-Klick-Abmeldung wird unterstützt",
        ["List-Id"] = "Kennung der Mailingliste",
        ["Precedence"] = "Versandart (bulk = Massenversand)",
        ["Auto-Submitted"] = "Automatisch erzeugte Nachricht (z. B. Abwesenheitsnotiz)",
        ["X-Auto-Response-Suppress"] = "Bittet darum, keine automatischen Antworten zu senden",
        ["Feedback-ID"] = "Kennung für Beschwerde-Auswertungen des Versanddienstes",
        ["X-Priority"] = "Wichtigkeit (1 = hoch, 3 = normal, 5 = niedrig)",
        ["Importance"] = "Wichtigkeit",
        ["Disposition-Notification-To"] = "Lesebestätigung angefordert",
        ["Thread-Topic"] = "Thema des Gesprächsverlaufs (Outlook)",
        ["Thread-Index"] = "Position im Gesprächsverlauf (Outlook)",
        ["Accept-Language"] = "Sprache des Absenders",
        ["X-MS-Has-Attach"] = "Hat Anhänge (Outlook)",
        ["X-MS-TNEF-Correlator"] = "Outlook-internes Format für Sonderfunktionen",
        ["X-MS-Exchange-Organization-SCL"] = "Spam-Stufe von Microsoft (−1 bis 9)",
        ["X-Forefront-Antispam-Report"] = "Spamfilter-Bericht von Microsoft 365",
        ["X-Microsoft-Antispam"] = "Spam- und Massenmail-Bewertung von Microsoft",
        ["X-Microsoft-Antispam-Message-Info"] = "Interne Spamfilter-Daten von Microsoft",
        ["X-MS-Exchange-CrossTenant-Id"] = "Kennung der Microsoft-365-Organisation",
        ["X-MS-Exchange-Organization-AuthAs"] = "Ob der Absender bei Microsoft 365 angemeldet war",
        ["X-Gm-Message-State"] = "Interne Kennung von Gmail",
        ["X-Google-Smtp-Source"] = "Interne Kennung von Google-Mailservern",
        ["X-Received"] = "Interne Zustellstation (z. B. bei Google)",
        ["X-Spam-Status"] = "Ergebnis des Spamfilters (SpamAssassin)",
        ["X-Spam-Flag"] = "Spam-Markierung",
        ["X-Spam-Score"] = "Spam-Punktezahl",
        ["X-Spam-Level"] = "Spam-Punkte als Sternchen",
        ["X-Virus-Scanned"] = "Virenscanner, der die Nachricht geprüft hat",
        ["Autocrypt"] = "Schlüssel für verschlüsselte Antworten",
        ["Organization"] = "Organisation des Absenders",
    };

    private static CheckSection AllHeaders(Ctx c)
    {
        var items = c.Headers.Select(h =>
        {
            var v = Shorten(Decode(h.Value), 600);
            if (Explanations.TryGetValue(h.Name, out var e)) v = "→ " + L.T(e) + "\n" + v;
            return new CheckItem(h.Name, v);
        });
        return new CheckSection(L.F("Alle Header-Felder ({0})", c.Headers.Count), items, "In der Reihenfolge der Nachricht – die oberste „Received“-Zeile ist die letzte Station.");
    }
}
