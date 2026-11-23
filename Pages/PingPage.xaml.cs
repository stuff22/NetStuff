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

/// <summary>Ping-Ziel mit Diagrammreihe (Statistik selbst: PingStats im Kern).</summary>
public class PingTarget : PingStats
{
    public Brush ColorBrush { get; init; }
    public LineChart.Series Series { get; init; }

    protected override void OnSample(double? rtt)
    {
        Series.Values.Add(rtt);
        if (Series.Values.Count > 2000) Series.Values.RemoveRange(0, 1000);
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
        var hosts = PingRunner.ParseTargets(TargetsBox.Text);
        if (hosts.Count == 0) return;

        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        _targets.Clear();
        Chart.SeriesList.Clear();

        var settings = new PingSettings
        {
            IntervalMs = (int)(IntervalBox.Value ?? 1000),
            TimeoutMs = (int)(TimeoutBox.Value ?? 2000),
            Size = (int)(SizeBox.Value ?? 32),
            DontFragment = DfBox.IsChecked == true,
        };

        for (int i = 0; i < hosts.Count; i++)
        {
            var color = Palette.Series[i % Palette.Series.Length];
            var series = new LineChart.Series { Name = hosts[i], Color = color };
            Chart.SeriesList.Add(series);
            _targets.Add(new PingTarget { Host = hosts[i], ColorBrush = new SolidColorBrush(color), Series = series });
        }

        var ct = _cts.Token;
        var tasks = _targets.Select(t => PingRunner.RunAsync(t, settings, ct)).ToList();
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

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();
}
