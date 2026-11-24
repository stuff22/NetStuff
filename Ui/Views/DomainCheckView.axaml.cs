using Avalonia.Input;
using Avalonia.Interactivity;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class DomainCheckView : ToolView
{
    private PanelCheckSink _sink;

    public DomainCheckView()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(string parameter) => DomainBox.Text = parameter;
    public override void RunNow() => Check_Click(null, null);

    private void DomainBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var domain = DomainChecker.ParseDomain(DomainBox.Text ?? "");
        if (string.IsNullOrEmpty(domain) || !CheckButton.IsEnabled) return;
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
