using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DhcpPage : ToolPage
{
    public class AdapterChoice
    {
        public string Label { get; init; }
        public IPAddress Ip { get; init; }
        public byte[] Mac { get; init; }
    }

    public DhcpPage()
    {
        InitializeComponent();
        var list = NetUtil.ActiveIPv4()
            .Where(x => x.Nic.GetPhysicalAddress().GetAddressBytes().Length == 6)
            .OrderByDescending(x => x.Gateway != null)
            .Select(x => new AdapterChoice
            {
                Label = $"{x.Nic.Name} – {x.Addr.Address}/{x.Addr.PrefixLength}",
                Ip = x.Addr.Address,
                Mac = x.Nic.GetPhysicalAddress().GetAddressBytes(),
            }).ToList();
        AdapterBox.ItemsSource = list;
        if (list.Count > 0) AdapterBox.SelectedIndex = 0;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not AdapterChoice a) return;
        StartButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        int wait = (int)(WaitBox.Value ?? 6);
        StatusText.Text = L.F("DHCPDISCOVER gesendet – warte {0} s auf Angebote …", wait);
        try
        {
            var mac = a.Mac;
            if (RandomMacBox.IsChecked == true)
            {
                mac = new byte[6];
                Random.Shared.NextBytes(mac);
                mac[0] = (byte)((mac[0] & 0xFE) | 0x02);
            }
            _ = OuiDatabase.EnsureLoadedAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(wait + 5));
            var offers = await DhcpTester.DiscoverAsync(a.Ip, mac, wait * 1000, cts.Token);

            if (offers.Count == 0)
            {
                ResultsPanel.Children.Add(UiFactory.Message(
                    "Kein DHCP-Server hat geantwortet. Mögliche Ursachen: statische IP-Konfiguration im Netz, Windows-Firewall blockiert eingehende Antworten (bei Nachfrage Zugriff erlauben) oder DHCP-Server antwortet nur bekannten Geräten.", Level.Warn));
            }
            else
            {
                var servers = offers.Select(o => o.Server).Distinct().ToList();
                ResultsPanel.Children.Add(servers.Count > 1
                    ? UiFactory.Section("Bewertung", new[] { new CheckItem("Mehrere DHCP-Server!", L.F("{0} verschiedene Server antworten: {1} – das führt zu IP-Konflikten. Fremden Server suchen (z. B. versehentlich angeschlossener Router).", servers.Count, string.Join(", ", servers)), Level.Bad) })
                    : UiFactory.Section("Bewertung", new[] { new CheckItem("DHCP-Server", L.F("Genau ein Server antwortet ({0}) ✓", servers[0]), Level.Ok) }));
            }

            foreach (var o in offers)
            {
                var items = new List<CheckItem>
                {
                    new("DHCP-Server", o.Server + (o.RelayOrSource != o.Server ? L.F("  (Antwort über {0} – Relay?)", o.RelayOrSource) : ""), Level.Ok),
                    new("Angebotene IP", o.OfferedIp),
                    new("Subnetzmaske", o.SubnetMask),
                    new("Gateway", o.Router),
                    new("DNS-Server", o.Dns),
                };
                if (o.Domain != "") items.Add(new CheckItem("Domain", o.Domain));
                if (o.Lease != "") items.Add(new CheckItem("Lease-Dauer", o.Lease));
                if (o.Ntp != "") items.Add(new CheckItem("NTP-Server", o.Ntp));
                if (o.BootServer != "" || o.BootFile != "") items.Add(new CheckItem("PXE / Boot", $"{o.BootServer} {o.BootFile}".Trim()));
                if (o.Other != "") items.Add(new CheckItem("Weitere Optionen", o.Other));
                items.Add(new CheckItem("Antwortzeit", $"{o.Ms:0} ms"));
                var vendor = "";
                try
                {
                    var smac = NetUtil.Arp(IPAddress.Parse(o.Server));
                    if (smac != null) { var m = NetUtil.FormatMac(smac); vendor = $"{m} ({OuiDatabase.Lookup(m)})"; }
                }
                catch { }
                if (vendor != "") items.Add(new CheckItem("Server-MAC / Hersteller", vendor));
                ResultsPanel.Children.Add(UiFactory.Section(L.F("Angebot von {0}", o.Server), items));
            }
            StatusText.Text = L.F("{0} Angebot(e) erhalten", offers.Count);
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Bad));
            StatusText.Text = "";
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
