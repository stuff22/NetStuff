using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SslView : ToolView
{
    private PanelCheckSink _sink;

    public SslView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(string parameter) => HostBox.Text = parameter;
    public override void RunNow() => Check_Click(null, null);

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var host = NetUtil.CleanHost(HostBox.Text ?? "");
        if (host == "" || !CheckButton.IsEnabled) return;
        CheckButton.IsEnabled = false;
        _sink = new PanelCheckSink(ResultsPanel);
        try
        {
            await SslChecker.CheckAsync(host, (int)(PortBox.Value ?? 443), SniBox.Text ?? "", _sink);
        }
        catch (Exception ex)
        {
            _sink.Error(L.T("TLS-Verbindung fehlgeschlagen: ") + NetUtil.ErrorText(ex));
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
