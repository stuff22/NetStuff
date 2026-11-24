using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Hauptansicht: Navigation links, Werkzeugleiste und Werkzeug rechts (Fenster-Inhalt am Desktop, ganze App auf iPad).</summary>
public partial class MainView : UserControl
{
    private readonly Dictionary<string, ToolView> _views = new();
    private bool _building;
    private string _current;
    private IDisposable _infoTimer;

    public MainView()
    {
        InitializeComponent();
        BuildNav("");
        SearchBox.TextChanged += (_, _) => BuildNav(SearchBox.Text ?? "");
        Nav.SelectionChanged += (_, _) =>
        {
            if (_building || Nav.SelectedItem is not ListBoxItem { Tag: ToolInfo t }) return;
            Open(t.Key);
            if (_compact) Split.IsPaneOpen = false;   // iPhone: nach der Auswahl Navigation schließen
        };
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);

        LoadProjects();
        ProjectStore.ActiveChanged += (_, _) => Dispatcher.UIThread.Post(LoadProjects);
        ProjectStore.Projects.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(LoadProjects);
        ProjectBox.SelectionChanged += (_, _) =>
        {
            if (!_building && ProjectBox.SelectedItem is Project p && p != ProjectStore.Active) ProjectStore.Active = p;
        };

        // Jeder Klick auf einen Start-Button landet im Verlauf (wie in der WPF-App)
        AddHandler(Button.ClickEvent, OnAnyButtonClick, RoutingStrategies.Bubble, handledEventsToo: true);

