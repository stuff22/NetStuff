using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class SurveyPoint
{
    public string Room { get; set; }
    public string Note { get; set; }
    public int Rssi { get; set; }
    public string RssiText => Rssi == 0 ? "–" : $"{Rssi} dBm";
    public string Ssid { get; set; }
    public string Bssid { get; set; }
    public string Channel { get; set; }
    public string Rate { get; set; }
    public string Ping { get; set; }
    public string Speed { get; set; }
    public string SameSsid { get; set; }
    public string Time { get; set; }
    public Level Level => Rssi == 0 ? Level.Bad : Rssi >= -60 ? Level.Ok : Rssi >= -70 ? Level.Info : Rssi >= -80 ? Level.Warn : Level.Bad;
    public string Rating => L.T(Rssi == 0 ? "keine Verbindung" : Rssi >= -60 ? "Gut" : Rssi >= -70 ? "Mittel" : Rssi >= -80 ? "Schwach" : "Kritisch");
}

public partial class WlanSurveyPage : ToolPage
{
    private readonly ObservableCollection<SurveyPoint> _points = new(JsonStore.LoadList<SurveyPoint>("survey.json"));

    public WlanSurveyPage()
    {
        InitializeComponent();
        SurveyGrid.ItemsSource = _points;
    }

    private void Save() => JsonStore.SaveList("survey.json", _points);

    private void RoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Measure_Click(sender, e);
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        var room = RoomBox.Text.Trim();
        if (room == "") room = L.F("Messpunkt {0}", _points.Count + 1);
        StartButton.IsEnabled = false;
        try
        {
            StatusText.Text = L.T("Scanne WLAN …");
            using var api = new WlanApi();
            var iface = api.Interfaces().FirstOrDefault() ?? throw new Exception(L.T("Kein WLAN-Adapter gefunden."));
            var conn = api.CurrentConnection(iface.Id);
            api.Scan(iface.Id);
            await Task.Delay(3500);
            List<WlanBss> bss;
            try { bss = await Task.Run(() => api.GetBssList(iface.Id)); }
            catch (WlanException ex) when (ex.LocationDenied) { throw new Exception(L.T("Standortzugriff fehlt – siehe WLAN-Scanner.")); }

            var p = new SurveyPoint { Room = room, Note = NoteBox.Text.Trim(), Time = DateTime.Now.ToString("HH:mm"), Ping = "–", Speed = "–" };
            if (conn != null)
            {
                var me = bss.FirstOrDefault(b => b.Bssid == conn.Bssid);
                p.Ssid = conn.Ssid;
                p.Bssid = conn.Bssid;
                p.Rssi = me?.Rssi ?? (int)(conn.SignalQuality / 2.0 - 100);
                p.Channel = me != null ? $"{me.Channel} · {me.Band} · {me.WidthMhz} MHz" : "";
                p.Rate = L.F("↓{0} / ↑{1} Mbit/s", conn.RxMbps, conn.TxMbps);
                var same = bss.Where(b => b.Ssid == conn.Ssid).ToList();
                var better = same.Where(b => b.Bssid != conn.Bssid && b.Rssi > p.Rssi + 8).OrderByDescending(b => b.Rssi).FirstOrDefault();
                p.SameSsid = same.Count.ToString() + (better != null ? L.F(" · besserer AP {0} ({1} dBm)", better.Bssid, better.Rssi) : "");

                StatusText.Text = L.T("Ping zum Gateway …");
                var gw = NetUtil.ActiveIPv4().Where(x => x.Nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211).Select(x => x.Gateway).FirstOrDefault(g => g != null);
                if (gw != null)
                {
                    var rtts = new List<long>();
                    int lost = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        var r = await NetUtil.PingAsync(gw, 1000);
                        if (r.HasValue) rtts.Add(r.Value); else lost++;
                    }
                    p.Ping = rtts.Count > 0 ? $"{rtts.Average():0} ms" + (lost > 0 ? $" ({lost}/5 ✗)" : "") : "✗";
                }

                if (SpeedBox.IsChecked == true)
                {
                    StatusText.Text = L.T("Messe Durchsatz …");
                    try
                    {
                        using var cts = new CancellationTokenSource(15000);
                        var mbps = await CloudflareSpeed.DownloadAsync(5, 4, null, cts.Token);
                        p.Speed = $"{mbps:0} Mbit/s";
                    }
                    catch { p.Speed = "✗"; }
                }
            }
            else
            {
                p.Ssid = L.T("(nicht verbunden)");
                p.SameSsid = bss.Count.ToString();
            }

            _points.Add(p);
            Save();
            RoomBox.Text = NoteBox.Text = "";
            RoomBox.Focus();
            StatusText.Text = L.F("„{0}“ aufgenommen: {1} ({2})", p.Room, p.RssiText, p.Rating);
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in SurveyGrid.SelectedItems.Cast<SurveyPoint>().ToList()) _points.Remove(p);
        Save();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(L.T("Alle Messpunkte löschen?"), "Net-Stuff", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _points.Clear();
        Save();
    }
}
