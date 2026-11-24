using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class PathMtuPage : ToolPage
{
    public PathMtuPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host) TargetBox.Text = host;
    }

    private void Log(string s) => LogBox.AppendText(s + Environment.NewLine);

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        LogBox.Clear();
        ResultText.Text = "…";
        ResultSub.Text = "";
        try
        {
            var ip = await NetUtil.ResolveAsync(TargetBox.Text, V6Box.IsChecked == true);
            var r = await PathMtu.DiscoverAsync(ip, (int)(MaxBox.Value ?? 1500), Log);
            if (!r.Reachable)
            {
                ResultText.Text = "–";
                ResultSub.Text = "Ziel nicht per ICMP erreichbar";
                return;
            }
            ResultText.Text = r.Mtu.ToString();
            ResultSub.Text = $"Nutzlast {r.Payload} Byte · TCP-MSS {r.Mss} Byte";
            Log("");
            Log($"Ergebnis: Path-MTU = {r.Mtu} Byte (größte unfragmentierte Ping-Nutzlast: {r.Payload} Byte)");
            if (!r.IPv6) Log($"Windows-Test zum Nachprüfen:  ping -f -l {r.Payload} {ip}");
        }
        catch (Exception ex)
        {
            ResultText.Text = "–";
            Log("Fehler: " + NetUtil.ErrorText(ex));
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
