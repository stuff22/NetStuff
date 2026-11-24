using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SurveyView : ToolView
{
    private readonly ObservableCollection<SurveyPoint> _points = new(WlanSurvey.Load());
    private DateTime _clearArmed = DateTime.MinValue;

    public SurveyView()
    {
        InitializeComponent();
        SurveyGrid.ItemsSource = _points;
    }

    public override void OnNavigatedTo(string parameter) => RoomBox.Text = parameter;
    public override void RunNow() => Measure_Click(null, null);

    private void RoomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Measure_Click(sender, e);
    }

    private async void Measure_Click(object sender, RoutedEventArgs e)
    {
        if (!StartButton.IsEnabled) return;
        var room = (RoomBox.Text ?? "").Trim();
        if (room == "") room = L.F("Messpunkt {0}", _points.Count + 1);
        StartButton.IsEnabled = false;
        try
        {
            var p = await WlanSurvey.MeasureAsync(room, (NoteBox.Text ?? "").Trim(), SpeedBox.IsChecked == true, s => StatusText.Text = s);
            _points.Add(p);
            WlanSurvey.Save(_points);
            RoomBox.Text = NoteBox.Text = "";
            RoomBox.Focus();
            StatusText.Text = L.F("„{0}“ aufgenommen: {1} ({2})", p.Room, p.RssiText, p.Rating)
                              + (Platform.Wlan?.NamesHidden == true ? "  ·  " + L.T("SSID/BSSID verborgen (Standortfreigabe fehlt – siehe WLAN-Scanner).") : "");
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
        WlanSurvey.Save(_points);
    }

    /// <summary>Zweimal klicken (innerhalb von 4 s) zum Leeren – ohne modalen Dialog.</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_points.Count == 0) return;
        if (DateTime.Now - _clearArmed > TimeSpan.FromSeconds(4))
        {
            _clearArmed = DateTime.Now;
            StatusText.Text = L.T("Zum Löschen aller Messpunkte nochmals „Liste leeren“ klicken.");
            return;
        }
        _clearArmed = DateTime.MinValue;
        _points.Clear();
        WlanSurvey.Save(_points);
        StatusText.Text = "";
    }
}
