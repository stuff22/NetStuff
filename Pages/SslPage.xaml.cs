using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SslPage : ToolPage
{
    private readonly StringBuilder _text = new();

    public SslPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) HostBox.Text = s;
    }

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

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

    private void AddSection(string title, List<CheckItem> items)
    {
        ResultsPanel.Children.Add(UiFactory.Section(title, items));
        _text.Append(UiFactory.ToText(title, items));
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var host = NetUtil.CleanHost(HostBox.Text);
        if (host == "") return;
        int port = (int)(PortBox.Value ?? 443);
        var sni = string.IsNullOrWhiteSpace(SniBox.Text) ? host : SniBox.Text.Trim();
        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        _text.Clear();
        ResultsPanel.Children.Add(UiFactory.Message("Verbindung wird aufgebaut …"));

        try
        {
            using var cts = new CancellationTokenSource(20000);
            var hs = await ConnectAsync(host, port, sni, SslProtocols.None, cts.Token);
            ResultsPanel.Children.Clear();
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
                cert.Add(new CheckItem("Alternative Namen (SAN)", (names.Count > 12 ? string.Join(", ", names.Take(12)) + $" … (+{names.Count - 12})" : string.Join(", ", names))));
            }
            cert.Add(new CheckItem("Hostname passt", nameOk ? $"ja ({sni})" : $"NEIN – Zertifikat gilt nicht für {sni}", nameOk ? Level.Ok : Level.Bad));
            cert.Add(new CheckItem("Aussteller", c.GetNameInfo(X509NameType.SimpleName, true) + "  ·  " + c.Issuer));
            cert.Add(new CheckItem("Gültig ab", c.NotBefore.ToString("dd.MM.yyyy HH:mm"), c.NotBefore > DateTime.Now ? Level.Bad : Level.Info));
            cert.Add(new CheckItem("Gültig bis", $"{c.NotAfter:dd.MM.yyyy HH:mm}  ({(days >= 0 ? $"noch {days:0} Tage" : $"seit {-days:0} Tagen abgelaufen")})",
                days < 0 ? Level.Bad : days < 14 ? Level.Warn : Level.Ok));
            var keyAlg = c.GetKeyAlgorithm() switch { "1.2.840.113549.1.1.1" => "RSA", "1.2.840.10045.2.1" => "ECDSA", "1.3.101.112" => "Ed25519", var o => o };
            int keySize = c.GetRSAPublicKey()?.KeySize ?? c.GetECDsaPublicKey()?.KeySize ?? 0;
            cert.Add(new CheckItem("Schlüssel", $"{keyAlg} {keySize} Bit", keyAlg == "RSA" && keySize < 2048 ? Level.Bad : Level.Ok));
            cert.Add(new CheckItem("Signatur", c.SignatureAlgorithm.FriendlyName, c.SignatureAlgorithm.FriendlyName?.Contains("sha1", StringComparison.OrdinalIgnoreCase) == true ? Level.Bad : Level.Info));
            cert.Add(new CheckItem("Seriennummer", c.SerialNumber));
            cert.Add(new CheckItem("SHA-256-Fingerabdruck", c.GetCertHashString(HashAlgorithmName.SHA256)));
            var validity = (c.NotAfter - c.NotBefore).TotalDays;
            cert.Add(new CheckItem("Laufzeit", $"{validity:0} Tage" + (validity <= 100 ? " (automatisierte Erneuerung, z. B. Let's Encrypt)" : "")));
            AddSection("Zertifikat", cert);

            // Vertrauen
            var trust = new List<CheckItem>();
            if (hs.Errors == SslPolicyErrors.None) trust.Add(new CheckItem("Vertrauenswürdig", "Zertifikat und Kette sind gültig.", Level.Ok));
            else
            {
                if (hs.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) trust.Add(new CheckItem("Namensfehler", "Hostname stimmt nicht überein", Level.Bad));
                if (hs.Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
                    trust.Add(new CheckItem("Kettenfehler", hs.ChainStatus.Count > 0 ? string.Join("; ", hs.ChainStatus.Distinct()) : "Kette nicht vertrauenswürdig", Level.Bad));
            }
            AddSection("Vertrauen", trust);

            // Kette
            var chain = new List<CheckItem>();
            for (int i = 0; i < hs.Chain.Count; i++)
            {
                var cc = hs.Chain[i];
                var role = i == 0 ? "Server" : i == hs.Chain.Count - 1 ? "Root" : "Zwischenzertifikat";
                var exp = (cc.NotAfter - DateTime.Now).TotalDays;
                chain.Add(new CheckItem($"{i + 1}. {role}", $"{cc.GetNameInfo(X509NameType.SimpleName, false)}  ·  bis {cc.NotAfter:dd.MM.yyyy}", exp < 0 ? Level.Bad : exp < 30 ? Level.Warn : Level.Ok));
            }
            AddSection("Zertifikatskette", chain);

            // Verbindung
            var conn = new List<CheckItem>
            {
                new("Ausgehandelt", ProtoName(hs.Protocol), hs.Protocol >= SslProtocols.Tls12 ? Level.Ok : Level.Bad),
                new("Cipher-Suite", hs.Cipher.ToString(), hs.Cipher.ToString().Contains("CBC") || hs.Cipher.ToString().Contains("RC4") || hs.Cipher.ToString().Contains("3DES") ? Level.Warn : Level.Ok),
            };
            if (!string.IsNullOrEmpty(hs.Alpn)) conn.Add(new CheckItem("ALPN", hs.Alpn == "h2" ? "HTTP/2 (h2)" : hs.Alpn));
            AddSection("Verbindung", conn);

            // Protokolle
            ResultsPanel.Children.Add(UiFactory.Message("Protokollversionen werden getestet …"));
            var protos = new List<CheckItem>();
#pragma warning disable SYSLIB0039
            foreach (var p in new[] { SslProtocols.Tls13, SslProtocols.Tls12, SslProtocols.Tls11, SslProtocols.Tls })
#pragma warning restore SYSLIB0039
            {
                bool ok;
                try
                {
                    using var pcts = new CancellationTokenSource(8000);
                    await ConnectAsync(host, port, sni, p, pcts.Token);
                    ok = true;
                }
                catch { ok = false; }
                bool legacy = p < SslProtocols.Tls12;
                protos.Add(new CheckItem(ProtoName(p),
                    ok ? (legacy ? "unterstützt – veraltet, sollte deaktiviert werden" : "unterstützt") : (legacy ? "nicht unterstützt ✓" : "nicht unterstützt"),
                    ok ? (legacy ? Level.Bad : Level.Ok) : (legacy ? Level.Ok : p == SslProtocols.Tls13 ? Level.Warn : Level.Info)));
            }
            ResultsPanel.Children.RemoveAt(ResultsPanel.Children.Count - 1);
            AddSection("Protokollversionen", protos, "Hinweis: Windows 11 kann TLS 1.0/1.1 clientseitig deaktiviert haben – dann wird „nicht unterstützt“ angezeigt.");

            // HSTS
            if (port == 443)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Head, $"https://{host}/");
                    using var hcts = new CancellationTokenSource(8000);
                    using var res = await NetUtil.Http.SendAsync(req, hcts.Token);
                    var hsts = res.Headers.TryGetValues("Strict-Transport-Security", out var v) ? string.Join(", ", v) : null;
                    AddSection("HTTP Strict Transport Security", new List<CheckItem>
                    {
                        new("HSTS", hsts ?? "nicht gesetzt", hsts != null ? Level.Ok : Level.Warn),
                    });
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Clear();
            ResultsPanel.Children.Add(UiFactory.Message("TLS-Verbindung fehlgeschlagen: " + (ex.InnerException?.Message ?? ex.Message), Level.Bad));
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void AddSection(string title, List<CheckItem> items, string subtitle)
    {
        ResultsPanel.Children.Add(UiFactory.Section(title, items, subtitle));
        _text.Append(UiFactory.ToText(title, items));
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_text.ToString());
}
