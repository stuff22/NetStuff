using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class PortScanPage : ToolPage
{
    private readonly ObservableCollection<PortRow> _rows = new();
    private CancellationTokenSource _cts;

    public PortScanPage()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
        PresetBox_SelectionChanged(null, null);
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host && _cts == null) HostBox.Text = host;
    }

    private void PresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortsBox == null) return;
        switch (PresetBox.SelectedIndex)
        {
            case 0: PortsBox.Text = string.Join(",", PortCatalog.Top100); break;
            case 1: PortsBox.Text = string.Join(",", PortCatalog.Names.Keys.OrderBy(p => p)); break;
            case 2: PortsBox.Text = "1-1024"; break;
            case 3: PortsBox.Text = "1-65535"; break;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        List<int> ports;
        try { ports = PortCatalog.Parse(PortsBox.Text); }
        catch { StatusText.Text = "Ungültige Portangabe."; return; }
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
            StatusText.Text = $"Scanne {ip} …";
            await PortScanner.ScanAsync(ip, ports, settings, Insert, done => Dispatcher.Invoke(() =>
            {
                Progress.Value = done;
                StatusText.Text = $"{done}/{ports.Count} Ports · {_rows.Count} offen";
            }), ct);
            StatusText.Text = $"Fertig: {_rows.Count} offene von {ports.Count} Ports auf {ip} in {sw.Elapsed.TotalSeconds:0.0} s";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = $"Abgebrochen – {_rows.Count} offene Ports gefunden.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + NetUtil.ErrorText(ex);
        }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Insert(PortRow row) => Dispatcher.Invoke(() =>
    {
        int i = 0;
        while (i < _rows.Count && _rows[i].Port < row.Port) i++;
        _rows.Insert(i, row);
    });

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
