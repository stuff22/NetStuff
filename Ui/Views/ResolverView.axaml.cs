using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DnsClient;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class ResolverView : ToolView
{
    private readonly ObservableCollection<ResolverRow> _rows;

    public ResolverView()
    {
        InitializeComponent();
        _rows = new ObservableCollection<ResolverRow>(ResolverBench.DefaultResolvers(L.T("System-Resolver")));
        ResultGrid.ItemsSource = _rows;
    }

    public override void OnNavigatedTo(string parameter) => NameBox.Text = parameter;
    public override void RunNow() => Start_Click(null, null);

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var a = (CustomBox.Text ?? "").Trim();
        if (a == "") return;
        if (!ResolverBench.IsValidAddress(a)) { InfoText.Text = L.T("Bitte IP-Adresse oder https-URL (DoH, JSON-API) angeben."); return; }
        _rows.Insert(0, new ResolverRow { Name = L.T("Eigener"), Address = a });
        CustomBox.Text = "";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (NetUtil.CleanHost(NameBox.Text) == "" || !StartButton.IsEnabled) return;
        var typeName = (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "A";
        StartButton.IsEnabled = false;
        InfoText.Text = L.T("Abfragen laufen …");
        try
        {
            InfoText.Text = await ResolverBench.RunAsync(_rows, NameBox.Text, Enum.Parse<QueryType>(typeName), (int)(RoundsBox.Value ?? 3));
        }
        catch (Exception ex) { InfoText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally { StartButton.IsEnabled = true; }
    }
}
