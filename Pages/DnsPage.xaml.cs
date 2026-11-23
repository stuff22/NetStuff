using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DnsClient;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DnsPage : ToolPage
{
    private readonly Dictionary<QueryType, CheckBox> _types = new();
    private DnsLookupResult _last;

    public DnsPage()
    {
        InitializeComponent();
        foreach (var t in DnsHelper.CommonTypes)
        {
            var cb = new CheckBox { Content = t.ToString(), Margin = new Thickness(0, 0, 14, 4), MinWidth = 0, IsChecked = DnsLookup.DefaultSelected(t) };
            _types[t] = cb;
            TypePanel.Children.Add(cb);
        }
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) { NameBox.Text = s; Query_Click(null, null); }
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Query_Click(sender, e);
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (NetUtil.CleanHost(NameBox.Text) == "") return;
        QueryButton.IsEnabled = false;
        InfoText.Text = "Abfrage läuft …";
        ResultGrid.ItemsSource = null;
        try
        {
            var types = _types.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key);
            _last = await DnsLookup.QueryAsync(NameBox.Text, ServerBox.Text, TcpBox.IsChecked == true, types);
            ResultGrid.ItemsSource = _last.Rows;
            InfoText.Text = _last.Summary;
        }
        catch (Exception ex)
        {
            InfoText.Text = "Fehler: " + ex.Message;
        }
        finally
        {
            QueryButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_last?.ToText());
}
