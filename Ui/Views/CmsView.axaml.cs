using System.Text;
using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Anzeigezeile für eine Erkennung (Kategorie und Sicherheit übersetzt).</summary>
public record CmsRow(string Name, string Category, string Version, string Confidence, string Evidence)
{
    public static CmsRow From(Detection d) =>
        new(d.Name, L.T(d.Category ?? ""), d.Version, L.T(d.ConfidenceText), d.EvidenceText);
}

public partial class CmsView : ToolView
{
    private CmsResult _last;

    public CmsView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(string parameter) => UrlBox.Text = parameter;
    public override void RunNow() => Check_Click(null, null);

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var url = (UrlBox.Text ?? "").Trim();
        if (url == "" || !CheckButton.IsEnabled) return;
        CheckButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        MainCard.IsVisible = true;
        MainText.Text = L.T("Analyse läuft …");
        MainSub.Text = "";
        DetectGrid.IsVisible = false;

        try
        {
            using var cts = new CancellationTokenSource(45000);
            var r = await CmsDetector.AnalyzeAsync(url, cts.Token);
            _last = r;
            var main = r.Detections.FirstOrDefault(d => d.Category is "CMS" or "Shop" or "Baukasten")
                       ?? r.Detections.FirstOrDefault(d => d.Category == "Framework");
            MainText.Text = main != null ? $"{main.Name} {main.Version}".Trim() : L.T("Kein bekanntes CMS erkannt");
            MainSub.Text = (main != null
                               ? L.F("Erkennung: {0} · {1}", L.T(main.ConfidenceText), main.EvidenceText) + "\n"
                               : L.T("Möglicherweise individuell programmiert oder gut verschleiert.\n"))
                           + L.F("Analysiert: {0} (HTTP {1})", r.FinalUrl, r.StatusCode);

            DetectGrid.ItemsSource = r.Detections.Select(CmsRow.From).ToList();
            DetectGrid.IsVisible = r.Detections.Count > 0;

            if (r.WpPlugins.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section(new CheckSection(L.F("WordPress-Plugins ({0})", r.WpPlugins.Count),
                    r.WpPlugins.OrderBy(p => p.Slug).Select(p => new CheckItem(p.Slug, p.Version != "" ? "Version " + p.Version : "Version nicht sichtbar")),
                    "Nur Plugins, die im Seitenquelltext Spuren hinterlassen.")));
            if (r.WpThemes.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section(new CheckSection("WordPress-Themes",
                    r.WpThemes.Select(p => new CheckItem(p.Slug, p.Version != "" ? "Version " + p.Version : "")))));
            if (r.Notes.Count > 0)
                ResultsPanel.Children.Add(UiFactory.Section(new CheckSection("Hinweise", r.Notes.Select(n => new CheckItem("", n)))));
        }
        catch (Exception ex)
        {
            MainText.Text = L.T("Fehler");
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
        var sb = new StringBuilder(L.F("CMS-Analyse {0}", _last.FinalUrl) + "\n");
        foreach (var d in _last.Detections) sb.AppendLine($"  {d.Name} {d.Version} [{L.T(d.Category ?? "")}, {L.T(d.ConfidenceText)}]");
        foreach (var p in _last.WpPlugins) sb.AppendLine($"  Plugin: {p.Slug} {p.Version}");
        foreach (var t in _last.WpThemes) sb.AppendLine($"  Theme: {t.Slug} {t.Version}");
        CopyText(sb.ToString());
    }
}
