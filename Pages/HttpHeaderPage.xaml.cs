using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class HttpHeaderPage : ToolPage
{
    private PanelCheckSink _sink;

    public HttpHeaderPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) UrlBox.Text = s;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (UrlBox.Text.Trim() == "") return;
        _sink = new PanelCheckSink(ResultsPanel);
        var uri = HttpHeaderChecker.ParseUrl(UrlBox.Text);
        if (uri == null) { _sink.Error(L.T("Ungültige URL")); return; }

        CheckButton.IsEnabled = false;
        var options = new HttpHeaderOptions
        {
            Head = MethodBox.SelectedIndex == 1,
            FollowRedirects = FollowBox.IsChecked == true,
            UserAgentIndex = UaBox.SelectedIndex,
        };
        try
        {
            await HttpHeaderChecker.CheckAsync(uri, options, _sink);
        }
        catch (Exception ex)
        {
            _sink.Error(L.T("Anfrage fehlgeschlagen: ") + NetUtil.ErrorText(ex));
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
