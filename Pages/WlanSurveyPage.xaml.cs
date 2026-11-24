using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class WlanSurveyPage : ToolPage
{
    private readonly ObservableCollection<SurveyPoint> _points = new(WlanSurvey.Load());

    public WlanSurveyPage()
    {
        InitializeComponent();
        SurveyGrid.ItemsSource = _points;
    }

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
            var p = await WlanSurvey.MeasureAsync(room, NoteBox.Text.Trim(), SpeedBox.IsChecked == true, s => StatusText.Text = s);
            _points.Add(p);
            WlanSurvey.Save(_points);
            RoomBox.Text = NoteBox.Text = "";
            RoomBox.Focus();
            StatusText.Text = L.F("„{0}“ aufgenommen: {1} ({2})", p.Room, p.RssiText, p.Rating);
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
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

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(L.T("Alle Messpunkte löschen?"), "Net-Stuff", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _points.Clear();
        WlanSurvey.Save(_points);
    }
}
