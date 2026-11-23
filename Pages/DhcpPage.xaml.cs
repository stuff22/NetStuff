using System.Windows;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DhcpPage : ToolPage
{
    public DhcpPage()
    {
        InitializeComponent();
        var list = DhcpCheck.Adapters();
        AdapterBox.ItemsSource = list;
        if (list.Count > 0) AdapterBox.SelectedIndex = 0;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not DhcpAdapterChoice a) return;
        StartButton.IsEnabled = false;
        ResultsPanel.Children.Clear();
        int wait = (int)(WaitBox.Value ?? 6);
        StatusText.Text = L.F("DHCPDISCOVER gesendet – warte {0} s auf Angebote …", wait);
        try
        {
            var sections = await DhcpCheck.RunAsync(a, RandomMacBox.IsChecked == true, wait);
            foreach (var s in sections) ResultsPanel.Children.Add(UiFactory.Section(s));
            StatusText.Text = L.F("{0} Angebot(e) erhalten", Math.Max(0, sections.Count - 1));
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Bad));
            StatusText.Text = "";
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
