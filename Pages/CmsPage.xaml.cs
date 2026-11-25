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
        _sitemap = null;
        SitemapCard.Visibility = Visibility.Collapsed;
        SitemapList.ItemsSource = null;
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
            WpOffer.Visibility = Visibility.Visible;

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
            WpOffer.Visibility = Visibility.Collapsed;
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
        var sb = new StringBuilder($"CMS-Analyse {_last.FinalUrl}\n");
        foreach (var d in _last.Detections) sb.AppendLine($"  {d.Name} {d.Version} [{d.Category}, {d.ConfidenceText}]");
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
        var any = _sitemap?.Links.Count > 0;
        SitemapCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;
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

    private void SitemapFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplySitemapFilter();

    private void SitemapList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement ?? (e.OriginalSource as FrameworkContentElement)?.Parent as FrameworkElement)?.DataContext is SitemapLink l)
            NetUtil.OpenUrl(l.Url);
    }

    private void SitemapCopy_Click(object sender, RoutedEventArgs e)
    {
        if (SitemapList.ItemsSource is IEnumerable<SitemapLink> list)
            CopyText(string.Join("\n", list.Select(l => l.Url)));
    }
}
