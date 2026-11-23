using System.Text;
using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class CmsPage : ToolPage
{
    private CmsResult _last;

    public CmsPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) UrlBox.Text = s;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (url == "") return;
        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        MainCard.Visibility = Visibility.Visible;
        MainText.Text = "Analyse läuft …";
        MainSub.Text = "";
        DetectGrid.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(45000);
            var r = await CmsDetector.AnalyzeAsync(url, cts.Token);
            _last = r;
            var main = r.Detections.FirstOrDefault(d => d.Category is "CMS" or "Shop" or "Baukasten")
                       ?? r.Detections.FirstOrDefault(d => d.Category == "Framework");
            MainText.Text = main != null ? $"{main.Name} {main.Version}".Trim() : "Kein bekanntes CMS erkannt";
            MainSub.Text = (main != null ? $"Erkennung: {main.ConfidenceText} · {main.EvidenceText}\n" : "Möglicherweise individuell programmiert oder gut verschleiert.\n")
                           + $"Analysiert: {r.FinalUrl} (HTTP {r.StatusCode})";

            DetectGrid.ItemsSource = r.Detections;
            DetectGrid.Visibility = r.Detections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (r.WpPlugins.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section($"WordPress-Plugins ({r.WpPlugins.Count})",
                    r.WpPlugins.OrderBy(p => p.Slug).Select(p => new CheckItem(p.Slug, p.Version != "" ? "Version " + p.Version : "Version nicht sichtbar")),
                    "Nur Plugins, die im Seitenquelltext Spuren hinterlassen."));
            if (r.WpThemes.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section("WordPress-Themes",
                    r.WpThemes.Select(p => new CheckItem(p.Slug, p.Version != "" ? "Version " + p.Version : ""))));
            if (r.Notes.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section("Hinweise", r.Notes.Select(n => new CheckItem("", n))));
        }
        catch (Exception ex)
        {
            MainText.Text = "Fehler";
            MainSub.Text = ex.InnerException?.Message ?? ex.Message;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var sb = new StringBuilder($"CMS-Analyse {_last.FinalUrl}\n");
        foreach (var d in _last.Detections) sb.AppendLine($"  {d.Name} {d.Version} [{d.Category}, {d.ConfidenceText}]");
        foreach (var p in _last.WpPlugins) sb.AppendLine($"  Plugin: {p.Slug} {p.Version}");
        foreach (var t in _last.WpThemes) sb.AppendLine($"  Theme: {t.Slug} {t.Version}");
        CopyText(sb.ToString());
    }
}
