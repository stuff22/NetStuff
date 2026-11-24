using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class PortScanView : ToolView
{
    private readonly ObservableCollection<PortRow> _rows = new();
    private CancellationTokenSource _cts;

    public PortScanView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
        PresetBox.SelectionChanged += (_, _) => ApplyPreset();
        ApplyPreset();
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (_cts == null) HostBox.Text = parameter;
    }

    public override void RunNow() => Start_Click(null, null);

    private void ApplyPreset()
    {
        switch (PresetBox.SelectedIndex)
        {
            case 0: PortsBox.Text = string.Join(",", PortCatalog.Top100); break;
            case 1: PortsBox.Text = string.Join(",", PortCatalog.Names.Keys.OrderBy(p => p)); break;
            case 2: PortsBox.Text = "1-1024"; break;
            case 3: PortsBox.Text = "1-65535"; break;
        }
    }

    private static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        List<int> ports;
        try { ports = PortCatalog.Parse(PortsBox.Text); }
        catch { StatusText.Text = L.T("Ungültige Portangabe."); return; }
        if (ports.Count == 0 || string.IsNullOrWhiteSpace(HostBox.Text)) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _rows.Clear();
        Progress.Value = 0;
        Progress.Maximum = ports.Count;
        var settings = new PortScanSettings
        {
            TimeoutMs = (int)(TimeoutBox.Value ?? 800),
            Parallel = (int)(ParallelBox.Value ?? 200),
            GrabBanners = BannerBox.IsChecked == true,
        };
        var sw = Stopwatch.StartNew();

        try
        {
            var ip = await NetUtil.ResolveAsync(HostBox.Text, false, ct);
            StatusText.Text = L.F("Scanne {0} …", ip);
            await PortScanner.ScanAsync(ip, ports, settings, Insert, done => Ui(() =>
            {
                Progress.Value = done;
                StatusText.Text = L.F("{0}/{1} Ports · {2} offen", done, ports.Count, _rows.Count);
            }), ct);
            StatusText.Text = L.F("Fertig: {0} offene von {1} Ports auf {2} in {3:0.0} s", _rows.Count, ports.Count, ip, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { StatusText.Text = L.F("Abgebrochen – {0} offene Ports gefunden.", _rows.Count); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Insert(PortRow row) => Ui(() =>
    {
        int i = 0;
        while (i < _rows.Count && _rows[i].Port < row.Port) i++;
        _rows.Insert(i, row);
    });

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
