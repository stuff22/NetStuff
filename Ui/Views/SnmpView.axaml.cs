using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Controls;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SnmpView : ToolView
{
    private CancellationTokenSource _trafficCts;

    public SnmpView()
    {
        InitializeComponent();
        var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).FirstOrDefault(g => g != null);
        HostBox.Text = gw?.ToString() ?? "";
    }

    public override void OnNavigatedTo(string parameter) => HostBox.Text = parameter;
    public override void RunNow() => System_Click(null, null);

    private async Task<SnmpService> Client()
    {
        var ip = await NetUtil.ResolveAsync(HostBox.Text ?? "");
        return new SnmpService(ip, (int)(PortBox.Value ?? 161), (CommunityBox.Text ?? "").Trim(), VersionBox.SelectedIndex == 1);
    }

    private enum Show { Panel, Interfaces, Walk }

    private void ShowOnly(Show what)
    {
        ResultScroll.IsVisible = what == Show.Panel;
        InterfaceGrid.IsVisible = what == Show.Interfaces;
        WalkGrid.IsVisible = what == Show.Walk;
        if (what == Show.Panel) ResultsPanel.Children.Clear();
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
            ShowOnly(Show.Panel);
            ResultsPanel.Children.Add(UiFactory.Message(SnmpReport.ErrorText(ex), Level.Bad));
            StatusText.Text = "";
        }
    }

    private async void System_Click(object sender, RoutedEventArgs e) => await Run("Systeminfo", async c =>
    {
        var info = await c.SystemInfoAsync();
        ShowOnly(Show.Panel);
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.System(info)));
        return L.F("Antwort von {0}", HostBox.Text);
    });

    private async void Interfaces_Click(object sender, RoutedEventArgs e) => await Run("Schnittstellen werden gelesen", async c =>
    {
        var list = await c.InterfacesAsync();
        InterfaceGrid.ItemsSource = list;
        ShowOnly(Show.Interfaces);
        return L.F("{0} Schnittstellen · {1} aktiv · Doppelklick auf einen Port zeigt den Live-Traffic", list.Count, list.Count(i => i.Status == "up"));
    });

    private async void Lldp_Click(object sender, RoutedEventArgs e) => await Run("LLDP-Nachbarn werden gelesen", async c =>
    {
        var n = await c.LldpNeighborsAsync();
        ShowOnly(Show.Panel);
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.LldpNeighbors(n)));
        return L.F("{0} Nachbarn", n.Count);
    });

    private async void Supplies_Click(object sender, RoutedEventArgs e) => await Run("Verbrauchsmaterial wird gelesen", async c =>
    {
        var s = await c.SuppliesAsync();
        ShowOnly(Show.Panel);
        ResultsPanel.Children.Add(UiFactory.Section(SnmpReport.Supplies(s)));
        return s.Count == 0 ? "" : L.F("{0} Einträge", s.Count);
    });

    private async void Walk_Click(object sender, RoutedEventArgs e) => await Run("Walk läuft", async c =>
    {
        var rows = await SnmpReport.WalkAsync(c, (OidBox.Text ?? "").Trim());
        WalkGrid.ItemsSource = rows;
        ShowOnly(Show.Walk);
        return L.F("{0} Werte", rows.Count);
    });

    private async void InterfaceGrid_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (InterfaceGrid.SelectedItem is not SnmpInterface itf) return;
        _trafficCts?.Cancel();
        _trafficCts = new CancellationTokenSource();
        var ct = _trafficCts.Token;
        StopButton.IsEnabled = true;
        TrafficCard.IsVisible = true;
        TrafficChart.SeriesList.Clear();
        var sIn = new LineChart.Series { Name = L.F("{0} ↓ empfangen", itf.Name), Color = Palette.Series[0] };
        var sOut = new LineChart.Series { Name = L.T("↑ gesendet"), Color = Palette.Series[2] };
        TrafficChart.SeriesList.Add(sIn);
        TrafficChart.SeriesList.Add(sOut);
        TrafficChart.Refresh();
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
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        StopButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _trafficCts?.Cancel();
}
