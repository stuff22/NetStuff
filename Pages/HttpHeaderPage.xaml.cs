using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class HttpHeaderPage : ToolPage
{
    private readonly StringBuilder _text = new();

    public HttpHeaderPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) UrlBox.Text = s;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private string UserAgent => UaBox.SelectedIndex switch
    {
        1 => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        2 => "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)",
        3 => "curl/8.10.1",
        _ => NetUtil.UserAgent,
    };

    private void AddSection(string title, List<CheckItem> items, string subtitle = null)
    {
        ResultsPanel.Children.Add(UiFactory.Section(title, items, subtitle));
        _text.Append(UiFactory.ToText(title, items));
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url == "") return;
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) { ResultsPanel.Children.Clear(); ResultsPanel.Children.Add(UiFactory.Message("Ungültige URL", Level.Bad)); return; }

        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        _text.Clear();
        var method = MethodBox.SelectedIndex == 1 ? HttpMethod.Head : HttpMethod.Get;
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

        try
        {
            var chain = new List<CheckItem>();
            HttpResponseMessage final = null;
            string body = "";
            var total = Stopwatch.StartNew();
            for (int hop = 0; hop < 12; hop++)
            {
                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(method, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
                req.Headers.UserAgent.ParseAdd(UserAgent);
                req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,*/*;q=0.8");
                req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
                var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                int code = (int)res.StatusCode;
                var level = code < 300 ? Level.Ok : code < 400 ? Level.Info : Level.Bad;
                chain.Add(new CheckItem($"{code} {res.ReasonPhrase}", $"{uri}  ·  HTTP/{res.Version}  ·  {sw.ElapsedMilliseconds} ms", level));

                if (code is >= 300 and < 400 && res.Headers.Location != null && FollowBox.IsChecked == true)
                {
                    uri = res.Headers.Location.IsAbsoluteUri ? res.Headers.Location : new Uri(uri, res.Headers.Location);
                    res.Dispose();
                    continue;
                }
                final = res;
                if (method == HttpMethod.Get)
                {
                    var bytes = await res.Content.ReadAsByteArrayAsync();
                    body = bytes.Length.ToString();
                }
                break;
            }
            if (final == null) throw new Exception("Zu viele Weiterleitungen.");
            AddSection("Weiterleitungskette", chain, $"Gesamtzeit {total.ElapsedMilliseconds} ms" + (body != "" ? $" · Inhalt {NetUtil.FormatBytes(long.Parse(body))} übertragen" : ""));

            var headers = final.Headers.Concat(final.Content.Headers)
                .Select(h => (Name: h.Key, Value: string.Join(", ", h.Value))).ToList();
            string H(string name) => headers.FirstOrDefault(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            bool https = uri.Scheme == "https";

            // Sicherheit
            var sec = new List<CheckItem>();
            var hsts = H("Strict-Transport-Security");
            if (!https) sec.Add(new CheckItem("HTTPS", "Seite wird unverschlüsselt ausgeliefert!", Level.Bad));
            else if (certErrors != SslPolicyErrors.None) sec.Add(new CheckItem("Zertifikat", "Fehler: " + certErrors, Level.Bad));
            if (https)
            {
                var maxAge = hsts != null ? Regex.Match(hsts, @"max-age=(\d+)").Groups[1].Value : "";
                sec.Add(hsts == null ? new CheckItem("Strict-Transport-Security", "fehlt", Level.Bad)
                    : long.TryParse(maxAge, out var ma) && ma >= 15552000 ? new CheckItem("Strict-Transport-Security", hsts, Level.Ok)
                    : new CheckItem("Strict-Transport-Security", hsts + "  (max-age sollte ≥ 6 Monate sein)", Level.Warn));
            }
            var csp = H("Content-Security-Policy");
            sec.Add(csp == null ? new CheckItem("Content-Security-Policy", "fehlt – schützt vor XSS und Daten-Injektion", Level.Warn)
                : csp.Contains("unsafe-inline") || csp.Contains("unsafe-eval") ? new CheckItem("Content-Security-Policy", Short(csp) + "  (enthält unsafe-inline/eval)", Level.Warn)
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
            if (server != null && Regex.IsMatch(server, @"\d")) sec.Add(new CheckItem("Server", server + "  (Versionsangabe gibt Angreifern Hinweise)", Level.Warn));
            var xpb = H("X-Powered-By");
            if (xpb != null) sec.Add(new CheckItem("X-Powered-By", xpb + "  (sollte entfernt werden)", Level.Warn));
            if (H("X-AspNet-Version") is { } aspv) sec.Add(new CheckItem("X-AspNet-Version", aspv + "  (sollte entfernt werden)", Level.Warn));

            int score = sec.Count(i => i.Level == Level.Ok) * 2 - sec.Count(i => i.Level == Level.Bad) * 3 - sec.Count(i => i.Level == Level.Warn);
            string grade = score >= 10 ? "A" : score >= 7 ? "B" : score >= 4 ? "C" : score >= 1 ? "D" : "F";
            AddSection($"Sicherheits-Header – Note {grade}", sec);

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
                    var flags = new List<string> { secure ? "Secure ✓" : "Secure ✗", httpOnly ? "HttpOnly ✓" : "HttpOnly ✗", same != "" ? "SameSite=" + same : "SameSite fehlt" };
                    ck.Add(new CheckItem(name, string.Join(" · ", flags), secure && httpOnly && same != "" ? Level.Ok : secure ? Level.Warn : Level.Bad));
                }
                AddSection("Cookies", ck);
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
            AddSection("Protokoll & Leistung", perf);

            // Alle Header
            AddSection($"Alle Response-Header ({headers.Count})", headers.OrderBy(h => h.Name).Select(h => new CheckItem(h.Name, h.Value)).ToList());
            final.Dispose();
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message("Anfrage fehlgeschlagen: " + (ex.InnerException?.Message ?? ex.Message), Level.Bad));
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + " …" : s;

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_text.ToString());
}
