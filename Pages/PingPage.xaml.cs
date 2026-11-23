using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using NetStuff.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class PingTarget : Bindable
{
    private int _sent, _recv;
    private double? _last;
    private double _min = double.MaxValue, _max, _sum, _jitterSum;
    private int _jitterCount;
    private string _status = "";
    private string _ip = "";

    public string Host { get; init; }
    public Brush ColorBrush { get; init; }
    public LineChart.Series Series { get; init; }

    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public int Sent => _sent;
    public string Status { get => _status; set => Set(ref _status, value); }

    public string LossText => _sent == 0 ? "–" : $"{100.0 * (_sent - _recv) / _sent:0.#} %";
    public string LastText => _last.HasValue ? $"{_last:0} ms" : "–";
    public string MinText => _recv == 0 ? "–" : $"{_min:0} ms";
    public string MaxText => _recv == 0 ? "–" : $"{_max:0} ms";
    public string AvgText => _recv == 0 ? "–" : $"{_sum / _recv:0.#} ms";
    public string JitterText => _jitterCount == 0 ? "–" : $"{_jitterSum / _jitterCount:0.#} ms";

    public void Add(double? rtt, string status)
    {
        _sent++;
        if (rtt.HasValue)
        {
            if (_last.HasValue) { _jitterSum += Math.Abs(rtt.Value - _last.Value); _jitterCount++; }
            _recv++;
            _min = Math.Min(_min, rtt.Value);
            _max = Math.Max(_max, rtt.Value);
            _sum += rtt.Value;
        }
        _last = rtt;
        Status = status;
        Series.Values.Add(rtt);
        if (Series.Values.Count > 2000) Series.Values.RemoveRange(0, 1000);
        foreach (var p in new[] { nameof(Sent), nameof(LossText), nameof(LastText), nameof(MinText), nameof(MaxText), nameof(AvgText), nameof(JitterText) })
            Raise(p);
    }
}

public partial class PingPage : ToolPage
{
    private readonly ObservableCollection<PingTarget> _targets = new();
    private CancellationTokenSource _cts;

    public PingPage()
    {
        InitializeComponent();
        StatsGrid.ItemsSource = _targets;
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host && _cts == null) TargetsBox.Text = host;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var hosts = TargetsBox.Text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NetUtil.CleanHost).Where(h => h.Length > 0).Distinct().Take(12).ToList();
        if (hosts.Count == 0) return;

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _targets.Clear();
        Chart.SeriesList.Clear();

        int interval = (int)(IntervalBox.Value ?? 1000);
        int timeout = (int)(TimeoutBox.Value ?? 2000);
        int size = (int)(SizeBox.Value ?? 32);
        bool df = DfBox.IsChecked == true;

        for (int i = 0; i < hosts.Count; i++)
        {
            var color = Palette.Series[i % Palette.Series.Length];
            var series = new LineChart.Series { Name = hosts[i], Color = color };
            Chart.SeriesList.Add(series);
            _targets.Add(new PingTarget { Host = hosts[i], ColorBrush = new SolidColorBrush(color), Series = series });
        }

        var ct = _cts.Token;
        var tasks = _targets.Select(t => RunAsync(t, interval, timeout, size, df, ct)).ToList();
        var redraw = RedrawLoop(ct);
        await Task.WhenAll(tasks);
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        _cts = null;
    }

    private async Task RedrawLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Chart.Refresh();
            try { await Task.Delay(500, ct); } catch { break; }
        }
        Chart.Refresh();
    }

    private static async Task RunAsync(PingTarget t, int interval, int timeout, int size, bool df, CancellationToken ct)
    {
        IPAddress ip;
        try
        {
            ip = await NetUtil.ResolveAsync(t.Host, false, ct);
            t.Ip = ip.ToString();
        }
        catch (Exception ex)
        {
            t.Status = "DNS-Fehler: " + ex.Message;
            return;
        }

        var buffer = new byte[size];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)('a' + i % 23);
        var options = new PingOptions(128, df);
        using var ping = new Ping();

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Bewusst ohne CancellationToken: Das Abbrechen eines laufenden ICMP-Requests kann in .NET
                // zu einem Schreibzugriff auf bereits freigegebenen Speicher führen. Der Ping endet spätestens nach dem Timeout.
                var r = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeout), buffer, options);
                if (ct.IsCancellationRequested) break;
                if (r.Status == IPStatus.Success)
                    t.Add(r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds, $"OK (TTL {r.Options?.Ttl})");
                else
                    t.Add(null, StatusText(r.Status));
            }
            catch (Exception ex)
            {
                t.Add(null, ex.InnerException?.Message ?? ex.Message);
            }
            var wait = interval - (int)sw.ElapsedMilliseconds;
            try { await Task.Delay(Math.Max(50, wait), ct); } catch { break; }
        }
    }

    public static string StatusText(IPStatus s) => s switch
    {
        IPStatus.TimedOut => "Zeitüberschreitung",
        IPStatus.DestinationHostUnreachable => "Host nicht erreichbar",
        IPStatus.DestinationNetworkUnreachable => "Netz nicht erreichbar",
        IPStatus.DestinationUnreachable => "Ziel nicht erreichbar",
        IPStatus.PacketTooBig => "Paket zu groß (DF)",
        IPStatus.TtlExpired => "TTL abgelaufen",
        IPStatus.BadDestination => "Ungültiges Ziel",
        _ => s.ToString(),
    };

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
