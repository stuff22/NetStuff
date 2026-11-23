using System.Diagnostics;
using System.Net;
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
    private List<DnsRow> _rows = new();

    public DnsPage()
    {
        InitializeComponent();
        foreach (var t in DnsHelper.CommonTypes)
        {
            var cb = new CheckBox { Content = t.ToString(), Margin = new Thickness(0, 0, 14, 4), MinWidth = 0, IsChecked = t is QueryType.A or QueryType.AAAA or QueryType.MX or QueryType.NS or QueryType.TXT or QueryType.SOA or QueryType.CNAME or QueryType.CAA };
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
        var name = NetUtil.CleanHost(NameBox.Text);
        if (name == "") return;
        QueryButton.IsEnabled = false;
        InfoText.Text = "Abfrage läuft …";
        ResultGrid.ItemsSource = null;
        var sw = Stopwatch.StartNew();

        try
        {
            IPAddress server = null;
            string serverLabel = "System-Resolver";
            var sel = ServerBox.Text.Trim();
            if (sel.StartsWith("Autoritativ"))
            {
                var (ip, ns) = await DnsHelper.AuthoritativeAsync(name);
                server = ip;
                serverLabel = $"{ns} ({ip}) – autoritativ";
            }
            else if (!sel.StartsWith("System"))
            {
                var first = sel.Split(' ')[0];
                server = IPAddress.TryParse(first, out var ip) ? ip : await NetUtil.ResolveAsync(first);
                serverLabel = server.ToString();
            }

            var client = DnsHelper.Create(server, TcpBox.IsChecked == true);
            var rows = new List<DnsRow>();
            var notes = new List<string>();
            bool anyAd = false;

            if (IPAddress.TryParse(name, out var addr))
            {
                var r = await client.QueryReverseAsync(addr);
                rows.AddRange(DnsHelper.Rows(r.Answers));
                if (r.Answers.Count == 0) notes.Add("Kein PTR-Eintrag");
            }
            else
            {
                var types = _types.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).Where(t => t != QueryType.PTR).ToList();
                var tasks = types.Select(async t => (Type: t, Res: await client.QueryAsync(name, t))).ToList();
                foreach (var (t, r) in await Task.WhenAll(tasks))
                {
                    if (r.HasError && r.Header.ResponseCode != DnsHeaderResponseCode.NoError)
                    {
                        notes.Add($"{t}: {r.ErrorMessage}");
                        continue;
                    }
                    anyAd |= r.Header.IsAuthenticData;
                    rows.AddRange(DnsHelper.Rows(r.Answers)); // CNAME-Ketten werden unten dedupliziert
                }
                rows = rows.GroupBy(x => (x.Type, x.Name, x.Value)).Select(g => g.First())
                    .OrderBy(x => Array.IndexOf(DnsHelper.CommonTypes, Enum.TryParse<QueryType>(x.Type, out var qt) ? qt : QueryType.ANY))
                    .ThenBy(x => x.Value).ToList();
            }

            _rows = rows;
            ResultGrid.ItemsSource = rows;
            var dnssec = anyAd ? "DNSSEC: validiert (AD-Flag) ✓" : "DNSSEC: nicht validiert";
            InfoText.Text = $"{rows.Count} Einträge · Server: {serverLabel} · {sw.ElapsedMilliseconds} ms · {dnssec}"
                            + (notes.Count > 0 ? "\n" + string.Join("  ·  ", notes) : "");
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

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        CopyText(string.Join("\n", _rows.Select(r => $"{r.Name}\t{r.Ttl}\tIN\t{r.Type}\t{r.Value}")));
}
