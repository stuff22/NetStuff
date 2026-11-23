using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

public class PortRow : Bindable
{
    private string _banner = "";
    public int Port { get; init; }
    public string Service { get; init; }
    public string State { get; init; }
    public string Time { get; init; }
    public string Banner { get => _banner; set => Set(ref _banner, value); }
}

public class PortScanSettings
{
    public int TimeoutMs { get; set; } = 800;
    public int Parallel { get; set; } = 200;
    public bool GrabBanners { get; set; } = true;
}

/// <summary>TCP-Connect-Scan mit Dienstnamen und Banner-Abfrage (SSH, SMTP, HTTP-HEAD, TLS-Zertifikat …).</summary>
public static class PortScanner
{
    /// <summary>
    /// Scannt die Ports. <paramref name="found"/> wird für jeden offenen Port aufgerufen (Banner folgen später über PropertyChanged),
    /// <paramref name="progress"/> mit der Anzahl geprüfter Ports. Rückgabe: alle offenen Ports, sortiert.
    /// </summary>
    public static async Task<List<PortRow>> ScanAsync(IPAddress ip, IReadOnlyList<int> ports, PortScanSettings s,
        Action<PortRow> found, Action<int> progress, CancellationToken ct)
    {
        var open = new List<PortRow>();
        using var gate = new SemaphoreSlim(Math.Max(1, s.Parallel));
        var bannerTasks = new List<Task>();
        int done = 0;

        var tasks = ports.Select(async port =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var t = Stopwatch.StartNew();
                if (await NetUtil.TcpConnectAsync(ip, port, s.TimeoutMs, ct))
                {
                    var row = new PortRow { Port = port, Service = PortCatalog.Name(port), State = L.T("offen"), Time = $"{t.ElapsedMilliseconds} ms" };
                    lock (open) open.Add(row);
                    found?.Invoke(row);
                    if (s.GrabBanners) lock (bannerTasks) bannerTasks.Add(GrabBannerAsync(ip, port, row, ct));
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                gate.Release();
                int d = Interlocked.Increment(ref done);
                if (d % 25 == 0 || d == ports.Count) progress?.Invoke(d);
            }
        });
        await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        Task[] bt;
        lock (bannerTasks) bt = bannerTasks.ToArray();
        await Task.WhenAll(bt);
        lock (open) return open.OrderBy(r => r.Port).ToList();
    }

    private static readonly int[] HttpPorts = { 80, 81, 591, 3000, 5000, 7547, 8000, 8008, 8080, 8081, 8083, 8088, 8123, 8888, 9000, 9090, 10000, 32400 };
    private static readonly int[] TlsPorts = { 443, 465, 636, 853, 990, 993, 995, 4433, 5001, 5986, 6443, 8006, 8443, 9443 };

    /// <summary>Banner/Kurzinfo eines Dienstes (leer, wenn nichts zu erfahren ist).</summary>
    public static async Task<string> GetBannerAsync(IPAddress ip, int port, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(4000);
            using var client = new TcpClient(ip.AddressFamily);
            await client.ConnectAsync(ip, port, cts.Token);
            Stream stream = client.GetStream();

            if (TlsPorts.Contains(port))
            {
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = ip.ToString() }, cts.Token);
                var cert = ssl.RemoteCertificate as X509Certificate2
                           ?? (ssl.RemoteCertificate != null ? new X509Certificate2(ssl.RemoteCertificate) : null);
                var info = TlsInfo.ProtoName(ssl.SslProtocol) + (cert != null ? " · " + cert.GetNameInfo(X509NameType.SimpleName, false) : "");
                if (port is 443 or 4433 or 5001 or 8006 or 8443 or 9443)
                {
                    var head = await HttpHead(ssl, ip.ToString(), cts.Token);
                    return head == "" ? info : info + " · " + head;
                }
                var text = await ReadSome(ssl, 1500, cts.Token);
                return text == "" ? info : info + " · " + text;
            }

            if (HttpPorts.Contains(port))
                return await HttpHead(stream, ip.ToString(), cts.Token);

            // Viele Dienste senden zuerst (SSH, FTP, SMTP, POP3, IMAP, MySQL …)
            return await ReadSome(stream, 1500, cts.Token);
        }
        catch { return ""; }
    }

    private static async Task GrabBannerAsync(IPAddress ip, int port, PortRow row, CancellationToken ct)
    {
        var b = await GetBannerAsync(ip, port, ct);
        if (b != "") row.Banner = b;
    }

    private static async Task<string> HttpHead(Stream s, string host, CancellationToken ct)
    {
        var req = Encoding.ASCII.GetBytes($"HEAD / HTTP/1.0\r\nHost: {host}\r\nUser-Agent: Net-Stuff\r\n\r\n");
        await s.WriteAsync(req, ct);
        var text = await ReadSome(s, 2500, ct, raw: true);
        if (text == "") return "";
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var status = lines.FirstOrDefault() ?? "";
        var server = lines.FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
        return Clean(status + (server != null ? " · " + server : ""));
    }

    private static async Task<string> ReadSome(Stream s, int waitMs, CancellationToken ct, bool raw = false)
    {
        var buf = new byte[1024];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(waitMs);
        try
        {
            int n = await s.ReadAsync(buf, cts.Token);
            if (n <= 0) return "";
            var text = Encoding.UTF8.GetString(buf, 0, n);
            return raw ? text : Clean(text);
        }
        catch { return ""; }
    }

    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]", "·");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }
}
