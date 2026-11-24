using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class LldpView : ToolView
{
    private CancellationTokenSource _cts;

    public LldpView()
    {
        InitializeComponent();
        Subtitle.Text = L.T(OperatingSystem.IsMacOS()
            ? "Zeigt, an welchem Switch, Port und VLAN dieser Mac hängt – aus den LLDP- und CDP-Paketen, die Switches regelmäßig senden. Nutzt das Bordmittel tcpdump (macOS fragt einmal nach dem Administratorkennwort)."
            : "Zeigt, an welchem Switch, Port und VLAN dieser PC hängt – aus den LLDP- und CDP-Paketen, die Switches regelmäßig senden. Nutzt das Windows-Bordmittel pktmon (Administratorrechte werden einmal abgefragt).");
        if (!LldpCapture.IsSupported)
        {
            StartButton.IsEnabled = false;
            StatusText.Text = L.T("LLDP/CDP-Mitschnitt ist auf dieser Plattform nicht verfügbar.");
        }
    }

    public override void RunNow() => Start_Click(null, null);

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null || !LldpCapture.IsSupported) return;
        int seconds = int.Parse((DurationBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "65");
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        ResultsPanel.Children.Clear();
        Progress.Maximum = seconds + 5;
        Progress.Value = 0;
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            Progress.Value = Math.Min(Progress.Maximum, sw.Elapsed.TotalSeconds);
            StatusText.Text = L.F("Warte auf LLDP/CDP-Pakete … {0:0} / {1} s", sw.Elapsed.TotalSeconds, seconds);
        };
        timer.Start();

        try
        {
            _ = OuiDatabase.EnsureLoadedAsync();
            var list = await LldpCapture.CaptureAsync(seconds, _cts.Token);
            foreach (var s in LldpReport.Sections(list)) ResultsPanel.Children.Add(UiFactory.Section(s));
            StatusText.Text = L.F("{0} Nachbar(n) gefunden", list.Count);
        }
        catch (OperationCanceledException) { StatusText.Text = L.T("Abgebrochen."); }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(NetUtil.ErrorText(ex), Level.Bad));
            StatusText.Text = "";
        }
        finally
        {
            timer.Stop();
            Progress.Value = Progress.Maximum;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
