using System.Windows.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SubnetPage : ToolPage
{
    private SubnetInfo _current;

    public SubnetPage()
    {
        InitializeComponent();
        SplitBox.ValueChanged += (_, _) => UpdateSplit();
        Loaded += (_, _) => Calculate();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) InputBox.Text = s;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => Calculate();

    private void Calculate()
    {
        if (ResultsPanel == null) return;
        ResultsPanel.Children.Clear();
        _current = null;
        if (InputBox.Text.Trim() != "")
        {
            try
            {
                _current = SubnetCalculator.Calculate(InputBox.Text);
                foreach (var s in _current.Sections) ResultsPanel.Children.Add(UiFactory.Section(s));
            }
            catch (Exception ex)
            {
                ResultsPanel.Children.Add(UiFactory.Message(ex.Message, Level.Warn));
            }
        }
        UpdateSplit();
    }

    private void UpdateSplit()
    {
        if (SplitGrid == null) return;
        SplitGrid.ItemsSource = null;
        SplitTitle.Text = "";
        if (_current == null) return;
        var (rows, title) = SubnetCalculator.Split(_current, (int)(SplitBox.Value ?? 0));
        SplitGrid.ItemsSource = rows.Count > 0 ? rows : null;
        SplitTitle.Text = title;
    }
}
