using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SslPage : ToolPage
{
    private PanelCheckSink _sink;

    public SslPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) HostBox.Text = s;
    }

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var host = NetUtil.CleanHost(HostBox.Text);
        if (host == "") return;
        CheckButton.IsEnabled = false;
        _sink = new PanelCheckSink(ResultsPanel);
        try
        {
            await SslChecker.CheckAsync(host, (int)(PortBox.Value ?? 443), SniBox.Text, _sink);
        }
        catch (Exception ex)
        {
            _sink.Error(L.T("TLS-Verbindung fehlgeschlagen: ") + (ex.InnerException?.Message ?? ex.Message));
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
