using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

public static class TlsInfo
{
    public static string ProtoName(SslProtocols p) => p switch
    {
#pragma warning disable SYSLIB0039
        SslProtocols.Tls => "TLS 1.0",
        SslProtocols.Tls11 => "TLS 1.1",
#pragma warning restore SYSLIB0039
        SslProtocols.Tls12 => "TLS 1.2",
        SslProtocols.Tls13 => "TLS 1.3",
        _ => p.ToString(),
    };
}

/* ====================================================================== SSL/TLS */

/// <summary>Prüft Zertifikat, Kette, ausgehandelte Verbindung, unterstützte TLS-Versionen und HSTS.</summary>
public static class SslChecker
{
    private class Handshake
    {
        public SslProtocols Protocol;
        public TlsCipherSuite Cipher;
        public string Alpn;
        public X509Certificate2 Leaf;
        public List<X509Certificate2> Chain = new();
        public SslPolicyErrors Errors;
        public List<string> ChainStatus = new();
    }

    private static async Task<Handshake> ConnectAsync(string host, int port, string sni, SslProtocols protocols, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        var hs = new Handshake();
        using var ssl = new SslStream(tcp.GetStream(), false);
        var opts = new SslClientAuthenticationOptions
        {
            TargetHost = sni,
            EnabledSslProtocols = protocols,
            ApplicationProtocols = port == 443 ? new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 } : null,
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
            {
                hs.Errors = errors;
                if (cert != null) hs.Leaf = new X509Certificate2(cert);
                if (chain != null)
                {
                    foreach (var el in chain.ChainElements) hs.Chain.Add(new X509Certificate2(el.Certificate));
                    hs.ChainStatus.AddRange(chain.ChainStatus.Where(s => s.Status != X509ChainStatusFlags.NoError).Select(s => s.StatusInformation.Trim()));
                }
                return true;
            },
        };
        await ssl.AuthenticateAsClientAsync(opts, ct);
        hs.Protocol = ssl.SslProtocol;
        hs.Cipher = ssl.NegotiatedCipherSuite;
        hs.Alpn = ssl.NegotiatedApplicationProtocol.ToString();
        return hs;
    }

    /// <summary>Führt die Prüfung aus. Verbindungsfehler werden als Exception gemeldet.</summary>
    public static async Task CheckAsync(string host, int port, string sni, ICheckSink sink, CancellationToken ct = default)
    {
        sni = string.IsNullOrWhiteSpace(sni) ? host : sni.Trim();
        sink.Status("Verbindung wird aufgebaut …");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(20000);
        var hs = await ConnectAsync(host, port, sni, SslProtocols.None, cts.Token);
        var c = hs.Leaf;

        // Zertifikat
        var cert = new List<CheckItem>();
        var days = (c.NotAfter - DateTime.Now).TotalDays;
        bool nameOk = c.MatchesHostname(sni);
        cert.Add(new CheckItem("Ausgestellt für", c.GetNameInfo(X509NameType.SimpleName, false)));
        var san = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san != null)
        {
            var names = san.EnumerateDnsNames().ToList();
            cert.Add(new CheckItem("Alternative Namen (SAN)", names.Count > 12 ? string.Join(", ", names.Take(12)) + $" … (+{names.Count - 12})" : string.Join(", ", names)));
        }
        cert.Add(new CheckItem("Hostname passt", nameOk ? L.F("ja ({0})", sni) : L.F("NEIN – Zertifikat gilt nicht für {0}", sni), nameOk ? Level.Ok : Level.Bad));
        cert.Add(new CheckItem("Aussteller", c.GetNameInfo(X509NameType.SimpleName, true) + "  ·  " + c.Issuer));
        cert.Add(new CheckItem("Gültig ab", c.NotBefore.ToString("dd.MM.yyyy HH:mm"), c.NotBefore > DateTime.Now ? Level.Bad : Level.Info));
        cert.Add(new CheckItem("Gültig bis", $"{c.NotAfter:dd.MM.yyyy HH:mm}  ({(days >= 0 ? L.F("noch {0:0} Tage", days) : L.F("seit {0:0} Tagen abgelaufen", -days))})",
            days < 0 ? Level.Bad : days < 14 ? Level.Warn : Level.Ok));
        var keyAlg = c.GetKeyAlgorithm() switch { "1.2.840.113549.1.1.1" => "RSA", "1.2.840.10045.2.1" => "ECDSA", "1.3.101.112" => "Ed25519", var o => o };
        int keySize = c.GetRSAPublicKey()?.KeySize ?? c.GetECDsaPublicKey()?.KeySize ?? 0;
        cert.Add(new CheckItem("Schlüssel", $"{keyAlg} {keySize} Bit", keyAlg == "RSA" && keySize < 2048 ? Level.Bad : Level.Ok));
        cert.Add(new CheckItem("Signatur", c.SignatureAlgorithm.FriendlyName, c.SignatureAlgorithm.FriendlyName?.Contains("sha1", StringComparison.OrdinalIgnoreCase) == true ? Level.Bad : Level.Info));
        cert.Add(new CheckItem("Seriennummer", c.SerialNumber));
        cert.Add(new CheckItem("SHA-256-Fingerabdruck", c.GetCertHashString(HashAlgorithmName.SHA256)));
        var validity = (c.NotAfter - c.NotBefore).TotalDays;
        cert.Add(new CheckItem("Laufzeit", L.F("{0:0} Tage", validity) + (validity <= 100 ? L.T(" (automatisierte Erneuerung, z. B. Let's Encrypt)") : "")));
        sink.Add(new CheckSection("Zertifikat", cert));

        // Vertrauen
        var trust = new List<CheckItem>();
        if (hs.Errors == SslPolicyErrors.None) trust.Add(new CheckItem("Vertrauenswürdig", "Zertifikat und Kette sind gültig.", Level.Ok));
        else
        {
            if (hs.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) trust.Add(new CheckItem("Namensfehler", "Hostname stimmt nicht überein", Level.Bad));
            if (hs.Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                trust.Add(new CheckItem("Kettenfehler", hs.ChainStatus.Count > 0 ? string.Join("; ", hs.ChainStatus.Distinct()) : "Kette nicht vertrauenswürdig", Level.Bad));
        }
        sink.Add(new CheckSection("Vertrauen", trust));

        // Kette
        var chain = new List<CheckItem>();
        for (int i = 0; i < hs.Chain.Count; i++)
        {
            var cc = hs.Chain[i];
            var role = i == 0 ? "Server" : i == hs.Chain.Count - 1 ? "Root" : "Zwischenzertifikat";
            var exp = (cc.NotAfter - DateTime.Now).TotalDays;
            chain.Add(new CheckItem($"{i + 1}. {L.T(role)}", $"{cc.GetNameInfo(X509NameType.SimpleName, false)}  ·  {L.T("bis")} {cc.NotAfter:dd.MM.yyyy}", exp < 0 ? Level.Bad : exp < 30 ? Level.Warn : Level.Ok));
        }
        sink.Add(new CheckSection("Zertifikatskette", chain));

        // Verbindung
        var cipher = hs.Cipher.ToString();
        var conn = new List<CheckItem>
        {
            new("Ausgehandelt", TlsInfo.ProtoName(hs.Protocol), hs.Protocol >= SslProtocols.Tls12 ? Level.Ok : Level.Bad),
            new("Cipher-Suite", cipher, cipher.Contains("CBC") || cipher.Contains("RC4") || cipher.Contains("3DES") ? Level.Warn : Level.Ok),
        };
        if (!string.IsNullOrEmpty(hs.Alpn)) conn.Add(new CheckItem("ALPN", hs.Alpn == "h2" ? "HTTP/2 (h2)" : hs.Alpn));
        sink.Add(new CheckSection("Verbindung", conn));

        // Protokollversionen
        sink.Status("Protokollversionen werden getestet …");
        var protos = new List<CheckItem>();
#pragma warning disable SYSLIB0039
        foreach (var p in new[] { SslProtocols.Tls13, SslProtocols.Tls12, SslProtocols.Tls11, SslProtocols.Tls })
#pragma warning restore SYSLIB0039
        {
            bool ok;
            try
            {
                using var pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pcts.CancelAfter(8000);
                await ConnectAsync(host, port, sni, p, pcts.Token);
                ok = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { ok = false; }
            bool legacy = p < SslProtocols.Tls12;
            protos.Add(new CheckItem(TlsInfo.ProtoName(p),
                ok ? (legacy ? "unterstützt – veraltet, sollte deaktiviert werden" : "unterstützt") : (legacy ? "nicht unterstützt ✓" : "nicht unterstützt"),
                ok ? (legacy ? Level.Bad : Level.Ok) : (legacy ? Level.Ok : p == SslProtocols.Tls13 ? Level.Warn : Level.Info)));
        }
        sink.Add(new CheckSection("Protokollversionen", protos,
            "Hinweis: Das Betriebssystem kann TLS 1.0/1.1 clientseitig deaktiviert haben – dann wird „nicht unterstützt“ angezeigt."));

        // HSTS
        if (port == 443)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
                using var hcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                hcts.CancelAfter(8000);
                using var res = await NetUtil.Http.SendAsync(req, hcts.Token);
                var hsts = res.Headers.TryGetValues("Strict-Transport-Security", out var v) ? string.Join(", ", v) : null;
                sink.Add(new CheckSection("HTTP Strict Transport Security", new List<CheckItem>
                {
                    new("HSTS", hsts ?? "nicht gesetzt", hsts != null ? Level.Ok : Level.Warn),
                }));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
    }
}