        Open(Tools.Find(UiOptions.Page ?? "")?.Key ?? "dashboard", UiOptions.Param, UiOptions.Run);
    }

    private ToolView CurrentView => Host.Content as ToolView;
    private ToolInfo CurrentInfo => Tools.Find(_current ?? "");

    private void BuildNav(string filter)
    {
        _building = true;
        Nav.Items.Clear();
        filter = filter.Trim();
        foreach (var g in Tools.Visible.GroupBy(t => t.Group))
        {
            var items = g.Where(t => filter == "" || L.T(t.Title).Contains(filter, StringComparison.OrdinalIgnoreCase) || t.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0) continue;
            Nav.Items.Add(new ListBoxItem { Content = new TextBlock { Text = L.T(g.Key).ToUpperInvariant(), Classes = { "NavGroup" } }, IsEnabled = false, Padding = new(0), MinHeight = 0 });
            foreach (var t in items)
            {
                var text = new TextBlock { Text = L.T(t.Title), VerticalAlignment = VerticalAlignment.Center };
                if (t.Create == null) text.Opacity = 0.5;
                Nav.Items.Add(new ListBoxItem { Content = text, Tag = t, Padding = new(14, 7), MinHeight = 0 });
            }
        }
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ToolInfo t && t.Key == _current);
        _building = false;
    }

    public void Open(string key, string parameter = null, bool run = false)
    {
        var info = Tools.Find(key);
        if (info == null) return;
        _current = key;
        if (!_views.TryGetValue(key, out var view))
        {
            view = info.Create?.Invoke() ?? new PlaceholderView(info);
            _views[key] = view;
        }
        Host.Content = view;
        Responsive.Apply(view, _compact);
        if (parameter != null) view.OnNavigatedTo(parameter);
        if (run)
        {
            view.RunNow();
            if (view.IsTool && info.Create != null) HistoryStore.Add(info.Key, info.Title, view.PrimaryTarget);
        }
        else view.OnShown();
        ReportButton.IsEnabled = FavButton.IsEnabled = view.IsTool && info.Create != null;
        _building = true;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ToolInfo t && t.Key == key);
        _building = false;
    }

    /* ------------------------------------------------------------------ Layout (breit / kompakt) */

    private bool _compact;
    private bool? _layoutApplied;

    /// <summary>Unter 760 px (iPhone, iPad im geteilten Bildschirm): Navigation einklappbar, kürzere Werkzeugleiste.</summary>
    private void ApplyLayout(double width)
    {
        bool compact = width > 0 && width < 760;
        if (_layoutApplied == compact) return;
        _layoutApplied = compact;
        _compact = compact;

        Split.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        Split.IsPaneOpen = !compact;
        Split.OpenPaneLength = compact ? Math.Min(280, width * 0.8) : 230;
        MenuButton.IsVisible = compact;
        ProjectLabel.IsVisible = !compact;
        ProjectBox.Width = compact ? Math.Max(100, Math.Min(200, width - 290)) : 230;
        ReportButton.Content = compact ? L.T("+ Bericht") : L.T("+ Zum Bericht");
        FavButton.Content = compact ? "★" : L.T("★ Favorit");
        ReportButton.MinWidth = FavButton.MinWidth = compact ? 0 : 90;
        Toolbar.Padding = compact ? new Thickness(10, 6, 10, 6) : new Thickness(28, 8, 20, 8);
        Host.Margin = compact ? new Thickness(12, 12, 12, 12) : new Thickness(28, 18, 28, 18);
        if (compact) Classes.Add("compact"); else Classes.Remove("compact");
        Responsive.Apply(CurrentView, compact);
    }

    private void Menu_Click(object sender, RoutedEventArgs e) => Split.IsPaneOpen = !Split.IsPaneOpen;

    /* ------------------------------------------------------------------ Werkzeugleiste */

    /// <summary>Projektauswahl neu laden (z. B. nach dem Umbenennen eines Kunden).</summary>
    public void RefreshProjects() => LoadProjects();

    private void LoadProjects()
    {
        _building = true;
        ProjectBox.ItemsSource = ProjectStore.Projects.ToList();
        ProjectBox.SelectedItem = ProjectStore.Active;
        _building = false;
    }

    private void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.Source is not Button b || !ToolView.RunButtonNames.Contains(b.Name)) return;
        var view = b.FindAncestorOfType<ToolView>();
        var info = CurrentInfo;
        if (view == null || view != CurrentView || info == null || !view.IsTool) return;
        HistoryStore.Add(info.Key, info.Title, view.PrimaryTarget);
    }

    public void ShowInfo(string text)
    {
        ToolbarInfo.Text = L.T(text);
        _infoTimer?.Dispose();
        _infoTimer = DispatcherTimer.RunOnce(() => ToolbarInfo.Text = "", TimeSpan.FromSeconds(5));
    }

    private void AddToReport_Click(object sender, RoutedEventArgs e) => AddToReport();

    /// <summary>Ergebnis des aktuellen Werkzeugs ins aktive Projekt übernehmen (legt bei Bedarf ein Projekt an).</summary>
    public void AddToReport()
    {
        var view = CurrentView;
        var info = CurrentInfo;
        if (view == null || info == null || !view.IsTool) { ShowInfo("Diese Seite kann nicht in einen Bericht übernommen werden."); return; }

        var entry = ReportCapture.Capture(view, info.Key, L.T(info.Title));
        if (entry.Blocks.Count == 0) { ShowInfo("Keine Ergebnisse zum Übernehmen – bitte zuerst das Werkzeug ausführen."); return; }

        var project = ProjectStore.Active;
        if (project == null)
        {
            project = ProjectStore.Create(L.T("Neues Projekt") + " " + DateTime.Now.ToString("dd.MM.yyyy"));
            ProjectStore.Active = project;
        }
        project.Entries.Add(entry);
        ProjectStore.Save(project);
        ShowInfo(L.F("✓ In „{0}“ übernommen ({1} Einträge)", project.Display, project.Entries.Count));
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var view = CurrentView;
        var info = CurrentInfo;
        if (view == null || info == null || !view.IsTool) return;
        var target = view.PrimaryTarget;
        HistoryStore.AddFavorite(new Favorite
        {
            Tool = info.Key,
            ToolTitle = info.Title,
            Target = target,
            Name = string.IsNullOrEmpty(target) ? L.T(info.Title) : $"{L.T(info.Title)}: {target}",
        });
        ShowInfo("★ Als Favorit gespeichert");
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var app = Application.Current;
        if (app == null) return;
        bool dark = app.ActualThemeVariant == ThemeVariant.Dark;
        app.RequestedThemeVariant = dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}

/// <summary>Werkzeug, das in der Avalonia-Version noch nicht umgesetzt ist.</summary>
public class PlaceholderView : ToolView
{
    public PlaceholderView(ToolInfo info)
    {
        Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = L.T(info.Title), Classes = { "PageTitle" } },
                new TextBlock { Text = L.T("Dieses Werkzeug folgt in einer der nächsten Ausbaustufen der Mac-Version."), Classes = { "PageSubtitle" } },
            },
        };
    }

    public override bool IsTool => false;
}
