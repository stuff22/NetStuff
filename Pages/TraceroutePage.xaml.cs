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

public partial class TraceroutePage : ToolPage
{
    private readonly ObservableCollection<TraceHop> _hops = new();
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
            await Traceroute.RunAsync(ip, maxHops, timeout, _hops, row => HopGrid.ScrollIntoView(row), ct);

            StatusText.Text = "Standorte werden ermittelt …";
            try
            {
                _self = await selfTask;
                await Traceroute.AddGeoAsync(_hops, ct);
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

    private async Task UpdateMap()
    {
        if (!_mapReady) return;
        var points = Traceroute.MapPoints(_self, _hops);
        try { await Map.ExecuteScriptAsync("setPoints(" + JsonSerializer.Serialize(points) + ")"); } catch { }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyText(Traceroute.ToText(_hops));
    }
}
