using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NetStuff.Services;
using NetStuff.Ui.Controls;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Dauerüberwachung. Läuft global weiter, auch wenn ein anderes Werkzeug angezeigt wird.</summary>
public partial class MonitorView : ToolView
{
    public MonitorView()
    {
        InitializeComponent();
        TargetGrid.ItemsSource = MonitorService.Targets;
        EventGrid.ItemsSource = MonitorService.Events;
        MonitorService.Updated += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public override void OnNavigatedTo(string parameter) => TargetBox.Text = parameter;
    public override void RunNow() => Start_Click(null, null);
    public override void OnShown() => Refresh();

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
        PortPanel.IsVisible = SelectedType is "TCP" or "TLS";
        if (SelectedType == "TLS") PortBox.Value = 443;
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var target = (TargetBox.Text ?? "").Trim();
        if (target == "") return;
        var type = SelectedType;
        var name = (NameInput.Text ?? "").Trim();
        MonitorService.Targets.Add(new MonitorTarget
        {
            Name = name == "" ? target : name,
            Type = type,
            Target = type is "HTTP" ? target : NetUtil.CleanHost(target),
            Port = (int)(PortBox.Value ?? 0),
            IntervalSec = (int)(IntervalBox.Value ?? 30),
            Threshold = (int)(ThresholdBox.Value ?? 3),
        });
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
