using System.Collections.ObjectModel;
using System.Windows;
using DnsClient;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class ResolverPage : ToolPage
{
    private readonly ObservableCollection<ResolverRow> _rows;

    public ResolverPage()
    {
        InitializeComponent();
        _rows = new ObservableCollection<ResolverRow>(ResolverBench.DefaultResolvers("Windows (System)"));
        ResultGrid.ItemsSource = _rows;
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) NameBox.Text = s;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var a = CustomBox.Text.Trim();
        if (a == "") return;
        if (!ResolverBench.IsValidAddress(a)) { InfoText.Text = "Bitte IP-Adresse oder https-URL (DoH, JSON-API) angeben."; return; }
        _rows.Insert(0, new ResolverRow { Name = "Eigener", Address = a });
        CustomBox.Text = "";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (NetUtil.CleanHost(NameBox.Text) == "") return;
        var typeName = (TypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "A";
        StartButton.IsEnabled = false;
        InfoText.Text = "Abfragen laufen …";
        try
        {
            InfoText.Text = await ResolverBench.RunAsync(_rows, NameBox.Text, Enum.Parse<QueryType>(typeName), (int)(RoundsBox.Value ?? 3));
        }
        catch (Exception ex)
        {
            InfoText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }
}
