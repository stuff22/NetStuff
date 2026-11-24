using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class DashboardView : ToolView
{
    private bool _loaded;

    public DashboardView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => AdapterGrid.Columns = e.NewSize.Width < 900 ? 1 : 2;
    }

    public override string PrimaryTarget => "";
    public override void OnShown() { if (!_loaded) { _loaded = true; _ = LoadAsync(); } }
    public override void RunNow() => _ = LoadAsync();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        RefreshButton.IsEnabled = false;
        AdapterGrid.Children.Clear();
        foreach (var a in NetworkOverview.Adapters()) AdapterGrid.Children.Add(AdapterCard(a));

        PublicPanel.Children.Clear();
        PublicPanel.Children.Add(UiFactory.Section(new CheckSection("Internetverbindung", new[] { new CheckItem("Öffentliche IP", "wird ermittelt …") })));
        try
        {
            var items = await NetworkOverview.PublicInfoAsync();
            PublicPanel.Children.Clear();
            PublicPanel.Children.Add(UiFactory.Section(new CheckSection("Internetverbindung", items)));
        }
        catch (Exception ex)
        {
            PublicPanel.Children.Clear();
            PublicPanel.Children.Add(UiFactory.Section(new CheckSection("Internetverbindung", new[] { new CheckItem("Fehler", ex.Message, Level.Bad) })));
        }
        finally { RefreshButton.IsEnabled = true; }
    }

    private static Control AdapterCard(AdapterInfo a)
    {
        var card = (Border)UiFactory.Section(new CheckSection(a.Name, a.Items, a.Description));
        card.Margin = new Thickness(0, 0, 12, 12);
        // Statusabzeichen oben rechts
        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2),
            Background = new SolidColorBrush(a.Up ? Palette.Ok : Color.FromRgb(0x6E, 0x76, 0x81)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = L.T(a.Status ?? ""), FontSize = 11, Foreground = Brushes.White },
        };
        var inner = card.Child;
        card.Child = null;
        var grid = new Grid();
        grid.Children.Add(inner);
        grid.Children.Add(badge);
        card.Child = grid;
        return card;
    }
}
