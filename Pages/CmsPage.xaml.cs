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
        WpPanel.Children.Clear();
        WpOffer.Visibility = Visibility.Collapsed;
        _wpSections = null;
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

            if (r.IsWordPress)
            {
                WpOfferText.Text = L.F("Fragt {0} ab und zeigt Website-Angaben, Plugins laut API-Namespaces, Anzahl der Inhalte und ob Benutzernamen öffentlich sind.",
                    r.WpApiRoot ?? new Uri(r.FinalUrl).GetLeftPart(UriPartial.Authority) + "/wp-json/");
                WpButton.IsEnabled = true;
                WpOffer.Visibility = Visibility.Visible;
            }

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

    private List<CheckSection> _wpSections;

    private async void WpJson_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null || !WpButton.IsEnabled) return;
        WpButton.IsEnabled = false;
        WpPanel.Children.Clear();
        WpPanel.Children.Add(UiFactory.Message(L.T("REST-API wird abgefragt …")));
        try
        {
            using var cts = new CancellationTokenSource(60000);
            _wpSections = await WordPressApi.AnalyzeAsync(_last.FinalUrl, _last.WpApiRoot, cts.Token);
            WpPanel.Children.Clear();
            foreach (var s in _wpSections) WpPanel.Children.Add(UiFactory.Section(s));
            WpOffer.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            WpPanel.Children.Clear();
            WpPanel.Children.Add(UiFactory.Message(L.T("Fehler: ") + (ex.InnerException?.Message ?? ex.Message), Level.Bad));
            WpButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var sb = new StringBuilder($"CMS-Analyse {_last.FinalUrl}\n");
        foreach (var d in _last.Detections) sb.AppendLine($"  {d.Name} {d.Version} [{d.Category}, {d.ConfidenceText}]");
        foreach (var p in _last.WpPlugins) sb.AppendLine($"  Plugin: {p.Slug} {p.Version}");
        foreach (var t in _last.WpThemes) sb.AppendLine($"  Theme: {t.Slug} {t.Version}");
        if (_wpSections != null) foreach (var sec in _wpSections) sb.Append(sec.ToText());
        CopyText(sb.ToString());
    }
}
