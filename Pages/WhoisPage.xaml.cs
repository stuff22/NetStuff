using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class WhoisPage : ToolPage
{
    public WhoisPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) { QueryBox.Text = s; Whois_Click(null, null); }
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Whois_Click(sender, e);
    }

    private void Busy(bool b)
    {
        WhoisButton.IsEnabled = !b;
        RdapButton.IsEnabled = !b;
        if (b) { RawBox.Text = "Abfrage läuft …"; SummaryList.ItemsSource = null; }
    }

    private void Show(WhoisResult r)
    {
        RawBox.Text = r.Raw;
        SummaryList.ItemsSource = r.Summary.Count > 0 ? r.Summary : null;
    }

    private async void Whois_Click(object sender, RoutedEventArgs e)
    {
        if (QueryBox.Text.Trim() == "") return;
        Busy(true);
        try { Show(await WhoisLookup.WhoisAsync(QueryBox.Text, ServerBox.Text, FollowBox.IsChecked == true)); }
        catch (Exception ex) { RawBox.Text = "Fehler: " + NetUtil.ErrorText(ex); }
        finally { Busy(false); }
    }

    private async void Rdap_Click(object sender, RoutedEventArgs e)
    {
        if (QueryBox.Text.Trim() == "") return;
        Busy(true);
        try { Show(await WhoisLookup.RdapAsync(QueryBox.Text)); }
        catch (Exception ex) { RawBox.Text = "Fehler: " + NetUtil.ErrorText(ex); }
        finally { Busy(false); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(RawBox.Text);
}
