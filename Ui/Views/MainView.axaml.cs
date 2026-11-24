using Avalonia.Controls;
using Avalonia.Layout;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Hauptansicht: Navigation links, Werkzeug rechts (Fenster-Inhalt am Desktop, ganze App auf iPad).</summary>
public partial class MainView : UserControl
{
    private readonly Dictionary<string, ToolView> _views = new();
    private bool _building;

    public MainView()
    {
        InitializeComponent();
        BuildNav("");
        SearchBox.TextChanged += (_, _) => BuildNav(SearchBox.Text ?? "");
        Nav.SelectionChanged += (_, _) =>
        {
            if (!_building && Nav.SelectedItem is ListBoxItem { Tag: ToolInfo t }) Open(t.Key);
        };
        Open(Tools.Find(UiOptions.Page ?? "")?.Key ?? "ping", UiOptions.Param, UiOptions.Run);
    }

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

    private string _current;

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
        if (run) view.RunNow();
        _building = true;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is ToolInfo t && t.Key == key);
        _building = false;
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
}
