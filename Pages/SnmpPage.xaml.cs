using System.Windows;
using System.Windows.Input;
using NetStuff.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SnmpPage : ToolPage
{
    private CancellationTokenSource _trafficCts;

    public SnmpPage()
    {
        InitializeComponent();
        var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).FirstOrDefault(g => g != null);
        HostBox.Text = gw?.ToString() ?? "";
        ResultGrid.AutoGeneratingColumn += (_, e) =>
        {
            e.Column.Header = L.T(e.PropertyName switch
            {
                "Index" => "Index", "Name" => "Name", "Alias" => "Beschreibung", "Status" => "Status", "Speed" => "Geschwindigkeit",
                "Mac" => "MAC", "InOctets" => "Empfangen", "OutOctets" => "Gesendet", "Errors" => "Fehler (in)", "Vlan" => "VLAN",
                "Oid" => "OID", "Type" => "Typ", "Value" => "Wert", _ => e.PropertyName,
            });
        };
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host) HostBox.Text = host;
    }

    private async Task<SnmpService> Client()
    {
        var ip = await NetUtil.ResolveAsync(HostBox.Text);
        return new SnmpService(ip, (int)(PortBox.Value ?? 161), CommunityBox.Text.Trim(), VersionBox.SelectedIndex == 1);
    }

    private void ShowGrid(object items)
    {
        ResultsPanel.Children.Clear();
        ResultScroll.Visibility = Visibility.Collapsed;
        ResultGrid.Visibility = Visibility.Visible;
        ResultGrid.ItemsSource = null;
        ResultGrid.ItemsSource = items as System.Collections.IEnumerable;
    }

    private void ShowPanel()
    {
        ResultGrid.Visibility = Visibility.Collapsed;
        ResultScroll.Visibility = Visibility.Visible;
        ResultsPanel.Children.Clear();
    }

    private async Task Run(string label, Func<SnmpService, Task<string>> action)
    {
        StatusText.Text = L.T(label) + " …";
        try
        {
            var c = await Client();
            StatusText.Text = await action(c);
        }
        catch (Exception ex)
        {
            ShowPanel();
            ResultsPanel.Children.Add(UiFactory.Message(SnmpReport.ErrorText(ex), Level.Bad));
            StatusText.Text = "";
        }
    }

    private async void System_Click(object sender, RoutedEventArgs e) => await Run("Systeminfo", async c =>
    {
        var info = await c.SystemInfoAsync();
        ShowPanel();
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.System(info)));
        return L.F("Antwort von {0}", HostBox.Text);
    });

    private async void Interfaces_Click(object sender, RoutedEventArgs e) => await Run("Schnittstellen werden gelesen", async c =>
    {
        var list = await c.InterfacesAsync();
        ShowGrid(list);
        return L.F("{0} Schnittstellen · {1} aktiv · Doppelklick auf einen Port zeigt den Live-Traffic", list.Count, list.Count(i => i.Status == "up"));
    });

    private async void Lldp_Click(object sender, RoutedEventArgs e) => await Run("LLDP-Nachbarn werden gelesen", async c =>
    {
        var n = await c.LldpNeighborsAsync();
        ShowPanel();
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.LldpNeighbors(n)));
        return L.F("{0} Nachbarn", n.Count);
    });

    private async void Supplies_Click(object sender, RoutedEventArgs e) => await Run("Verbrauchsmaterial wird gelesen", async c =>
    {
        var s = await c.SuppliesAsync();
        ShowPanel();
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.Supplies(s)));
        if (s.Count == 0) return "";
        return L.F("{0} Einträge", s.Count);
    });

    private async void Walk_Click(object sender, RoutedEventArgs e) => await Run("Walk läuft", async c =>
    {
        var rows = await SnmpReport.WalkAsync(c, OidBox.Text.Trim());
        ShowGrid(rows);
        return L.F("{0} Werte", rows.Count);
    });

    private async void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultGrid.SelectedItem is not SnmpInterface itf) return;
        _trafficCts?.Cancel();
        _trafficCts = new CancellationTokenSource();
        var ct = _trafficCts.Token;
        StopButton.IsEnabled = true;
        TrafficCard.Visibility = Visibility.Visible;
        TrafficChart.SeriesList.Clear();
        var sIn = new LineChart.Series { Name = L.F("{0} ↓ empfangen", itf.Name), Color = Palette.Series[0] };
        var sOut = new LineChart.Series { Name = L.T("↑ gesendet"), Color = Palette.Series[2] };
        TrafficChart.SeriesList.Add(sIn);
        TrafficChart.SeriesList.Add(sOut);
        try
        {
            var c = await Client();
            await SnmpReport.TrafficAsync(c, itf.Index, 2000, (inMbps, outMbps) =>
            {
                sIn.Values.Add(inMbps);
                sOut.Values.Add(outMbps);
                TrafficChart.Refresh();
                StatusText.Text = L.F("Live-Traffic {0}: ↓ {1:0.00} Mbit/s · ↑ {2:0.00} Mbit/s", itf.Name, inMbps, outMbps);
            }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + ex.Message; }
        StopButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _trafficCts?.Cancel();
}