/* ====================================================================== HTTP-Header */

public class HttpHeaderOptions
{
    public bool Head { get; set; }
    public bool FollowRedirects { get; set; } = true;
    /// <summary>0 = Net-Stuff (Desktop-Browser), 1 = iPhone, 2 = Googlebot, 3 = curl.</summary>
    public int UserAgentIndex { get; set; }

    public string UserAgent => UserAgentIndex switch
    {
        1 => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        2 => "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        3 => "curl/8.10.1",
        _ => NetUtil.UserAgent,
    };
}

/// <summary>Weiterleitungskette, Sicherheits-Header (mit Note), Cookies, Protokoll/Leistung und alle Response-Header.</summary>
public static class HttpHeaderChecker
{
    /// <summary>Eingabe zu URL ergänzen (https:// falls kein Schema). null bei ungültiger Eingabe.</summary>
    public static Uri ParseUrl(string text)
    {
        var url = (text ?? "").Trim();
        if (url == "") return null;
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    public static async Task CheckAsync(Uri uri, HttpHeaderOptions o, ICheckSink sink, CancellationToken ct = default)
    {
        var method = o.Head ? HttpMethod.Head : HttpMethod.Get;
        SslPolicyErrors certErrors = SslPolicyErrors.None;

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, // sonst entfernt .NET den Content-Encoding-Header
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, err) => { certErrors |= err; return true; },
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };

