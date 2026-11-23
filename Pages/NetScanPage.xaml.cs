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
            OuiText.Text = NetworkScanner.OuiStatus();
        };
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
        var settings = new NetScanSettings { TimeoutMs = (int)(TimeoutBox.Value ?? 700), CheckPorts = PortsBox.IsChecked == true };

        try
        {
            await NetworkScanner.ScanAsync(ips, settings, Insert,
                (done, count) => Dispatcher.Invoke(() =>
                {
                    Progress.Value = done;
                    StatusText.Text = $"{done}/{ips.Count} Adressen · {count} Geräte";
                }),
                text => Dispatcher.Invoke(() => { StatusText.Text = text; OuiText.Text = NetworkScanner.OuiStatus(); }), ct);
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

    private void Insert(ScanHost h) => Dispatcher.Invoke(() =>
    {
        int i = 0;
        while (i < _hosts.Count && _hosts[i].SortKey < h.SortKey) i++;
        _hosts.Insert(i, h);
    });

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
        OuiText.Text = NetworkScanner.OuiStatus();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0) return;
        var dlg = new SaveFileDialog { Filter = "CSV-Datei|*.csv", FileName = $"netzwerkscan-{DateTime.Now:yyyy-MM-dd-HHmm}.csv" };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, NetworkScanner.ToCsv(_hosts), new UTF8Encoding(true));
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
