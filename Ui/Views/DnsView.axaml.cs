using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DnsClient;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class DnsView : ToolView
{
    private readonly Dictionary<QueryType, CheckBox> _types = new();
    private DnsLookupResult _last;

    public DnsView()
    {
        InitializeComponent();
        var servers = new[] { L.T("System-Resolver"), "1.1.1.1 (Cloudflare)", "8.8.8.8 (Google)", "9.9.9.9 (Quad9)", L.T("Autoritativ (NS der Domain)") };
        ServerBox.ItemsSource = servers;
        ServerBox.Text = servers[0];
        foreach (var t in DnsHelper.CommonTypes)
        {
            var cb = new CheckBox { Content = t.ToString(), Margin = new Thickness(0, 0, 14, 0), MinWidth = 0, IsChecked = DnsLookup.DefaultSelected(t) };
            _types[t] = cb;
            TypePanel.Children.Add(cb);
        }
    }

    public override void OnNavigatedTo(string parameter) => NameBox.Text = parameter;
    public override void RunNow() => Query_Click(null, null);

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Query_Click(sender, e);
    }

    private async void Query_Click(object sender, RoutedEventArgs e)
    {
        if (NetUtil.CleanHost(NameBox.Text) == "" || !QueryButton.IsEnabled) return;
        QueryButton.IsEnabled = false;
        InfoText.Text = L.T("Abfrage läuft …");
        ResultGrid.ItemsSource = null;
        try
        {
            var types = _types.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key);
            var server = ServerBox.Text ?? "";
            if (server == L.T("System-Resolver")) server = "System";
            else if (server == L.T("Autoritativ (NS der Domain)")) server = "Autoritativ";
            _last = await DnsLookup.QueryAsync(NameBox.Text, server, TcpBox.IsChecked == true, types);
            ResultGrid.ItemsSource = _last.Rows;
            InfoText.Text = _last.Summary;
        }
        catch (Exception ex) { InfoText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally { QueryButton.IsEnabled = true; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_last?.ToText());
}
