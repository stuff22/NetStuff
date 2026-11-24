using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class LldpPage : ToolPage
{
    private CancellationTokenSource _cts;

    public LldpPage()
    {
        InitializeComponent();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
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
