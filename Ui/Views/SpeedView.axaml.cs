using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Controls;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SpeedView : ToolView
{
    private static readonly string[] PublicServers = { "speedtest.wtnet.de", "ping.online.net", "iperf3.moji.fr", "iperf.par2.as49434.net" };
    private readonly List<CheckItem> _results = new();
    private CancellationTokenSource _cts;

    public SpeedView()
    {
        InitializeComponent();
        ServerBox.ItemsSource = PublicServers;
        ServerBox.Text = string.IsNullOrEmpty(AppSettings.Current.IperfServer) ? PublicServers[0] : AppSettings.Current.IperfServer;
    }

    public override void OnNavigatedTo(string parameter) => ServerBox.Text = parameter;
    public override void RunNow() => Start_Click(null, null);

    private static string Mbps(double v) => SpeedReport.Mbps(v);

    private void Busy(bool b)
    {
        StartButton.IsEnabled = !b;
        IperfButton.IsEnabled = !b;
        StopButton.IsEnabled = b;
    }

    private void AddResult(CheckItem item)
    {
        _results.Insert(0, item);
        ResultPanel.Children.Clear();
        ResultPanel.Children.Add(UiFactory.Section(new CheckSection("Ergebnisse", _results)));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Busy(true);
        DownText.Text = UpText.Text = PingText.Text = JitterText.Text = "…";
        NetChart.SeriesList.Clear();
        var down = new LineChart.Series { Name = L.T("Download"), Color = Palette.Series[0] };
        var up = new LineChart.Series { Name = L.T("Upload"), Color = Palette.Series[2] };
        NetChart.SeriesList.Add(down);
        NetChart.SeriesList.Add(up);
        NetChart.Refresh();

        try
        {
            var meta = await CloudflareSpeed.MetaAsync(ct);
            string M(string k) => meta.TryGetValue(k, out var v) ? v : "";
            NetInfo.Text = L.F("Server: Cloudflare {0} {1} · Ihre IP: {2} · {3}", M("colo"), M("city"), M("clientIp"), M("asOrganization"));

            NetStatus.Text = L.T("Messe Latenz …");
            var (lat, jit) = await CloudflareSpeed.LatencyAsync(10, ct);
            PingText.Text = lat.ToString("0.0");
            JitterText.Text = jit.ToString("0.0");

            NetStatus.Text = L.T("Messe Download …");
            var dl = await CloudflareSpeed.DownloadAsync(10, 6, new Progress<SpeedSample>(s =>
            {
                down.Values.Add(s.Mbps); up.Values.Add(null);
                DownText.Text = Mbps(s.Mbps);
                NetChart.Refresh();
            }), ct);
            DownText.Text = Mbps(dl);

            NetStatus.Text = L.T("Messe Upload …");
            var ul = await CloudflareSpeed.UploadAsync(10, 4, new Progress<SpeedSample>(s =>
            {
                up.Values.Add(s.Mbps); down.Values.Add(null);
                UpText.Text = Mbps(s.Mbps);
                NetChart.Refresh();
            }), ct);
            UpText.Text = Mbps(ul);
            NetStatus.Text = L.F("Fertig – {0:HH:mm:ss}", DateTime.Now);

            AddResult(SpeedReport.Internet(dl, ul, lat, jit, M("asOrganization"), M("colo")));
        }
        catch (OperationCanceledException) { NetStatus.Text = L.T("Abgebrochen."); }
        catch (Exception ex) { NetStatus.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally
        {
            Busy(false);
            _cts = null;
        }
    }

    private async void Iperf_Click(object sender, RoutedEventArgs e)
    {
        var host = ServerBox.Text?.Trim();
        if (string.IsNullOrEmpty(host) || _cts != null) return;
        List<int> ports;
        try { ports = PortCatalog.Parse(IperfPort.Text ?? ""); }
        catch { IperfStatus.Text = L.T("Ungültige Portangabe."); return; }
        if (ports.Count == 0) ports.Add(5201);

        AppSettings.Current.IperfServer = host;
        AppSettings.Current.Save();
        HistoryStore.Add("speed", "Speedtest & iPerf3", host);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Busy(true);
        int seconds = (int)(IperfTime.Value ?? 10);
        int streams = (int)(IperfStreams.Value ?? 4);
        var dirs = IperfDir.SelectedIndex switch { 0 => new[] { false }, 1 => new[] { true }, _ => new[] { false, true } };
        IperfUp.Text = IperfDown.Text = IperfRetr.Text = "–";
        IperfChart.SeriesList.Clear();
        var su = new LineChart.Series { Name = L.T("Upload"), Color = Palette.Series[2] };
        var sd = new LineChart.Series { Name = L.T("Download"), Color = Palette.Series[0] };
        IperfChart.SeriesList.Add(su);
        IperfChart.SeriesList.Add(sd);

        try
        {
            int portIndex = 0;
            foreach (var reverse in dirs)
            {
                var res = await SpeedReport.IperfWithFallbackAsync(host, ports, seconds, streams, reverse, new Progress<SpeedSample>(s =>
                {
                    (reverse ? sd : su).Values.Add(s.Mbps);
                    (reverse ? su : sd).Values.Add(null);
                    (reverse ? IperfDown : IperfUp).Text = Mbps(s.Mbps);
                    IperfChart.Refresh();
                }), t => IperfStatus.Text = t, () => portIndex, i => portIndex = i, ports.Count * dirs.Length, ct);
                (reverse ? IperfDown : IperfUp).Text = Mbps(res.Mbps);
                if (res.Retransmits >= 0) IperfRetr.Text = res.Retransmits.ToString("N0");
                AddResult(SpeedReport.Iperf(res, streams, seconds, host));
            }
            IperfStatus.Text = L.F("Fertig – {0:HH:mm:ss}", DateTime.Now);
        }
        catch (OperationCanceledException) { IperfStatus.Text = L.T("Abgebrochen."); }
        catch (Exception ex)
        {
            IperfStatus.Text = L.T("Fehler: ") + (ex is System.Net.Sockets.SocketException ? L.T("Server nicht erreichbar (Firewall? iperf3 -s gestartet?)") : NetUtil.ErrorText(ex));
        }
        finally
        {
            Busy(false);
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