        sink.Status("Anfrage läuft …");
        var chain = new List<CheckItem>();
        HttpResponseMessage final = null;
        long bodyBytes = -1;
        var total = Stopwatch.StartNew();
        for (int hop = 0; hop < 12; hop++)
        {
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
            req.Headers.UserAgent.ParseAdd(o.UserAgent);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
            req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
            var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)res.StatusCode;
            var level = code < 300 ? Level.Ok : code < 400 ? Level.Info : Level.Bad;
            chain.Add(new CheckItem($"{code} {res.ReasonPhrase}", $"{uri}  ·  HTTP/{res.Version}  ·  {sw.ElapsedMilliseconds} ms", level));

            if (code is >= 300 and < 400 && res.Headers.Location != null && o.FollowRedirects)
            {
                uri = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(uri, res.Headers.Location);
                res.Dispose();
                continue;
            }
            final = res;
            if (method == HttpMethod.Get) bodyBytes = (await res.Content.ReadAsByteArrayAsync(ct)).Length;
            break;
        }
        if (final == null) throw new Exception(L.T("Zu viele Weiterleitungen."));
        using var _ = final;
        sink.Add(new CheckSection("Weiterleitungskette", chain,
            L.F("Gesamtzeit {0} ms", total.ElapsedMilliseconds) + (bodyBytes >= 0 ? L.F(" · Inhalt {0} übertragen", NetUtil.FormatBytes(bodyBytes)) : "")));

        var headers = final.Headers.Concat(final.Content.Headers)
            .Select(h => (Name: h.Key, Value: string.Join(", ", h.Value))).ToList();
        string H(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        bool https = uri.Scheme == "https";

        // Sicherheit
        var sec = new List<CheckItem>();
        var hsts = H("Strict-Transport-Security");
        if (!https) sec.Add(new CheckItem("HTTPS", "Seite wird unverschlüsselt ausgeliefert!", Level.Bad));
        else if (certErrors != SslPolicyErrors.None) sec.Add(new CheckItem("Zertifikat", L.T("Fehler: ") + certErrors, Level.Bad));
        if (https)
        {
            var maxAge = hsts != null ? Regex.Match(hsts, @"max-age=(\d+)").Groups[1].Value : "";
            sec.Add(hsts == null ? new CheckItem("Strict-Transport-Security", "fehlt", Level.Bad)
                : long.TryParse(maxAge, out var ma) && ma >= 15552000 ? new CheckItem("Strict-Transport-Security", hsts, Level.Ok)
                : new CheckItem("Strict-Transport-Security", hsts + L.T("  (max-age sollte ≥ 6 Monate sein)"), Level.Warn));
        }
        var csp = H("Content-Security-Policy");
        sec.Add(csp == null ? new CheckItem("Content-Security-Policy", "fehlt – schützt vor XSS und Daten-Injektion", Level.Warn)
            : csp.Contains("unsafe-inline") || csp.Contains("unsafe-eval") ? new CheckItem("Content-Security-Policy", Short(csp) + L.T("  (enthält unsafe-inline/eval)"), Level.Warn)
            : new CheckItem("Content-Security-Policy", Short(csp), Level.Ok));
        var xfo = H("X-Frame-Options");
        bool frameAncestors = csp?.Contains("frame-ancestors") == true;
        sec.Add(xfo != null || frameAncestors ? new CheckItem("Clickjacking-Schutz", xfo ?? "CSP frame-ancestors", Level.Ok)
            : new CheckItem("Clickjacking-Schutz", "X-Frame-Options / frame-ancestors fehlt", Level.Warn));
        var xcto = H("X-Content-Type-Options");
        sec.Add(xcto?.Contains("nosniff", StringComparison.OrdinalIgnoreCase) == true ? new CheckItem("X-Content-Type-Options", xcto, Level.Ok) : new CheckItem("X-Content-Type-Options", "fehlt (nosniff empfohlen)", Level.Warn));
        var rp = H("Referrer-Policy");
        sec.Add(rp != null ? new CheckItem("Referrer-Policy", rp, Level.Ok) : new CheckItem("Referrer-Policy", "fehlt", Level.Warn));
        var pp = H("Permissions-Policy");
        sec.Add(pp != null ? new CheckItem("Permissions-Policy", Short(pp), Level.Ok) : new CheckItem("Permissions-Policy", "fehlt (optional)", Level.Info));
        var coop = H("Cross-Origin-Opener-Policy");
        if (coop != null) sec.Add(new CheckItem("Cross-Origin-Opener-Policy", coop, Level.Ok));
        var server = H("Server");
        if (server != null && Regex.IsMatch(server, @"\d")) sec.Add(new CheckItem("Server", server + L.T("  (Versionsangabe gibt Angreifern Hinweise)"), Level.Warn));
        var xpb = H("X-Powered-By");
        if (xpb != null) sec.Add(new CheckItem("X-Powered-By", xpb + L.T("  (sollte entfernt werden)"), Level.Warn));
        if (H("X-AspNet-Version") is { } aspv) sec.Add(new CheckItem("X-AspNet-Version", aspv + L.T("  (sollte entfernt werden)"), Level.Warn));

        sink.Add(new CheckSection(L.F("Sicherheits-Header – Note {0}", Grade(sec)), sec));

        // Cookies
        if (final.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            var ck = new List<CheckItem>();
            foreach (var c in cookies)
            {
                var name = c.Split('=')[0];
                bool secure = Regex.IsMatch(c, @";\s*Secure", RegexOptions.IgnoreCase);
                bool httpOnly = Regex.IsMatch(c, @";\s*HttpOnly", RegexOptions.IgnoreCase);
                var same = Regex.Match(c, @";\s*SameSite=(\w+)", RegexOptions.IgnoreCase).Groups[1].Value;
                var flags = new List<string> { secure ? "Secure ✓" : "Secure ✗", httpOnly ? "HttpOnly ✓" : "HttpOnly ✗", same != "" ? "SameSite=" + same : L.T("SameSite fehlt") };
                ck.Add(new CheckItem(name, string.Join(" · ", flags), secure && httpOnly && same != "" ? Level.Ok : secure ? Level.Warn : Level.Bad));
            }
            sink.Add(new CheckSection("Cookies", ck));
        }

        // Leistung
        var perf = new List<CheckItem>
        {
            new("HTTP-Version", "HTTP/" + final.Version, final.Version >= HttpVersion.Version20 ? Level.Ok : Level.Info),
            new("Komprimierung", H("Content-Encoding") ?? "keine", H("Content-Encoding") != null ? Level.Ok : Level.Warn),
            new("Caching", H("Cache-Control") ?? "kein Cache-Control"),
        };
        if (H("Alt-Svc") is { } alt && alt.Contains("h3")) perf.Add(new CheckItem("HTTP/3", "angeboten (Alt-Svc)", Level.Ok));
        if (H("Age") is { } age) perf.Add(new CheckItem("Age (CDN-Cache)", age + " s"));
        sink.Add(new CheckSection("Protokoll & Leistung", perf));

        // Alle Header
        sink.Add(new CheckSection(L.F("Alle Response-Header ({0})", headers.Count), headers.OrderBy(h => h.Name).Select(h => new CheckItem(h.Name, h.Value))));
    }

    /// <summary>Schulnote A–F aus den Sicherheits-Prüfzeilen.</summary>
    public static string Grade(IEnumerable<CheckItem> sec)
    {
        var list = sec.ToList();
        int score = list.Count(i => i.Level == Level.Ok) * 2 - list.Count(i => i.Level == Level.Bad) * 3 - list.Count(i => i.Level == Level.Warn);
        return score >= 10 ? "A" : score >= 7 ? "B" : score >= 4 ? "C" : score >= 1 ? "D" : "F";
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + " …" : s;
}
