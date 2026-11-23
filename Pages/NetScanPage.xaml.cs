using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class ScanHost : Bindable
{
    private string _hostname = "", _mac = "", _vendor = "", _type = "", _ports = "", _info = "";
    private long? _ping;

    public IPAddress Address { get; init; }
    public string Ip => Address.ToString();
    public uint SortKey => NetUtil.ToUInt(Address);
    public bool IsSelf { get; set; }
    public bool IsGateway { get; set; }
    public List<int> OpenPorts { get; } = new();
    public SsdpDevice Ssdp { get; set; }
    public string NetBiosName { get; set; } = "";
    public string Workgroup { get; set; } = "";

    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public string Mac { get => _mac; set => Set(ref _mac, value); }
    public string Vendor { get => _vendor; set => Set(ref _vendor, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Ports { get => _ports; set => Set(ref _ports, value); }
    public string Info { get => _info; set => Set(ref _info, value); }

    public long? Ping
    {
        get => _ping;
        set { Set(ref _ping, value); Raise(nameof(PingText)); Raise(nameof(PingSort)); }
    }

    public string PingText => _ping.HasValue ? $"{_ping} ms" : "–";
    public long PingSort => _ping ?? long.MaxValue;
}

public partial class NetScanPage : ToolPage
{
    private readonly ObservableCollection<ScanHost> _hosts = new();
    private CancellationTokenSource _cts;

    public NetScanPage()
    {
        InitializeComponent();
        HostGrid.ItemsSource = _hosts;
        RangeBox.Text = NetUtil.DefaultScanRange();
        Loaded += async (_, _) =>
        {
            if (!OuiDatabase.IsLoaded)
            {
                OuiText.Text = "Herstellerliste wird geladen …";
                await OuiDatabase.EnsureLoadedAsync();
            }
            UpdateOuiText();
        };
    }

    private void UpdateOuiText()
    {
        OuiText.Text = OuiDatabase.Count > 0
            ? $"Herstellerliste: {OuiDatabase.Count:N0} Einträge (Stand {OuiDatabase.CacheDate:dd.MM.yyyy})"
            : "Herstellerliste nicht verfügbar (keine Internetverbindung?)";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        List<IPAddress> ips;
        try { ips = NetUtil.ParseRange(RangeBox.Text, 4096); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _hosts.Clear();
        Progress.Maximum = ips.Count;
        Progress.Value = 0;
        var sw = Stopwatch.StartNew();
        int timeout = (int)(TimeoutBox.Value ?? 700);
        bool checkPorts = PortsBox.IsChecked == true;

        var locals = NetUtil.ActiveIPv4().ToList();
        var selfIps = locals.Select(l => l.Addr.Address.ToString()).ToHashSet();
        var gateways = locals.Where(l => l.Gateway != null).Select(l => l.Gateway.ToString()).ToHashSet();
        bool IsLocalSubnet(IPAddress ip) => locals.Any(l =>
        {
            var mask = NetUtil.PrefixToMask(l.Addr.PrefixLength);
            return (NetUtil.ToUInt(l.Addr.Address) & mask) == (NetUtil.ToUInt(ip) & mask);
        });

        try
        {
            await OuiDatabase.EnsureLoadedAsync();
            UpdateOuiText();
            StatusText.Text = "Suche Geräte …";
            var ssdpTask = SsdpDiscovery.DiscoverAsync(3500, ct);

            using var gate = new SemaphoreSlim(128);
            int done = 0;
            var detailTasks = new List<Task>();

            var tasks = ips.Select(async ip =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var pingTask = NetUtil.PingAsync(ip, timeout, ct);
                    byte[] mac = null;
                    if (IsLocalSubnet(ip)) mac = await Task.Run(() => NetUtil.Arp(ip), ct);
                    var rtt = await pingTask;
                    bool self = selfIps.Contains(ip.ToString());
                    if (rtt == null && mac == null && !self) return;

                    var host = new ScanHost
                    {
                        Address = ip,
                        IsSelf = self,
                        IsGateway = gateways.Contains(ip.ToString()),
                        Ping = rtt,
                    };
                    if (self)
                    {
                        var nic = locals.First(l => l.Addr.Address.Equals(ip)).Nic;
                        mac = nic.GetPhysicalAddress().GetAddressBytes();
                        host.Ping ??= 0;
                    }
                    if (mac != null && mac.Any(b => b != 0)) host.Mac = NetUtil.FormatMac(mac);
                    host.Vendor = OuiDatabase.Lookup(host.Mac);
                    Insert(host);
                    lock (detailTasks) detailTasks.Add(DetailsAsync(host, checkPorts, ct));
                }
                catch (OperationCanceledException) { }
                finally
                {
                    gate.Release();
                    done++;
                    if (done % 8 == 0 || done == ips.Count)
                    {
                        Progress.Value = done;
                        StatusText.Text = $"{done}/{ips.Count} Adressen · {_hosts.Count} Geräte";
                    }
                }
            });
            await Task.WhenAll(tasks);

            StatusText.Text = $"{_hosts.Count} Geräte gefunden – ermittle Details …";
            Task[] dt;
            lock (detailTasks) dt = detailTasks.ToArray();
            await Task.WhenAll(dt);

            var ssdp = await ssdpTask;
            foreach (var h in _hosts)
            {
                if (ssdp.TryGetValue(h.Ip, out var dev)) h.Ssdp = dev;
                UpdateInfo(h);
            }
            StatusText.Text = $"Fertig: {_hosts.Count} Geräte in {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Abgebrochen – {_hosts.Count} Geräte gefunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Progress.Value = Progress.Maximum;
            _cts = null;
        }
    }

    private void Insert(ScanHost h)
    {
        int i = 0;
        while (i < _hosts.Count && _hosts[i].SortKey < h.SortKey) i++;
        _hosts.Insert(i, h);
    }

    private static async Task DetailsAsync(ScanHost h, bool checkPorts, CancellationToken ct)
    {
        var dnsTask = NetUtil.ReverseDnsAsync(h.Address, 2500);
        var nbTask = NetBiosClient.QueryAsync(h.Address, 1200);
        Task<int[]> portTask = Task.FromResult(Array.Empty<int>());
        if (checkPorts)
        {
            portTask = Task.WhenAll(PortCatalog.Quick.Select(async p => await NetUtil.TcpConnectAsync(h.Address, p, 600, ct) ? p : 0))
                .ContinueWith(t => t.Result.Where(p => p > 0).ToArray(), ct);
        }

        var dns = await dnsTask;
        var nb = await nbTask;
        h.NetBiosName = nb.Name;
        h.Hostname = dns != "" ? dns : nb.Name;
        h.Workgroup = nb.Group;

        try
        {
            var ports = await portTask;
            h.OpenPorts.AddRange(ports);
            h.Ports = string.Join(", ", ports.Select(p => PortCatalog.Name(p) is { Length: > 0 } n ? $"{p} {n}" : p.ToString()));
        }
        catch { }
        UpdateInfo(h);
    }

    private static void UpdateInfo(ScanHost h)
    {
        var parts = new List<string>();
        if (h.Ssdp != null && h.Ssdp.Summary != "") parts.Add(h.Ssdp.Summary);
        if (h.NetBiosName != "" && !h.Hostname.StartsWith(h.NetBiosName, StringComparison.OrdinalIgnoreCase)) parts.Add("NetBIOS " + h.NetBiosName);
        if (h.Workgroup != "") parts.Add("Arbeitsgruppe " + h.Workgroup);
        h.Info = string.Join(" · ", parts.Distinct());
        h.Type = DeviceClassifier.Classify(h);
    }

    private ScanHost Selected => HostGrid.SelectedItem as ScanHost;

    private void HostGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected != null) Main.Navigate("portscan", Selected.Ip);
    }

    private void PortScan_Click(object sender, RoutedEventArgs e) { if (Selected != null) Main.Navigate("portscan", Selected.Ip); }
    private void Ping_Click(object sender, RoutedEventArgs e) { if (Selected != null) Main.Navigate("ping", Selected.Ip); }
    private void OpenHttp_Click(object sender, RoutedEventArgs e) { if (Selected != null) NetUtil.OpenUrl("http://" + Selected.Ip); }
    private void OpenHttps_Click(object sender, RoutedEventArgs e) { if (Selected != null) NetUtil.OpenUrl("https://" + Selected.Ip); }
    private void CopyIp_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Ip);
    private void CopyMac_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Mac);

    private void Wol_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || Selected.Mac == "") return;
        Main.Navigate("wol", Selected);
    }

    private async void UpdateOui_Click(object sender, RoutedEventArgs e)
    {
        OuiText.Text = "Herstellerliste wird heruntergeladen …";
        try
        {
            await OuiDatabase.EnsureLoadedAsync(forceDownload: true);
            foreach (var h in _hosts) h.Vendor = OuiDatabase.Lookup(h.Mac);
        }
        catch (Exception ex) { OuiText.Text = "Fehler: " + ex.Message; return; }
        UpdateOuiText();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "CSV-Datei|*.csv", FileName = $"netzwerkscan-{DateTime.Now:yyyy-MM-dd-HHmm}.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder("IP;Hostname;MAC;Hersteller;Typ;Offene Ports;Ping ms;Info\n");
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        foreach (var h in _hosts)
            sb.AppendLine(string.Join(";", Q(h.Ip), Q(h.Hostname), Q(h.Mac), Q(h.Vendor), Q(h.Type), Q(h.Ports), h.Ping?.ToString() ?? "", Q(h.Info)));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}

