using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class WolView : ToolView
{
    private readonly ObservableCollection<WolDevice> _devices = new(WakeOnLan.LoadDevices());

    public WolView()
    {
        InitializeComponent();
        DeviceGrid.ItemsSource = _devices;
    }

    /// <summary>Parameter für die Übernahme eines Geräts aus dem Netzwerkscan.</summary>
    public static string Parameter(string mac, string name, string target) => $"{mac}|{name}|{target}";

    public override void OnNavigatedTo(string parameter)
    {
        var p = (parameter ?? "").Split('|');
        MacBox.Text = p[0];
        if (p.Length > 1) NameBox.Text = p[1];
        if (p.Length > 2) TargetBox.Text = p[2];
        if (p.Length > 1) StatusText.Text = L.T("Gerät aus dem Netzwerkscan übernommen – mit „Speichern“ in die Liste aufnehmen.");
    }

    public override void RunNow() => Wake_Click(null, null);

    private async Task Wake(string mac, string target, int port, string label)
    {
        try
        {
            await WakeOnLan.SendAsync(mac, target, port);
            StatusText.Text = L.F("Magic Packet an {0} ({1}) über {2}:{3} gesendet – {4:HH:mm:ss}", label, mac, target, port, DateTime.Now);
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
        }
    }

    private async void Wake_Click(object sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? "").Trim();
        await Wake(MacBox.Text ?? "", (TargetBox.Text ?? "").Trim(), (int)(PortBox.Value ?? 9), name == "" ? L.T("Gerät") : name);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { WakeOnLan.ParseMac(MacBox.Text ?? ""); }
        catch (Exception ex) { StatusText.Text = NetUtil.ErrorText(ex); return; }
        var mac = (MacBox.Text ?? "").Trim().ToUpperInvariant();
        var name = (NameBox.Text ?? "").Trim();
        var dev = new WolDevice
        {
            Name = name == "" ? mac : name,
            Mac = mac,
            Target = (TargetBox.Text ?? "").Trim(),
            Port = (int)(PortBox.Value ?? 9),
        };
        var existing = _devices.FirstOrDefault(d => d.Mac.Equals(dev.Mac, StringComparison.OrdinalIgnoreCase));
        if (existing != null) _devices.Remove(existing);
        _devices.Add(dev);
        WakeOnLan.SaveDevices(_devices);
        StatusText.Text = L.F("„{0}“ gespeichert.", dev.Name);
    }

    private async void WakeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceGrid.SelectedItem is WolDevice d) await Wake(d.Mac, d.Target, d.Port, d.Name);
    }

    private void DeviceGrid_DoubleTapped(object sender, TappedEventArgs e) => WakeSelected_Click(sender, e);

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
