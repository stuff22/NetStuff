using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class PathMtuView : ToolView
{
    public PathMtuView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(string parameter) => TargetBox.Text = parameter;
    public override void RunNow() => Start_Click(null, null);

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private void Log(string s) => Dispatcher.UIThread.Post(() =>
    {
        LogBox.Text += s + Environment.NewLine;
        LogBox.CaretIndex = LogBox.Text.Length;
    });

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled || NetUtil.CleanHost(TargetBox.Text) == "") return;
        StartButton.IsEnabled = false;
        LogBox.Text = "";
        ResultText.Text = "…";
        ResultSub.Text = "";
        try
        {
            var ip = await NetUtil.ResolveAsync(TargetBox.Text, V6Box.IsChecked == true);
            var r = await PathMtu.DiscoverAsync(ip, (int)(MaxBox.Value ?? 1500), Log);
            if (!r.Reachable)
            {
                ResultText.Text = "–";
                ResultSub.Text = L.T("Ziel nicht per ICMP erreichbar");
                return;
            }
            ResultText.Text = r.Mtu.ToString();
            ResultSub.Text = L.F("Nutzlast {0} Byte · TCP-MSS {1} Byte", r.Payload, r.Mss);
            Log("");
            Log(L.F("Ergebnis: Path-MTU = {0} Byte (größte unfragmentierte Ping-Nutzlast: {1} Byte)", r.Mtu, r.Payload));
            if (!r.IPv6)
                Log(OperatingSystem.IsWindows()
                    ? L.F("Windows-Test zum Nachprüfen:  ping -f -l {0} {1}", r.Payload, ip)
                    : L.F("Test im Terminal zum Nachprüfen:  ping -D -s {0} {1}", r.Payload, ip));
        }
        catch (Exception ex)
        {
            ResultText.Text = "–";
            Log(L.T("Fehler: ") + NetUtil.ErrorText(ex));
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
