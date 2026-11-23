using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class WolDevice
{
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Target { get; set; } = "255.255.255.255";
    public int Port { get; set; } = 9;
}

public partial class WolPage : ToolPage
{
    private static readonly string StoreFile = Path.Combine(App.DataDir, "wol.json");
    private readonly ObservableCollection<WolDevice> _devices = new();

    public WolPage()
    {
        InitializeComponent();
        DeviceGrid.ItemsSource = _devices;
        try
        {
            if (File.Exists(StoreFile))
                foreach (var d in JsonSerializer.Deserialize<List<WolDevice>>(File.ReadAllText(StoreFile)) ?? new())
                    _devices.Add(d);
        }
        catch { }
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is ScanHost h)
        {
            NameBox.Text = string.IsNullOrEmpty(h.Hostname) ? h.Ip : h.Hostname.Split('.')[0];
            MacBox.Text = h.Mac;
            var local = NetUtil.ActiveIPv4().FirstOrDefault(l =>
                (NetUtil.ToUInt(l.Addr.Address) & NetUtil.PrefixToMask(l.Addr.PrefixLength)) == (h.SortKey & NetUtil.PrefixToMask(l.Addr.PrefixLength)));
            if (local.Addr != null)
                TargetBox.Text = NetUtil.FromUInt(NetUtil.ToUInt(local.Addr.Address) | ~NetUtil.PrefixToMask(local.Addr.PrefixLength)).ToString();
            StatusText.Text = "Gerät aus dem Netzwerkscan übernommen – mit „Speichern“ in die Liste aufnehmen.";
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(StoreFile, JsonSerializer.Serialize(_devices.ToList(), new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    private static byte[] ParseMac(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new Exception("MAC-Adresse muss 12 Hex-Zeichen haben.");
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    public static async Task SendAsync(string mac, string target, int port)
    {
        var m = ParseMac(mac);
        var packet = new byte[102];
        for (int i = 0; i < 6; i++) packet[i] = 0xFF;
        for (int i = 1; i <= 16; i++) Array.Copy(m, 0, packet, i * 6, 6);

        var ip = IPAddress.TryParse(target, out var t) ? t : await NetUtil.ResolveAsync(target);
        using var udp = new UdpClient(ip.AddressFamily) { EnableBroadcast = true };
        for (int i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, packet.Length, new IPEndPoint(ip, port));
            await Task.Delay(100);
        }
        // zusätzlich an alle lokalen Subnetz-Broadcasts
        if (ip.Equals(IPAddress.Broadcast))
        {
            foreach (var l in NetUtil.ActiveIPv4())
            {
                var bc = NetUtil.FromUInt(NetUtil.ToUInt(l.Addr.Address) | ~NetUtil.PrefixToMask(l.Addr.PrefixLength));
                try { await udp.SendAsync(packet, packet.Length, new IPEndPoint(bc, port)); } catch { }
            }
        }
    }

    private async Task Wake(string mac, string target, int port, string label)
    {
        try
        {
            await SendAsync(mac, target, port);
            StatusText.Text = $"Magic Packet an {label} ({mac}) über {target}:{port} gesendet – {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + ex.Message;
        }
    }

    private async void Wake_Click(object sender, RoutedEventArgs e) =>
        await Wake(MacBox.Text, TargetBox.Text.Trim(), (int)(PortBox.Value ?? 9), NameBox.Text.Trim() == "" ? "Gerät" : NameBox.Text.Trim());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { ParseMac(MacBox.Text); }
        catch (Exception ex) { StatusText.Text = ex.Message; return; }
        var dev = new WolDevice
        {
            Name = NameBox.Text.Trim() == "" ? MacBox.Text.Trim() : NameBox.Text.Trim(),
            Mac = MacBox.Text.Trim().ToUpperInvariant(),
            Target = TargetBox.Text.Trim(),
            Port = (int)(PortBox.Value ?? 9),
        };
        var existing = _devices.FirstOrDefault(d => d.Mac.Equals(dev.Mac, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _devices.Remove(existing);
        _devices.Add(dev);
        Persist();
        StatusText.Text = $"„{dev.Name}“ gespeichert.";
    }

    private async void WakeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is WolDevice d) await Wake(d.Mac, d.Target, d.Port, d.Name);
    }

    private void DeviceGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => WakeSelected_Click(sender, e);

    private void DeviceGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is not WolDevice d) return;
        NameBox.Text = d.Name;
        MacBox.Text = d.Mac;
        TargetBox.Text = d.Target;
        PortBox.Value = d.Port;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is WolDevice d)
        {
            _devices.Remove(d);
            Persist();
        }
    }
}
