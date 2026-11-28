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
    /// <summary>FRITZ!Box-Kennwort nur im Speicher (bis zum Beenden der App), nicht in settings.json.</summary>
    private static string _routerPassword = "";

    private static readonly (RouterKind Kind, string Label)[] Kinds =
    {
        (RouterKind.Auto, "Automatisch"), (RouterKind.FritzBox, "FRITZ!Box (TR-064)"), (RouterKind.UniFi, "UniFi OS"),
        (RouterKind.OpenWrt, "OpenWrt (ubus)"), (RouterKind.MikroTik, "MikroTik (REST)"), (RouterKind.OpnSense, "OPNsense (API)"),
        (RouterKind.SnmpOnly, "Nur SNMP"),
    };

    public NetScanView()
    {
        InitializeComponent();
        HostGrid.ItemsSource = _hosts;
        RangeBox.Text = NetUtil.DefaultScanRange();
        var cfg = AppSettings.Current;
        RouterBox.IsChecked = cfg.ScanQueryRouter;
        ProbeBox.IsChecked = cfg.ScanDeviceProbes;
        KindBox.ItemsSource = Kinds.Select(k => L.T(k.Label)).ToList();
        KindBox.SelectedIndex = Math.Max(0, Array.FindIndex(Kinds, k => k.Kind.ToString() == cfg.ScanRouterKind));
        RouterAddrBox.Text = cfg.ScanRouter;
        RouterUserBox.Text = cfg.ScanRouterUser;
        RouterPassBox.Text = _routerPassword;
        CommunityBox.Text = cfg.ScanSnmpCommunity;
        // Offen, bis der Nutzer den Bereich zuklappt (Zustand wird gemerkt)
        RouterExpander.IsExpanded = cfg.ScanOptionsExpanded;
        RouterExpander.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Expander.IsExpandedProperty) return;
            AppSettings.Current.ScanOptionsExpanded = RouterExpander.IsExpanded;
            AppSettings.Current.Save();
        };
        Loaded += async (_, _) =>
        {
            if (!Platform.HasArp) StatusText.Text = L.T("Hinweis: Dieses System gibt keine MAC-Adressen heraus – sie werden beim Router erfragt.");
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
        catch (Exception ex) { StatusText.Text = NetUtil.ErrorText(ex); return; }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _hosts.Clear();
        Progress.Maximum = ips.Count;
        Progress.Value = 0;
        var sw = Stopwatch.StartNew();
        var cfg = AppSettings.Current;
        cfg.ScanQueryRouter = RouterBox.IsChecked == true;
        cfg.ScanDeviceProbes = ProbeBox.IsChecked == true;
        var kind = Kinds[Math.Max(0, KindBox.SelectedIndex)].Kind;
        cfg.ScanRouterKind = kind.ToString();
        cfg.ScanRouter = (RouterAddrBox.Text ?? "").Trim();
        cfg.ScanRouterUser = (RouterUserBox.Text ?? "").Trim();
        cfg.ScanSnmpCommunity = (CommunityBox.Text ?? "").Trim();
        cfg.Save();
        _routerPassword = RouterPassBox.Text ?? "";
        var router = cfg.ScanRouter;
        if (router != "" && !IPAddress.TryParse(router, out _))
        {
            try { router = (await Dns.GetHostAddressesAsync(router)).FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? ""; }
            catch { StatusText.Text = L.F("Router „{0}“ nicht gefunden.", cfg.ScanRouter); return; }
        }
        var settings = new NetScanSettings
        {
            TimeoutMs = (int)(TimeoutBox.Value ?? 700), CheckPorts = PortsBox.IsChecked == true,
            QueryRouter = cfg.ScanQueryRouter, RouterKind = kind, DeviceProbes = cfg.ScanDeviceProbes, Router = router, RouterUser = cfg.ScanRouterUser,
            RouterPassword = _routerPassword, SnmpCommunity = cfg.ScanSnmpCommunity,
        };

        try
        {
            await NetworkScanner.ScanAsync(ips, settings, Insert,
                (done, count) => Dispatcher.UIThread.Post(() =>
                {
                    Progress.Value = done;
                    StatusText.Text = L.F("{0}/{1} Adressen · {2} Geräte", done, ips.Count, count);
                }),
                text => Dispatcher.UIThread.Post(() => { StatusText.Text = text; OuiText.Text = NetworkScanner.OuiStatus(); }), ct);
            StatusText.Text = L.F("Fertig: {0} Geräte in {1:0.0} s", _hosts.Count, sw.Elapsed.TotalSeconds)
                + (settings.RouterSummary != "" ? " · " + settings.RouterSummary : "");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.F("Abgebrochen – {0} Geräte gefunden.", _hosts.Count);
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
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
        catch (Exception ex) { OuiText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); return; }
        OuiText.Text = NetworkScanner.OuiStatus();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_hosts.Count == 0) return;
        try { await SaveTextAsync($"netzwerkscan-{DateTime.Now:yyyy-MM-dd-HHmm}.csv", L.T("CSV-Datei"), "csv", NetworkScanner.ToCsv(_hosts)); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
