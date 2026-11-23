using System.Windows;
using System.Windows.Media;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DashboardPage : ToolPage
{
    /// <summary>Adapter aus dem Kern + Statusfarbe für die Anzeige.</summary>
    public class AdapterView
    {
        public AdapterView(AdapterInfo a)
        {
            Name = a.Name;
            Description = a.Description;
            Status = a.Status;
            StatusBrush = new SolidColorBrush(a.Up ? Palette.Ok : Color.FromRgb(0x6E, 0x76, 0x81));
            Items = a.Items;
        }

        public string Name { get; }
        public string Description { get; }
        public string Status { get; }
        public Brush StatusBrush { get; }
        public List<CheckItem> Items { get; }
    }

    private bool _loaded;

    public DashboardPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (!_loaded) { _loaded = true; _ = LoadAsync(); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private async Task LoadAsync()
    {
        AdapterList.ItemsSource = NetworkOverview.Adapters().Select(a => new AdapterView(a)).ToList();
        PublicList.ItemsSource = new[] { new CheckItem("Öffentliche IP", "wird ermittelt …") };
        try
        {
            PublicList.ItemsSource = await NetworkOverview.PublicInfoAsync();
        }
        catch (Exception ex)
        {
            PublicList.ItemsSource = new[] { new CheckItem("Fehler", ex.Message, Level.Bad) };
        }
    }
}
