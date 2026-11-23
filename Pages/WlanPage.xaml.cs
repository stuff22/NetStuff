using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetStuff.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class WlanRow : Bindable
{
    private WlanBss _b;
    public WlanRow(WlanBss b) { _b = b; }

    public WlanBss Data => _b;
    public string Bssid => _b.Bssid;
    public string Ssid => _b.DisplaySsid;
    public string Vendor => _b.Vendor;
    public int Rssi => _b.Rssi;
    public string RssiText => $"{_b.Rssi} dBm";
    public int Quality => _b.Quality;
    public int Channel => _b.Channel;
    public int Width => _b.WidthMhz;
    public string WidthText => $"{_b.WidthMhz} MHz";
    public string Band => _b.Band;
    public string Standard => _b.Standard;
    public string Security => _b.Security;
    public string RateText => _b.MaxRateMbps > 0 ? $"{_b.MaxRateMbps:0} Mbit" : "";
    public string ConnectedText => _b.Connected ? "verbunden" : "";
    public Brush ColorBrush => new SolidColorBrush(SpectrumChart.ColorFor(_b.Ssid));

    public void Update(WlanBss b)
    {
        _b = b;
        Raise(string.Empty);
    }
}

public partial class WlanPage : ToolPage
{
    private readonly ObservableCollection<WlanRow> _rows = new();
    private CancellationTokenSource _cts;
    private WlanApi _api;
    private List<WlanBss> _last = new();

    public WlanPage()
    {
        InitializeComponent();
        NetGrid.ItemsSource = _rows;
        Loaded += (_, _) => LoadInterfaces();
        Unloaded += (_, _) => { };
    }

    private void LoadInterfaces()
    {
        if (IfaceBox.Items.Count > 0) return;
        try
        {
            _api ??= new WlanApi();
            var list = _api.Interfaces();
            IfaceBox.ItemsSource = list;
            if (list.Count > 0) IfaceBox.SelectedIndex = 0;
            else StatusText.Text = "Kein WLAN-Adapter gefunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StartButton.IsEnabled = false;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (IfaceBox.SelectedItem is not WlanApi.Interface iface) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        await OuiDatabase.EnsureLoadedAsync();

        int round = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _api.Scan(iface.Id);
                if (round++ == 0) await Task.Delay(300, ct);
                var list = await Task.Run(() => _api.GetBssList(iface.Id), ct);
                PermissionPanel.Visibility = Visibility.Collapsed;
                foreach (var b in list) b.Vendor = VendorOf(b.Bssid);
                Apply(list);
                StatusText.Text = $"{list.Count} Access Points · {list.Select(b => b.Ssid).Distinct().Count()} Netze · aktualisiert {DateTime.Now:HH:mm:ss}";
            }
            catch (OperationCanceledException) { break; }
            catch (WlanException ex) when (ex.LocationDenied)
            {
                PermissionPanel.Visibility = Visibility.Visible;
                StatusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Fehler: " + ex.Message;
            }
            try { await Task.Delay(3500, ct); } catch { break; }
        }
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        _cts = null;
    }

    private void Apply(List<WlanBss> list)
    {
        list = list.GroupBy(b => b.Bssid).Select(g => g.OrderByDescending(x => x.Rssi).First()).ToList();
        _last = list;
        var byBssid = list.ToDictionary(b => b.Bssid);
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (byBssid.TryGetValue(_rows[i].Bssid, out var b)) _rows[i].Update(b);
            else _rows.RemoveAt(i);
        }
        var known = _rows.Select(r => r.Bssid).ToHashSet();
        foreach (var b in list.Where(b => !known.Contains(b.Bssid)).OrderByDescending(b => b.Rssi))
            _rows.Add(new WlanRow(b));

        Spectrum.Networks = list;
        Spectrum.Refresh();
        UpdateAdvice();
    }

    /// <summary>Virtuelle BSSIDs (mehrere SSIDs pro AP) haben das "lokal verwaltet"-Bit gesetzt – ohne dieses Bit findet man meist den echten Hersteller.</summary>
    private static string VendorOf(string bssid)
    {
        if (!OuiDatabase.IsRandomized(bssid)) return OuiDatabase.Lookup(bssid);
        var first = Convert.ToByte(bssid[..2], 16) & ~0x02;
        var real = OuiDatabase.Lookup(first.ToString("X2") + bssid[2..]);
        return real != "" && !real.StartsWith("Private") ? real + " (virtuell)" : "virtuelle BSSID";
    }

    private string CurrentBand => Band5.IsChecked == true ? "5 GHz" : Band6.IsChecked == true ? "6 GHz" : "2,4 GHz";

    private void Band_Checked(object sender, RoutedEventArgs e)
    {
        if (Spectrum == null) return;
        Spectrum.Band = CurrentBand;
        Spectrum.Refresh();
        UpdateAdvice();
    }

    /// <summary>Kanalempfehlung: Belastung je Kanal aus Signalstärke und Überlappung.</summary>
    private void UpdateAdvice()
    {
        var band = CurrentBand;
        var nets = _last.Where(b => b.Band == band).ToList();
        if (nets.Count == 0) { AdviceText.Text = ""; return; }

        IEnumerable<int> candidates = band switch
        {
            "2,4 GHz" => new[] { 1, 6, 11 },
            "5 GHz" => new[] { 36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140 },
            _ => Enumerable.Range(0, 59).Select(i => 1 + i * 4),
        };
        double Mhz(int ch) => band == "2,4 GHz" ? 2407 + ch * 5 : band == "6 GHz" ? 5950 + ch * 5 : 5000 + ch * 5;

        var scores = candidates.Select(ch =>
        {
            double c = Mhz(ch), load = 0;
            foreach (var n in nets)
            {
                double overlap = (n.WidthMhz + 20) / 2.0 - Math.Abs(n.CenterMhz - c);
                if (overlap > 0) load += Math.Pow(10, (n.Rssi + 100) / 20.0) * (overlap / ((n.WidthMhz + 20) / 2.0));
            }
            return (ch, load);
        }).OrderBy(x => x.load).ToList();

        var best = scores.Take(3).Select(s => s.ch.ToString());
        var mine = nets.FirstOrDefault(n => n.Connected);
        AdviceText.Text = $"Empfohlene Kanäle ({band}): {string.Join(", ", best)}"
                          + (mine != null ? $"   ·   Dein AP „{mine.DisplaySsid}“ funkt auf Kanal {mine.Channel} ({mine.WidthMhz} MHz, {mine.Rssi} dBm)" : "")
                          + (band == "5 GHz" ? "   ·   Kanäle 52–140 sind DFS-Kanäle (Radar-Erkennung)." : "");
    }

    private void NetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = NetGrid.SelectedItem as WlanRow;
        Spectrum.HighlightBssid = row?.Bssid;
        if (row != null && row.Band != CurrentBand)
        {
            if (row.Band == "5 GHz") Band5.IsChecked = true;
            else if (row.Band == "6 GHz") Band6.IsChecked = true;
            else Band24.IsChecked = true;
        }
        Spectrum.Refresh();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e) => NetUtil.OpenUrl("ms-settings:privacy-location");

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
