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
            if (!_building && Nav.SelectedItem is ListBoxItem { Tag: ToolInfo t }) Open(t.Key);
        };

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
        foreach (var g in Tools.All.GroupBy(t => t.Group))
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

    /* ------------------------------------------------------------------ Werkzeugleiste */

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
