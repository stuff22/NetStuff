using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class DomainCheckPage : ToolPage
{
    private PanelCheckSink _sink;

    public DomainCheckPage()
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
        var domain = DomainChecker.ParseDomain(DomainBox.Text);
        if (domain == null) return;
        CheckButton.IsEnabled = false;
        _sink = new PanelCheckSink(ResultsPanel);
        try
        {
            var summary = await DomainChecker.CheckAsync(domain, BlacklistBox.IsChecked == true, _sink);
            _sink.Finish();
            _sink.Insert(summary);
        }
        catch (Exception ex)
        {
            _sink.Error(ex.Message);
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
