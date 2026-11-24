using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NetStuff.Services;
using NetStuff.Ui.Controls;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Anzeigezeile eines Access Points (Farbe passend zum Spektrum-Diagramm).</summary>
public class WlanRow : Bindable
{
    private WlanBss _b;
    public WlanRow(WlanBss b) { _b = b; }

    public WlanBss Data => _b;
    public string Bssid => _b.Bssid;
    public string Ssid => string.IsNullOrEmpty(_b.Ssid) ? L.T(Platform.Wlan?.NamesHidden == true ? "(Name verborgen)" : "(versteckt)") : _b.Ssid;
    public string Vendor => _b.Vendor;
    public int Rssi => _b.Rssi;
    public string RssiText => $"{_b.Rssi} dBm";
    public int Quality => _b.Quality;
    public int Channel => _b.Channel;
    public int Width => _b.WidthMhz;
    public string WidthText => $"{_b.WidthMhz} MHz";
    public string Band => _b.Band;
    public string Standard => _b.Standard;
    public string Security => L.T(_b.Security ?? "");
    public string RateText => _b.MaxRateMbps > 0 ? $"{_b.MaxRateMbps:0} Mbit" : "";
    public string ConnectedText => _b.Connected ? L.T("verbunden") : "";
    public IBrush ColorBrush => new SolidColorBrush(SpectrumChart.ColorFor(_b.Ssid));

    public void Update(WlanBss b)
    {
        _b = b;
        Raise(string.Empty);
    }
}

public partial class WlanView : ToolView
{
    private readonly ObservableCollection<WlanRow> _rows = new();
    private CancellationTokenSource _cts;
    private List<WlanBss> _last = new();

    public WlanView()
    {
        InitializeComponent();
        NetGrid.ItemsSource = _rows;
        Loaded += (_, _) => LoadInterfaces();
        DetachedFromVisualTree += (_, _) => _cts?.Cancel();
    }

    public override void RunNow() => Start_Click(null, null);

    private void LoadInterfaces()
    {
        if (IfaceBox.ItemCount > 0) return;
        try
        {
            var list = WlanAnalysis.Scanner.Interfaces();
            IfaceBox.ItemsSource = list;
            if (list.Count > 0) IfaceBox.SelectedIndex = 0;
            else { StatusText.Text = L.T("Kein WLAN-Adapter gefunden."); StartButton.IsEnabled = false; }
        }
        catch (Exception ex)
        {
            StatusText.Text = NetUtil.ErrorText(ex);
            StartButton.IsEnabled = false;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        LoadInterfaces();
        if (IfaceBox.SelectedItem is not WlanInterfaceInfo iface || !StartButton.IsEnabled) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        StatusText.Text = L.T("Scanne WLAN …");
        await OuiDatabase.EnsureLoadedAsync();

        int round = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var list = WlanAnalysis.Prepare(await WlanAnalysis.Scanner.ScanAsync(iface.Id, round++ == 0 ? 300 : 0, ct));
                ShowPermission(WlanAnalysis.Scanner.NamesHidden
                    ? L.T("macOS zeigt WLAN-Namen und BSSIDs nur mit Standortfreigabe. Kanäle und Signalstärken sind trotzdem sichtbar. Für die Namen Net-Stuff als App starten (Desktop/macos/build-app.sh) und unter Systemeinstellungen → Datenschutz & Sicherheit → Ortungsdienste erlauben.")
                    : null);
                Apply(list);
                StatusText.Text = WlanAnalysis.Summary(list);
            }
            catch (OperationCanceledException) { break; }
            catch (WlanException ex) when (ex.LocationDenied)
            {
                ShowPermission(L.T("Windows 11 gibt WLAN-Scanergebnisse nur frei, wenn die Standortdienste aktiv sind und Desktop-Apps darauf zugreifen dürfen: Einstellungen → Datenschutz und Sicherheit → Standort → „Standortdienste“ und „Desktop-Apps Zugriff auf Ihren Standort erlauben“ einschalten."));
                StatusText.Text = NetUtil.ErrorText(ex);
            }
            catch (Exception ex)
            {
                StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
            }
            try { await Task.Delay(3500, ct); } catch { break; }
        }
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        _cts = null;
    }

    private void ShowPermission(string text)
    {
        PermissionPanel.IsVisible = text != null;
        PermissionText.Text = text ?? "";
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

    // Beim Umschalten ist der vorherige RadioButton kurz noch gesetzt – daher den zuletzt gewählten merken
    private string _band = "2,4 GHz";
    private string CurrentBand => _band;

    private void Band_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        _band = rb == Band5 ? "5 GHz" : rb == Band6 ? "6 GHz" : "2,4 GHz";
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
