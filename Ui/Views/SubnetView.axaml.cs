using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SubnetView : ToolView
{
    private SubnetInfo _current;

    public SubnetView()
    {
        InitializeComponent();
        InputBox.TextChanged += (_, _) => Calculate();
        SplitBox.ValueChanged += (_, _) => UpdateSplit();
        Calculate();
    }

    public override void OnNavigatedTo(string parameter) => InputBox.Text = parameter;

    private void Calculate()
    {
        ResultsPanel.Children.Clear();
        _current = null;
        if ((InputBox.Text ?? "").Trim() != "")
        {
            try
            {
                _current = SubnetCalculator.Calculate(InputBox.Text);
                foreach (var s in _current.Sections) ResultsPanel.Children.Add(UiFactory.Section(s));
            }
            catch (Exception ex)
            {
                ResultsPanel.Children.Add(UiFactory.Message(NetUtil.ErrorText(ex), Level.Warn));
            }
        }
        UpdateSplit();
    }

    private void UpdateSplit()
    {
        SplitGrid.ItemsSource = null;
        SplitTitle.Text = "";
        if (_current == null) return;
        var (rows, title) = SubnetCalculator.Split(_current, (int)(SplitBox.Value ?? 0));
        SplitGrid.ItemsSource = rows.Count > 0 ? rows : null;
        SplitTitle.Text = title;
    }
}
