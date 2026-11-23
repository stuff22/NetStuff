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
            var msg = ex.GetType().Name.Contains("Timeout") || ex is System.Net.Sockets.SocketException || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? L.T("Keine Antwort – SNMP am Gerät aktiviert? Community und Version korrekt? UDP 161 erreichbar?")
                : ex.Message;
            ResultsPanel.Children.Add(UiFactory.Message(msg, Level.Bad));
            StatusText.Text = "";
        }
    }

    private async void System_Click(object sender, RoutedEventArgs e) => await Run("Systeminfo", async c =>
    {
        var info = await c.SystemInfoAsync();
        ShowPanel();
        var labels = new Dictionary<string, string>
        {
            ["sysName"] = "Name", ["sysDescr"] = "Beschreibung", ["sysUpTime"] = "Laufzeit", ["sysContact"] = "Kontakt",
            ["sysLocation"] = "Standort", ["sysObjectID"] = "Objekt-ID", ["sysServices"] = "Dienste",
        };
        ResultsPanel.Children.Add(UiFactory.Section("System", info.Select(i => new CheckItem(labels.GetValueOrDefault(i.Name, i.Name), i.Value, Level.Info))));
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
        ResultsPanel.Children.Add(n.Count == 0
            ? UiFactory.Message("Keine LLDP-Nachbarn gefunden (LLDP am Gerät deaktiviert oder MIB nicht unterstützt).", Level.Warn)
            : UiFactory.Section("LLDP-Nachbarn", n.Select(x => new CheckItem(x.LocalPort, $"{x.Neighbor} · Port {x.RemotePort}" + (x.Description != "" ? $"\n{x.Description}" : ""), Level.Info))));
        return L.F("{0} Nachbarn", n.Count);
    });

    private async void Supplies_Click(object sender, RoutedEventArgs e) => await Run("Verbrauchsmaterial wird gelesen", async c =>
    {
        var s = await c.SuppliesAsync();
        ShowPanel();
        if (s.Count == 0)
        {
            ResultsPanel.Children.Add(UiFactory.Message("Keine Printer-MIB-Daten – kein Drucker oder SNMP eingeschränkt.", Level.Warn));
            return "";
        }
        ResultsPanel.Children.Add(UiFactory.Section("Verbrauchsmaterial", s.Select(x =>
        {
            if (x.Level >= 0 && x.Max > 0)
            {
                var pct = 100.0 * x.Level / x.Max;
                return new CheckItem(x.Name, $"{pct:0} %  ({x.Level}/{x.Max})", pct < 10 ? Level.Bad : pct < 25 ? Level.Warn : Level.Ok);
            }
            return new CheckItem(x.Name, x.Level == -3 ? L.T("vorhanden (Füllstand nicht messbar)") : x.Level == -2 ? L.T("unbekannt") : x.Level.ToString(), Level.Info);
        })));
        return L.F("{0} Einträge", s.Count);
    });

    private async void Walk_Click(object sender, RoutedEventArgs e) => await Run("Walk läuft", async c =>
    {
        var vars = await c.WalkAsync(OidBox.Text.Trim());
        var rows = vars.Select(v => new SnmpRow
        {
            Oid = v.Id.ToString(),
            Name = SnmpService.KnownOids.GetValueOrDefault(v.Id.ToString(), ""),
            Type = v.Data.TypeCode.ToString(),
            Value = SnmpService.Format(v.Data),
        }).ToList();
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
            var (lastIn, lastOut) = await c.CountersAsync(itf.Index);
            var last = DateTime.Now;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                var (i, o) = await c.CountersAsync(itf.Index);
                var dt = (DateTime.Now - last).TotalSeconds;
                last = DateTime.Now;
                double inMbps = i >= lastIn ? (i - lastIn) * 8 / 1e6 / dt : 0;
                double outMbps = o >= lastOut ? (o - lastOut) * 8 / 1e6 / dt : 0;
                lastIn = i; lastOut = o;
                sIn.Values.Add(inMbps);
                sOut.Values.Add(outMbps);
                TrafficChart.Refresh();
                StatusText.Text = L.F("Live-Traffic {0}: ↓ {1:0.00} Mbit/s · ↑ {2:0.00} Mbit/s", itf.Name, inMbps, outMbps);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + ex.Message; }
        StopButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _trafficCts?.Cancel();
}
