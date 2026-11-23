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
            bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            int overhead = v6 ? 48 : 28;
            int maxMtu = (int)(MaxBox.Value ?? 1500);
            Log($"Ziel: {ip}  ({(v6 ? "IPv6, Header 40 + ICMPv6 8" : "IPv4, Header 20 + ICMP 8")} Byte)");

            using var ping = new Ping();
            async Task<IPStatus> Probe(int payload)
            {
                IPStatus last = IPStatus.Unknown;
                for (int i = 0; i < 2; i++)
                {
                    try
                    {
                        var r = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(2000), new byte[payload], new PingOptions(64, true));
                        last = r.Status;
                        if (last == IPStatus.Success || last == IPStatus.PacketTooBig) return last;
                    }
                    catch (PingException ex) { Log("  Fehler: " + (ex.InnerException?.Message ?? ex.Message)); last = IPStatus.PacketTooBig; }
                }
                return last;
            }

            var first = await Probe(32);
            if (first != IPStatus.Success)
            {
                Log("Ziel antwortet nicht auf Ping (" + PingPage.StatusText(first) + ") – Messung nicht möglich.");
                ResultText.Text = "–";
                ResultSub.Text = "Ziel nicht per ICMP erreichbar";
                return;
            }

            int lo = 32, hi = maxMtu - overhead;
            if (await Probe(hi) == IPStatus.Success)
            {
                lo = hi;
                Log($"  {hi + overhead,5} Byte  ✓  (Maximum erreicht)");
            }
            else
            {
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    var st = await Probe(mid);
                    Log($"  {mid + overhead,5} Byte  {(st == IPStatus.Success ? "✓" : "✗  " + PingPage.StatusText(st))}");
                    if (st == IPStatus.Success) lo = mid;
                    else hi = mid - 1;
                }
            }

            int mtu = lo + overhead;
            ResultText.Text = mtu.ToString();
            ResultSub.Text = $"Nutzlast {lo} Byte · TCP-MSS {mtu - (v6 ? 60 : 40)} Byte";
            Log("");
            Log($"Ergebnis: Path-MTU = {mtu} Byte (größte unfragmentierte Ping-Nutzlast: {lo} Byte)");
            if (!v6) Log($"Windows-Test zum Nachprüfen:  ping -f -l {lo} {ip}");
        }
        catch (Exception ex)
        {
            ResultText.Text = "–";
            Log("Fehler: " + ex.Message);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
