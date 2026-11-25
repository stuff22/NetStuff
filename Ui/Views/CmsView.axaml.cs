using System.Text;
using Avalonia.Controls;
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
        WpPanel.Children.Clear();
        WpOffer.IsVisible = false;
        _wpSections = null;
        _sitemap = null;
        SitemapCard.IsVisible = false;
        SitemapList.ItemsSource = null;
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

            if (CmsApi.SystemFor(r) is { } apiSystem)
            {
                WpOfferTitle.Text = L.F("{0} erkannt – Schnittstellen auswerten?", apiSystem);
                WpOfferText.Text = CmsApi.OfferText(r, apiSystem) + " " + L.T("Dazu alle Links aus der Sitemap.");
                WpButton.Content = apiSystem == "WordPress" ? L.T("wp-json auswerten") : L.T("API auswerten");
            }
            else
            {
                WpOfferTitle.Text = L.T("Sitemap auslesen?");
                WpOfferText.Text = L.T("Liest robots.txt und die Sitemap der Website und listet alle Links alphabetisch auf.");
                WpButton.Content = L.T("Sitemap auslesen");
            }
            WpButton.IsEnabled = true;
            WpOffer.IsVisible = true;

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
            MainSub.Text = NetUtil.ErrorText(ex);
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
        WpPanel.Children.Add(UiFactory.Message(L.T("Schnittstellen werden abgefragt …")));
        try
        {
            using var cts = new CancellationTokenSource(120000);
            var api = CmsApi.SystemFor(_last) != null ? CmsApi.AnalyzeAsync(_last, cts.Token) : Task.FromResult(new List<CheckSection>());
            var map = SitemapReader.ReadAsync(_last.FinalUrl, cts.Token);
            _wpSections = await api;
            _sitemap = await map;
            _wpSections.Add(_sitemap.Summary());
            WpPanel.Children.Clear();
            foreach (var s in _wpSections) WpPanel.Children.Add(UiFactory.Section(s));
            ShowSitemap();
            WpOffer.IsVisible = false;
        }
        catch (Exception ex)
        {
            WpPanel.Children.Clear();
            WpPanel.Children.Add(UiFactory.Message(L.T("Fehler: ") + NetUtil.ErrorText(ex), Level.Bad));
            WpButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_last == null) return;
        var sb = new StringBuilder(L.F("CMS-Analyse {0}", _last.FinalUrl) + "\n");
        foreach (var d in _last.Detections) sb.AppendLine($"  {d.Name} {d.Version} [{L.T(d.Category ?? "")}, {L.T(d.ConfidenceText)}]");
        foreach (var p in _last.WpPlugins) sb.AppendLine($"  Plugin: {p.Slug} {p.Version}");
        foreach (var t in _last.WpThemes) sb.AppendLine($"  Theme: {t.Slug} {t.Version}");
        if (_wpSections != null) foreach (var sec in _wpSections) sb.Append(sec.ToText());
        if (_sitemap?.Links.Count > 0) sb.Append(_sitemap.LinksText());
        CopyText(sb.ToString());
    }

    // ------------------------------------------------------------------ Sitemap-Links

    private SitemapResult _sitemap;

    private void ShowSitemap()
    {
        SitemapCard.IsVisible = _sitemap?.Links.Count > 0;
        if (!SitemapCard.IsVisible) return;
        SitemapTitle.Text = L.F("Links aus der Sitemap ({0})", _sitemap.Links.Count.ToString("N0"));
        SitemapFilter.Text = "";
        ApplySitemapFilter();
    }

    private void ApplySitemapFilter()
    {
        if (_sitemap == null) return;
        var f = (SitemapFilter.Text ?? "").Trim();
        var list = f == "" ? _sitemap.Links
            : _sitemap.Links.Where(l => l.Display.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        SitemapList.ItemsSource = list;
        SitemapCount.Text = f == "" ? "" : L.F("{0} von {1} Links", list.Count.ToString("N0"), _sitemap.Links.Count.ToString("N0"));
    }

    private void SitemapFilter_TextChanged(object sender, TextChangedEventArgs e) => ApplySitemapFilter();

    private void SitemapList_Tapped(object sender, TappedEventArgs e)
    {
        if ((e.Source as Avalonia.StyledElement)?.DataContext is SitemapLink l) NetUtil.OpenUrl(l.Url);
    }

    private void SitemapCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SitemapList.ItemsSource is IEnumerable<SitemapLink> list)
            CopyText(string.Join("\n", list.Select(l => l.Url)));
    }
}
