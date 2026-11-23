using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class HopRow : Bindable
{
    private string _host = "", _place = "", _isp = "";
    public int Hop { get; init; }
    public IPAddress Address { get; init; }
    public string Ip => Address?.ToString() ?? "*";
    public string Rtt { get; init; }
    public bool Reached { get; init; }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Place { get => _place; set => Set(ref _place, value); }
    public string Isp { get => _isp; set => Set(ref _isp, value); }
    public GeoInfo Geo { get; set; }
}

public partial class TraceroutePage : ToolPage
{
    private readonly ObservableCollection<HopRow> _hops = new();
    private CancellationTokenSource _cts;
    private bool _mapReady;
    private GeoInfo _self;

    public TraceroutePage()
    {
        InitializeComponent();
        HopGrid.ItemsSource = _hops;
        Loaded += async (_, _) => await InitMapAsync();
        MainWindow.ThemeChanged += async (_, _) =>
        {
            if (_mapReady) await Map.ExecuteScriptAsync($"setTheme({(MainWindow.IsDark ? "true" : "false")})");
        };
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host && _cts == null) TargetBox.Text = host;
    }

    private async Task InitMapAsync()
    {
        if (_mapReady || Map.CoreWebView2 != null) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDir, "WebView2"));
            await Map.EnsureCoreWebView2Async(env);
            Map.CoreWebView2.SetVirtualHostNameToFolderMapping("netstuff.local",
                Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            Map.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Map.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Map.NavigationCompleted += (_, e) => _mapReady = e.IsSuccess;
            Map.CoreWebView2.Navigate($"https://netstuff.local/map.html?theme={(MainWindow.IsDark ? "dark" : "light")}");
        }
        catch (Exception ex)
        {
            Map.Visibility = Visibility.Collapsed;
            MapError.Visibility = Visibility.Visible;
            MapError.Text = "Karte nicht verfügbar – WebView2-Laufzeit fehlt?\n" + ex.Message;
        }
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var target = NetUtil.CleanHost(TargetBox.Text);
        if (target == "") return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _hops.Clear();
        await UpdateMap();

        try
        {
            var ip = await NetUtil.ResolveAsync(target, V6Box.IsChecked == true, ct);
            StatusText.Text = $"Traceroute zu {target} [{ip}] …";
            int maxHops = (int)(MaxHopsBox.Value ?? 30);
            int timeout = (int)(TimeoutBox.Value ?? 1500);
            var selfTask = _self == null ? GeoIpService.SelfAsync(ct) : Task.FromResult(_self);
            var nameTasks = new List<Task>();

            using var ping = new Ping();
            var payload = new byte[32];
            for (int ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested; ttl++)
            {
                IPAddress hopIp = null;
                bool reached = false;
                var rtts = new List<string>();
                for (int probe = 0; probe < 3 && !ct.IsCancellationRequested; probe++)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        // ohne CancellationToken (siehe PingPage) – endet spätestens nach dem Timeout
                        var r = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeout), payload, new PingOptions(ttl, true));
                        sw.Stop();
                        if (r.Status is IPStatus.TtlExpired or IPStatus.Success or IPStatus.TimeExceeded)
                        {
                            hopIp ??= r.Address;
                            var ms = r.Status == IPStatus.Success && r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds;
                            rtts.Add(ms < 1 ? "<1" : ms.ToString("0"));
                            if (r.Status == IPStatus.Success) reached = true;
                        }
                        else if (r.Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable)
                        {
                            hopIp ??= r.Address;
                            rtts.Add("!H");
                        }
                        else rtts.Add("*");
                    }
                    catch { rtts.Add("*"); }
                }
                ct.ThrowIfCancellationRequested();

                var row = new HopRow
                {
                    Hop = ttl,
                    Address = hopIp,
                    Rtt = string.Join(" / ", rtts) + (rtts.Any(r => r != "*" && r != "!H") ? " ms" : ""),
                    Reached = reached,
                };
                _hops.Add(row);
                HopGrid.ScrollIntoView(row);
                if (hopIp != null) nameTasks.Add(ResolveName(row));
                if (reached) break;
                if (_hops.Count >= 5 && _hops.Skip(_hops.Count - 5).All(h => h.Address == null))
                {
                    for (int i = 0; i < 4; i++) _hops.RemoveAt(_hops.Count - 1);
                    _hops[^1].Host = "… keine Antworten mehr – Ziel oder Router filtern ICMP";
                    break;
                }
            }

            await Task.WhenAll(nameTasks);
            StatusText.Text = "Standorte werden ermittelt …";
            try
            {
                _self = await selfTask;
                var geo = await GeoIpService.LookupAsync(_hops.Where(h => h.Address != null).Select(h => h.Address), ct);
                foreach (var h in _hops.Where(h => h.Address != null))
                {
                    if (NetUtil.IsPrivate(h.Address)) { h.Place = "privates Netz"; continue; }
                    if (geo.TryGetValue(h.Ip, out var g) && g.Ok)
                    {
                        h.Geo = g;
                        h.Place = g.Location;
                        h.Isp = string.Join(" · ", new[] { g.Isp, g.As?.Split(' ').FirstOrDefault() }.Where(s => !string.IsNullOrEmpty(s)));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText.Text = "Geodaten nicht verfügbar: " + ex.Message;
            }
            await UpdateMap();
            var last = _hops.LastOrDefault();
            StatusText.Text = last?.Reached == true
                ? $"Ziel {ip} nach {last.Hop} Hops erreicht."
                : $"Ziel nach {_hops.Count} Hops nicht erreicht (ICMP evtl. gefiltert).";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Abgebrochen.";
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

    private static async Task ResolveName(HopRow row)
    {
        row.Host = await NetUtil.ReverseDnsAsync(row.Address, 2500);
    }

    private async Task UpdateMap()
    {
        if (!_mapReady) return;
        var points = new List<object>();
        if (_self?.Ok == true && _hops.Any(h => h.Geo != null))
            points.Add(new { hop = 0, ip = _self.Ip, host = "", lat = _self.Lat, lon = _self.Lon, place = _self.Location, isp = _self.Isp, rtt = "", kind = "start" });

        var withGeo = _hops.Where(h => h.Geo != null).ToList();
        for (int i = 0; i < withGeo.Count; i++)
        {
            var h = withGeo[i];
            // Aufeinanderfolgende Hops am selben Ort zusammenfassen
            if (i > 0 && withGeo[i - 1].Geo.Lat == h.Geo.Lat && withGeo[i - 1].Geo.Lon == h.Geo.Lon && i < withGeo.Count - 1) continue;
            points.Add(new
            {
                hop = h.Hop, ip = h.Ip, host = h.Host, lat = h.Geo.Lat, lon = h.Geo.Lon, place = h.Place, isp = h.Isp, rtt = h.Rtt,
                kind = i == withGeo.Count - 1 && h.Reached ? "end" : "hop",
            });
        }
        try { await Map.ExecuteScriptAsync("setPoints(" + JsonSerializer.Serialize(points) + ")"); } catch { }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyText(string.Join("\n", _hops.Select(h => $"{h.Hop,3}  {h.Ip,-40} {h.Rtt,-22} {h.Host}  {h.Place}  {h.Isp}")));
    }
}
