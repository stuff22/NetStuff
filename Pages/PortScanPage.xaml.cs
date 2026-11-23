using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class PortRow : Bindable
{
    private string _banner = "";
    public int Port { get; init; }
    public string Service { get; init; }
    public string State { get; init; }
    public string Time { get; init; }
    public string Banner { get => _banner; set => Set(ref _banner, value); }
}

public partial class PortScanPage : ToolPage
{
    private readonly ObservableCollection<PortRow> _rows = new();
    private CancellationTokenSource _cts;

    public PortScanPage()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
        PresetBox_SelectionChanged(null, null);
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host && _cts == null) HostBox.Text = host;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortsBox == null) return;
        switch (PresetBox.SelectedIndex)
        {
            case 0: PortsBox.Text = string.Join(",", PortCatalog.Top100); break;
            case 1: PortsBox.Text = string.Join(",", PortCatalog.Names.Keys.OrderBy(p => p)); break;
            case 2: PortsBox.Text = "1-1024"; break;
            case 3: PortsBox.Text = "1-65535"; break;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        List<int> ports;
        try { ports = PortCatalog.Parse(PortsBox.Text); }
        catch { StatusText.Text = "Ungültige Portangabe."; return; }
        if (ports.Count == 0 || string.IsNullOrWhiteSpace(HostBox.Text)) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _rows.Clear();
        Progress.Value = 0;
        Progress.Maximum = ports.Count;

        int timeout = (int)(TimeoutBox.Value ?? 800);
        int parallel = (int)(ParallelBox.Value ?? 200);
        bool banner = BannerBox.IsChecked == true;
        var sw = Stopwatch.StartNew();
        int done = 0;

        try
        {
            var ip = await NetUtil.ResolveAsync(HostBox.Text, false, ct);
            StatusText.Text = $"Scanne {ip} …";
            using var gate = new SemaphoreSlim(parallel);
            var bannerTasks = new List<Task>();

            var tasks = ports.Select(async port =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var t = Stopwatch.StartNew();
                    bool open = await NetUtil.TcpConnectAsync(ip, port, timeout, ct);
                    if (open)
                    {
                        var row = new PortRow
                        {
                            Port = port,
                            Service = PortCatalog.Name(port),
                            State = "offen",
                            Time = $"{t.ElapsedMilliseconds} ms",
                        };
                        Insert(row);
                        if (banner) lock (bannerTasks) bannerTasks.Add(GrabBannerAsync(ip, port, row, ct));
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    gate.Release();
                    done++;
                    if (done % 25 == 0 || done == ports.Count)
                    {
                        Progress.Value = done;
                        StatusText.Text = $"{done}/{ports.Count} Ports · {_rows.Count} offen";
                    }
                }
            });
            await Task.WhenAll(tasks);
            Task[] bt;
            lock (bannerTasks) bt = bannerTasks.ToArray();
            await Task.WhenAll(bt);
            StatusText.Text = $"Fertig: {_rows.Count} offene von {ports.Count} Ports auf {ip} in {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Abgebrochen – {_rows.Count} offene Ports gefunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Insert(PortRow row)
    {
        int i = 0;
        while (i < _rows.Count && _rows[i].Port < row.Port) i++;
        _rows.Insert(i, row);
    }

    private static readonly int[] HttpPorts = { 80, 81, 591, 3000, 5000, 7547, 8000, 8008, 8080, 8081, 8083, 8088, 8123, 8888, 9000, 9090, 10000, 32400 };
    private static readonly int[] TlsPorts = { 443, 465, 636, 853, 990, 993, 995, 4433, 5001, 5986, 6443, 8006, 8443, 9443 };

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
                var ssl = new System.Net.Security.SslStream(stream, false, (_, _, _, _) => true);
                await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = ip.ToString() }, cts.Token);
                var cert = ssl.RemoteCertificate as System.Security.Cryptography.X509Certificates.X509Certificate2
                           ?? (ssl.RemoteCertificate != null ? new System.Security.Cryptography.X509Certificates.X509Certificate2(ssl.RemoteCertificate) : null);
                var info = TlsInfo.ProtoName(ssl.SslProtocol) + (cert != null ? " · " + cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false) : "");
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
            var banner = await ReadSome(stream, 1500, cts.Token);
            return banner;
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

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
