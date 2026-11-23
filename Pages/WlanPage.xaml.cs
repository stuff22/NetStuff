using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetStuff.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

/// <summary>Anzeigezeile eines Access Points (Farbe passend zum Spektrum-Diagramm).</summary>
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
    public string ConnectedText => _b.Connected ? L.T("verbunden") : "";
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
    private List<WlanBss> _last = new();

    public WlanPage()
    {
        InitializeComponent();
        NetGrid.ItemsSource = _rows;
        Loaded += (_, _) => LoadInterfaces();
    }

    private void LoadInterfaces()
    {
        if (IfaceBox.Items.Count > 0) return;
        try
        {
            var list = WlanAnalysis.Scanner.Interfaces();
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
        if (IfaceBox.SelectedItem is not WlanInterfaceInfo iface) return;
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
                var list = WlanAnalysis.Prepare(await WlanAnalysis.Scanner.ScanAsync(iface.Id, round++ == 0 ? 300 : 0, ct));
                PermissionPanel.Visibility = Visibility.Collapsed;
                Apply(list);
                StatusText.Text = WlanAnalysis.Summary(list);
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

    private string CurrentBand => Band5.IsChecked == true ? "5 GHz" : Band6.IsChecked == true ? "6 GHz" : "2,4 GHz";

    private void Band_Checked(object sender, RoutedEventArgs e)
    {
        if (Spectrum == null) return;
        Spectrum.Band = CurrentBand;
        Spectrum.Refresh();
        UpdateAdvice();
    }

    private void UpdateAdvice() => AdviceText.Text = WlanAnalysis.ChannelAdvice(_last, CurrentBand);

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

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var hint = Platform.Wlan?.PermissionHint;
        if (!string.IsNullOrEmpty(hint)) NetUtil.OpenUrl(hint);
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
