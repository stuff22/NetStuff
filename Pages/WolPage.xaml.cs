using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class WolPage : ToolPage
{
    private readonly ObservableCollection<WolDevice> _devices = new(WakeOnLan.LoadDevices());

    public WolPage()
    {
        InitializeComponent();
        DeviceGrid.ItemsSource = _devices;
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is ScanHost h)
        {
            NameBox.Text = string.IsNullOrEmpty(h.Hostname) ? h.Ip : h.Hostname.Split('.')[0];
            MacBox.Text = h.Mac;
            var bc = WakeOnLan.SubnetBroadcastFor(h.SortKey);
            if (bc != null) TargetBox.Text = bc.ToString();
            StatusText.Text = "Gerät aus dem Netzwerkscan übernommen – mit „Speichern“ in die Liste aufnehmen.";
        }
    }

    private async Task Wake(string mac, string target, int port, string label)
    {
        try
        {
            await WakeOnLan.SendAsync(mac, target, port);
            StatusText.Text = $"Magic Packet an {label} ({mac}) über {target}:{port} gesendet – {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Fehler: " + NetUtil.ErrorText(ex);
        }
    }

    private async void Wake_Click(object sender, RoutedEventArgs e) =>
        await Wake(MacBox.Text, TargetBox.Text.Trim(), (int)(PortBox.Value ?? 9), NameBox.Text.Trim() == "" ? "Gerät" : NameBox.Text.Trim());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { WakeOnLan.ParseMac(MacBox.Text); }
        catch (Exception ex) { StatusText.Text = NetUtil.ErrorText(ex); return; }
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
        WakeOnLan.SaveDevices(_devices);
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
            WakeOnLan.SaveDevices(_devices);
        }
    }
}
