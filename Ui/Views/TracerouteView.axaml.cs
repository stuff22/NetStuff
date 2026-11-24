using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class TracerouteView : ToolView
{
    private readonly ObservableCollection<TraceHop> _hops = new();
    private CancellationTokenSource _cts;

    public TracerouteView()
    {
        InitializeComponent();
        HopGrid.ItemsSource = _hops;
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (_cts == null) TargetBox.Text = parameter;
    }

    public override void RunNow() => Start_Click(null, null);

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var target = NetUtil.CleanHost(TargetBox.Text);
        if (target == "" || _cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _hops.Clear();

        try
        {
            var ip = await NetUtil.ResolveAsync(target, V6Box.IsChecked == true, ct);
            StatusText.Text = L.F("Traceroute zu {0} [{1}] …", target, ip);
            int maxHops = (int)(MaxHopsBox.Value ?? 30);
            int timeout = (int)(TimeoutBox.Value ?? 1500);
            await Traceroute.RunAsync(ip, maxHops, timeout, _hops, row => HopGrid.ScrollIntoView(row, null), ct);

            StatusText.Text = L.T("Standorte werden ermittelt …");
            try { await Traceroute.AddGeoAsync(_hops, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusText.Text = L.T("Geodaten nicht verfügbar: ") + ex.Message;
            }
            var last = _hops.LastOrDefault();
            StatusText.Text = last?.Reached == true
                ? L.F("Ziel {0} nach {1} Hops erreicht.", ip, last.Hop)
                : L.F("Ziel nach {0} Hops nicht erreicht (ICMP evtl. gefiltert).", _hops.Count);
        }
        catch (OperationCanceledException) { StatusText.Text = L.T("Abgebrochen."); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + ex.Message; }
        finally
        {
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(Traceroute.ToText(_hops));
}
