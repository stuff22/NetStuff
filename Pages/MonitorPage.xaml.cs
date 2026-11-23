using System.Windows;
using System.Windows.Controls;
using NetStuff.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class MonitorPage : ToolPage
{
    public MonitorPage()
    {
        InitializeComponent();
        TargetGrid.ItemsSource = MonitorService.Targets;
        EventGrid.ItemsSource = MonitorService.Events;
        MonitorService.Updated += (_, _) => Dispatcher.Invoke(Refresh);
        Refresh();
    }

    /// <summary>Überwachung läuft global weiter – beim Schließen des Tabs nicht stoppen.</summary>
    public override void OnClosing() { }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string host) TargetBox.Text = host;
    }

    private void Refresh()
    {
        StartButton.IsEnabled = !MonitorService.IsRunning;
        StopButton.IsEnabled = MonitorService.IsRunning;
        int down = MonitorService.Targets.Count(t => t.Level == Level.Bad);
        StatusText.Text = MonitorService.IsRunning
            ? L.F("Läuft · {0} Ziele · {1} gestört", MonitorService.Targets.Count, down)
            : L.F("Gestoppt · {0} Ziele", MonitorService.Targets.Count);
        UpdateChart();
    }

    private void UpdateChart()
    {
        Chart.SeriesList.Clear();
        var sel = TargetGrid.SelectedItems.Cast<MonitorTarget>().ToList();
        var show = sel.Count > 0 ? sel : MonitorService.Targets.Take(6).ToList();
        int i = 0;
        foreach (var t in show)
        {
            var s = new LineChart.Series { Name = t.Name, Color = Palette.Series[i++ % Palette.Series.Length] };
            s.Values.AddRange(t.History);
            Chart.SeriesList.Add(s);
        }
        Chart.Refresh();
    }

    private string SelectedType => (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ping";

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PortPanel == null) return;
        PortPanel.Visibility = SelectedType is "TCP" or "TLS" ? Visibility.Visible : Visibility.Collapsed;
        PortBox.Value = SelectedType == "TLS" ? 443 : PortBox.Value;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim();
        if (target == "") return;
        var type = SelectedType;
        var t = new MonitorTarget
        {
            Name = NameInput.Text.Trim() == "" ? target : NameInput.Text.Trim(),
            Type = type,
            Target = type is "HTTP" ? target : NetUtil.CleanHost(target),
            Port = (int)(PortBox.Value ?? 0),
            IntervalSec = (int)(IntervalBox.Value ?? 30),
            Threshold = (int)(ThresholdBox.Value ?? 3),
        };
        MonitorService.Targets.Add(t);
        MonitorService.Save();
        NameInput.Text = TargetBox.Text = "";
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in TargetGrid.SelectedItems.Cast<MonitorTarget>().ToList()) MonitorService.Targets.Remove(t);
        MonitorService.Save();
        Refresh();
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in MonitorService.Targets) t.NextRun = DateTime.Now;
        if (!MonitorService.IsRunning) Start_Click(sender, e);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorService.Targets.Count == 0) { StatusText.Text = L.T("Bitte zuerst Ziele hinzufügen."); return; }
        MonitorService.Start();
        Refresh();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        MonitorService.Stop();
        Refresh();
    }

    private void TargetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateChart();
}
