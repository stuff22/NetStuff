using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class WhoisView : ToolView
{
    public WhoisView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(string parameter) => QueryBox.Text = parameter;
    public override void RunNow() => Whois_Click(null, null);

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Whois_Click(sender, e);
    }

    private bool Busy(bool b)
    {
        if (b && !WhoisButton.IsEnabled) return false;
        WhoisButton.IsEnabled = RdapButton.IsEnabled = !b;
        if (b) { RawBox.Text = L.T("Abfrage läuft …"); SummaryPanel.Children.Clear(); }
        return true;
    }

    private void Show(WhoisResult r)
    {
        RawBox.Text = r.Raw;
        SummaryPanel.Children.Clear();
        if (r.Summary.Count > 0) SummaryPanel.Children.Add(UiFactory.Section(new CheckSection("Zusammenfassung", r.Summary)));
    }

    private async void Whois_Click(object sender, RoutedEventArgs e)
    {
        if ((QueryBox.Text ?? "").Trim() == "" || !Busy(true)) return;
        try { Show(await WhoisLookup.WhoisAsync(QueryBox.Text, ServerBox.Text ?? "", FollowBox.IsChecked == true)); }
        catch (Exception ex) { RawBox.Text = L.T("Fehler: ") + ex.Message; }
        finally { Busy(false); }
    }

    private async void Rdap_Click(object sender, RoutedEventArgs e)
    {
        if ((QueryBox.Text ?? "").Trim() == "" || !Busy(true)) return;
        try { Show(await WhoisLookup.RdapAsync(QueryBox.Text)); }
        catch (Exception ex) { RawBox.Text = L.T("Fehler: ") + ex.Message; }
        finally { Busy(false); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(RawBox.Text);
}