/// <summary>Heuristische Geräteerkennung aus Hersteller, Ports, Hostname und UPnP-Daten.</summary>
public static class DeviceClassifier
{
    public static string Classify(ScanHost h)
    {
        if (h.IsSelf) return "Dieser PC";
        var v = (h.Vendor ?? "").ToLowerInvariant();
        var n = (h.Hostname ?? "").ToLowerInvariant();
        var s = ((h.Ssdp?.DeviceType ?? "") + " " + (h.Ssdp?.Summary ?? "") + " " + (h.Ssdp?.Server ?? "")).ToLowerInvariant();
        var p = h.OpenPorts;
        bool Has(params int[] ports) => ports.Any(p.Contains);
        bool V(params string[] k) => k.Any(v.Contains);
        bool N(params string[] k) => k.Any(n.Contains);
        bool S(params string[] k) => k.Any(s.Contains);

        if (h.IsGateway || S("internetgatewaydevice", "fritz!box", "router") || N("fritz.box", "router", "gateway", "speedport"))
            return "Router / Gateway";
        if (S("mediarenderer", "tv", "bravia", "webos", "tizen") || V("lg innotek", "samsung electronics", "tcl", "hisense") && Has(8008, 8009, 9080))
            return "Smart-TV / Mediaplayer";
        if (Has(8009) || S("chromecast", "google cast")) return "Chromecast / Google Cast";
        if (V("sonos") || S("sonos")) return "Sonos-Lautsprecher";
        if (Has(9100, 631, 515) || S("printer", "drucker") || V("brother", "canon", "epson", "hewlett", "kyocera", "lexmark", "ricoh", "xerox"))
            return "Drucker";
        if (Has(554) || V("hikvision", "dahua", "axis comm", "reolink", "ubiquiti") && Has(554) || S("camera", "nvr"))
            return "Kamera / NVR";
        if (V("synology", "qnap", "western digital", "asustor") || Has(5000, 5001) && Has(445) || S("nas"))
            return "NAS";
        if (Has(8123) || N("homeassistant")) return "Home Assistant";
        if (V("raspberry")) return "Raspberry Pi";
        if (V("espressif", "tuya", "shelly", "tp-link") && !Has(445, 3389) || Has(1883)) return "IoT-Gerät";
        if (V("avm", "lancom", "mikrotik", "cisco", "juniper", "aruba", "zyxel", "netgear", "d-link", "draytek") && Has(80, 443, 22, 23))
            return "Netzwerkgerät";
        if (V("ubiquiti") ) return "Ubiquiti (AP/Switch)";
        if (Has(62078) || V("apple") && !Has(445, 22)) return N("ipad") ? "iPad" : N("iphone") ? "iPhone" : "Apple-Gerät";
        if (Has(3389) || (Has(135, 139, 445) && !Has(22))) return "Windows-PC/Server";
        if (Has(548) || N("macbook", "imac", "mac-mini")) return "Mac";
        if (Has(22) && Has(445)) return "Linux-Server / NAS";
        if (Has(22)) return "Linux / Netzwerkgerät";
        if (V("amazon")) return "Amazon-Gerät (Echo/Fire)";
        if (V("nintendo", "sony interactive", "microsoft") && !Has(445)) return "Spielkonsole";
        if (OuiDatabase.IsRandomized(h.Mac)) return "Smartphone / Tablet (private MAC)";
        if (V("samsung", "xiaomi", "huawei", "oneplus", "oppo", "motorola", "google")) return "Smartphone / Tablet";
        if (Has(80, 443)) return "Gerät mit Weboberfläche";
        return "";
    }
}
