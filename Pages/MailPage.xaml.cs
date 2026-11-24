using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class MailPage : ToolPage
{
    private PanelCheckSink _sink;

    public MailPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) DomainBox.Text = s;
    }

    private void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var domain = MailChecker.ParseDomain(DomainBox.Text);
        if (domain == "") return;
        CheckButton.IsEnabled = false;
        _sink = new PanelCheckSink(ResultsPanel);
        var options = new MailCheckOptions
        {
            DkimSelectors = SelectorBox.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries),
            SmtpTest = SmtpBox.IsChecked == true,
        };
        try
        {
            await MailChecker.CheckAsync(domain, options, _sink);
        }
        catch (Exception ex)
        {
            _sink.Error(NetUtil.ErrorText(ex));
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
