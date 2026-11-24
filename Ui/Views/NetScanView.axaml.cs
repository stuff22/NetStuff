using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class NetScanView : ToolView
{
    private readonly ObservableCollection<ScanHost> _hosts = new();
    private CancellationTokenSource _cts;

    public NetScanView()
    {
        InitializeComponent();
        HostGrid.ItemsSource = _hosts;
        RangeBox.Text = NetUtil.DefaultScanRange();
        Loaded += async (_, _) =>
        {
            if (!Platform.HasArp) StatusText.Text = L.T("Hinweis: Auf dieser Plattform werden keine MAC-Adressen ermittelt.");
            if (!OuiDatabase.IsLoaded)
            {
                OuiText.Text = L.T("Herstellerliste wird geladen …");
                await OuiDatabase.EnsureLoadedAsync();
            }
            OuiText.Text = NetworkScanner.OuiStatus();
        };
    }

    public override void OnNavigatedTo(string parameter) => RangeBox.Text = parameter;
    public override void RunNow() => Start_Click(null, null);

    private void RangeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled) return;
        List<IPAddress> ips;
        try { ips = NetUtil.ParseRange(RangeBox.Text ?? "", 4096); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _hosts.Clear();
        Progress.Maximum = ips.Count;
        Progress.Value = 0;
        var sw = Stopwatch.StartNew();
        var settings = new NetScanSettings { TimeoutMs = (int)(TimeoutBox.Value ?? 700), CheckPorts = PortsBox.IsChecked == true };

        try
        {
            await NetworkScanner.ScanAsync(ips, settings, Insert,
                (done, count) => Dispatcher.UIThread.Post(() =>
                {
                    Progress.Value = done;
                    StatusText.Text = L.F("{0}/{1} Adressen · {2} Geräte", done, ips.Count, count);
                }),
                text => Dispatcher.UIThread.Post(() => { StatusText.Text = text; OuiText.Text = NetworkScanner.OuiStatus(); }), ct);
            StatusText.Text = L.F("Fertig: {0} Geräte in {1:0.0} s", _hosts.Count, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.F("Abgebrochen – {0} Geräte gefunden.", _hosts.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Progress.Value = Progress.Maximum;
            _cts = null;
        }
    }

    private void Insert(ScanHost h) => Dispatcher.UIThread.Post(() =>
    {
        int i = 0;
        while (i < _hosts.Count && _hosts[i].SortKey < h.SortKey) i++;
        _hosts.Insert(i, h);
    });

    private ScanHost Selected => HostGrid.SelectedItem as ScanHost;

    private void HostGrid_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (Selected != null) Navigate("portscan", Selected.Ip);
    }

    private void PortScan_Click(object sender, RoutedEventArgs e) { if (Selected != null) Navigate("portscan", Selected.Ip); }
    private void Ping_Click(object sender, RoutedEventArgs e) { if (Selected != null) Navigate("ping", Selected.Ip); }
    private void OpenHttp_Click(object sender, RoutedEventArgs e) { if (Selected != null) NetUtil.OpenUrl("http://" + Selected.Ip); }
    private void OpenHttps_Click(object sender, RoutedEventArgs e) { if (Selected != null) NetUtil.OpenUrl("https://" + Selected.Ip); }
    private void CopyIp_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Ip);
    private void CopyMac_Click(object sender, RoutedEventArgs e) => CopyText(Selected?.Mac);

    private void Wol_Click(object sender, RoutedEventArgs e)
    {
        var h = Selected;
        if (h == null || h.Mac == "") return;
        var name = string.IsNullOrEmpty(h.Hostname) ? h.Ip : h.Hostname.Split('.')[0];
        var bc = WakeOnLan.SubnetBroadcastFor(h.SortKey)?.ToString() ?? "255.255.255.255";
        Navigate("wol", WolView.Parameter(h.Mac, name, bc));
    }

    private async void UpdateOui_Click(object sender, RoutedEventArgs e)
    {
        OuiText.Text = L.T("Herstellerliste wird heruntergeladen …");
        try
        {
            await OuiDatabase.EnsureLoadedAsync(forceDownload: true);
            foreach (var h in _hosts) h.Vendor = OuiDatabase.Lookup(h.Mac);
        }
        catch (Exception ex) { OuiText.Text = L.T("Fehler: ") + ex.Message; return; }
        OuiText.Text = NetworkScanner.OuiStatus();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0) return;
        try { await SaveTextAsync($"netzwerkscan-{DateTime.Now:yyyy-MM-dd-HHmm}.csv", L.T("CSV-Datei"), "csv", NetworkScanner.ToCsv(_hosts)); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + ex.Message; }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
