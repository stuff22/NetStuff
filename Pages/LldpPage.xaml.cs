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
            if (list.Count == 0)
            {
                ResultsPanel.Children.Add(UiFactory.Message(
                    "Keine LLDP-/CDP-Pakete empfangen. Mögliche Ursachen: LLDP/CDP am Switch deaktiviert, unmanaged Switch, WLAN-Verbindung (LLDP gibt es nur am Kabel) oder Dauer zu kurz.", Level.Warn));
            }
            foreach (var n in list)
            {
                var items = new List<CheckItem>
                {
                    new("Gerät", n.DeviceName == "" ? "(kein Name übermittelt)" : n.DeviceName, Level.Ok),
                    new("Switch-Port", n.PortId + (n.PortDescription != "" && n.PortDescription != n.PortId ? $"  ({n.PortDescription})" : ""), Level.Ok),
                };
                if (n.Vlan != "") items.Add(new CheckItem("VLAN (Port / nativ)", n.Vlan, Level.Ok));
                if (n.VoiceVlan != "") items.Add(new CheckItem("Voice-VLAN", n.VoiceVlan));
                if (n.ManagementIp != "") items.Add(new CheckItem("Management-IP", n.ManagementIp));
                if (n.ChassisId != "") items.Add(new CheckItem("Chassis-ID", n.ChassisId));
                if (n.Platform != "") items.Add(new CheckItem("Plattform", n.Platform));
                if (n.Software != "") items.Add(new CheckItem("Systembeschreibung", n.Software.Length > 300 ? n.Software[..300] + " …" : n.Software));
                if (n.Capabilities != "") items.Add(new CheckItem("Funktionen", n.Capabilities));
                if (n.Duplex != "") items.Add(new CheckItem("Geschwindigkeit / Duplex", n.Duplex + (n.AutoNeg != "" ? L.F(" · Autonegotiation {0}", L.T(n.AutoNeg)) : "")));
                if (n.MaxFrame != "") items.Add(new CheckItem("Max. Framegröße", n.MaxFrame + " Byte"));
                if (n.Poe != "") items.Add(new CheckItem("PoE", n.Poe));
                items.Add(new CheckItem("Absender-MAC", $"{n.SourceMac}  ({OuiDatabase.Lookup(n.SourceMac)})"));
                ResultsPanel.Children.Add(UiFactory.Section($"{n.Protocol}: {n.DeviceName}", items));
            }
            StatusText.Text = L.F("{0} Nachbar(n) gefunden", list.Count);
        }
        catch (OperationCanceledException) { StatusText.Text = L.T("Abgebrochen."); }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Bad));
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
