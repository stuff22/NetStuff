using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DashboardPage : ToolPage
{
    public class AdapterView
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public Brush StatusBrush { get; set; }
        public List<CheckItem> Items { get; set; }
    }

    private bool _loaded;

    public DashboardPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        LoadAdapters();
        PublicList.ItemsSource = new[] { new CheckItem("Öffentliche IP", "wird ermittelt …") };

        var items = new List<CheckItem>();
        try
        {
            var v4Task = SafeGet("https://api.ipify.org");
            var v6Task = SafeGet("https://api6.ipify.org");
            var geoTask = GeoIpService.SelfAsync();
            var v4 = await v4Task;
            var v6 = await v6Task;
            items.Add(new CheckItem("Öffentliche IPv4", v4 ?? "nicht ermittelbar", v4 != null ? Level.Ok : Level.Warn));
            items.Add(new CheckItem("Öffentliche IPv6", v6 ?? "keine IPv6-Konnektivität", v6 != null ? Level.Ok : Level.Info));
            try
            {
                var g = await geoTask;
                if (g?.Ok == true)
                {
                    items.Add(new CheckItem("Provider", g.Isp));
                    if (!string.IsNullOrEmpty(g.Org) && g.Org != g.Isp) items.Add(new CheckItem("Organisation", g.Org));
                    items.Add(new CheckItem("Standort (ungefähr)", g.Location));
                    items.Add(new CheckItem("AS", g.As));
                }
            }
            catch { }
            if (v4 != null)
            {
                var ptr = await NetUtil.ReverseDnsAsync(System.Net.IPAddress.Parse(v4));
                if (ptr != "") items.Add(new CheckItem("Reverse-DNS", ptr));
            }
        }
        catch (Exception ex)
        {
            items.Add(new CheckItem("Fehler", ex.Message, Level.Bad));
        }
        PublicList.ItemsSource = items;
    }

    private static async Task<string> SafeGet(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(6000);
            return (await NetUtil.Http.GetStringAsync(url, cts.Token)).Trim();
        }
        catch { return null; }
    }

    private void LoadAdapters()
    {
        var list = new List<AdapterView>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                     .Where(n => !System.Text.RegularExpressions.Regex.IsMatch(n.Description, @"-\d{4}$")) // NDIS-Filter (WFP, QoS …)
                     .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
                     .ThenByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0))
        {
            var up = nic.OperationalStatus == OperationalStatus.Up;
            var props = nic.GetIPProperties();
            var items = new List<CheckItem>
            {
                new("Typ", TypeName(nic.NetworkInterfaceType)),
                new("MAC-Adresse", NetUtil.FormatMac(nic.GetPhysicalAddress())),
            };
            if (up) items.Add(new CheckItem("Geschwindigkeit", NetUtil.FormatSpeed(nic.Speed)));

            foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                items.Add(new CheckItem("IPv4", $"{a.Address}/{a.PrefixLength}" + (a.Address.ToString().StartsWith("169.254.") ? "  (APIPA – kein DHCP!)" : ""),
                    a.Address.ToString().StartsWith("169.254.") ? Level.Warn : Level.Info));
            foreach (var a in props.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6))
                items.Add(new CheckItem("IPv6", $"{a.Address}/{a.PrefixLength}"));
            var gws = props.GatewayAddresses.Select(g => g.Address.ToString()).Where(s => s != "0.0.0.0").ToList();
            if (gws.Count > 0) items.Add(new CheckItem("Gateway", string.Join(", ", gws)));
            var dns = props.DnsAddresses.Select(d => d.ToString()).ToList();
            if (dns.Count > 0) items.Add(new CheckItem("DNS-Server", string.Join(", ", dns)));
            try
            {
                var v4 = props.GetIPv4Properties();
                if (v4 != null)
                {
                    items.Add(new CheckItem("DHCP", v4.IsDhcpEnabled ? "aktiv" : "statisch"));
                    if (v4.IsDhcpEnabled && props.DhcpServerAddresses.Count > 0)
                        items.Add(new CheckItem("DHCP-Server", string.Join(", ", props.DhcpServerAddresses)));
                    items.Add(new CheckItem("MTU", v4.Mtu.ToString()));
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(props.DnsSuffix)) items.Add(new CheckItem("DNS-Suffix", props.DnsSuffix));

            list.Add(new AdapterView
            {
                Name = nic.Name,
                Description = nic.Description,
                Status = up ? "Verbunden" : "Getrennt",
                StatusBrush = new SolidColorBrush(up ? Palette.Ok : Color.FromRgb(0x6E, 0x76, 0x81)),
                Items = items,
            });
        }
        AdapterList.ItemsSource = list;
    }

    private static string TypeName(NetworkInterfaceType t) => t switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
        NetworkInterfaceType.Wireless80211 => "WLAN",
        NetworkInterfaceType.Ppp => "PPP / VPN",
        NetworkInterfaceType.Tunnel => "Tunnel",
        NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "Mobilfunk",
        _ => t.ToString(),
    };
}
