using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetStuff.Infrastructure;

namespace NetStuff.Pages;

public partial class HistoryPage : ToolPage
{
    private readonly ICollectionView _histView;

    public HistoryPage()
    {
        InitializeComponent();
        FavGrid.ItemsSource = HistoryStore.Favorites;
        _histView = new CollectionViewSource { Source = HistoryStore.History }.View;
        HistGrid.ItemsSource = _histView;
    }

    public override string PrimaryTarget => "";

    private static bool NewTab => Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = FilterBox.Text.Trim().ToLowerInvariant();
        _histView.Filter = q == "" ? null : o => o is HistoryEntry h &&
            ((h.Target ?? "").ToLowerInvariant().Contains(q) || L.T(h.ToolTitle ?? "").ToLowerInvariant().Contains(q));
    }

    private void RunFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavGrid.SelectedItem is Favorite f) Main.RunTool(f.Tool, f.Target, true, NewTab);
    }

    private void FavGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunFav_Click(sender, e);

    private void RenameFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavGrid.SelectedItem is not Favorite f) return;
        var name = Prompt(L.T("Neuer Name für den Favoriten:"), f.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        f.Name = name.Trim();
        HistoryStore.SaveFavorites();
        FavGrid.Items.Refresh();
    }

    private void RemoveFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavGrid.SelectedItem is not Favorite f) return;
        HistoryStore.Favorites.Remove(f);
        HistoryStore.SaveFavorites();
    }

    private void RunHist_Click(object sender, RoutedEventArgs e)
    {
        if (HistGrid.SelectedItem is HistoryEntry h) Main.RunTool(h.Tool, h.Target, true, NewTab);
    }

    private void HistGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => RunHist_Click(sender, e);

    private void FavFromHist_Click(object sender, RoutedEventArgs e)
    {
        if (HistGrid.SelectedItem is not HistoryEntry h) return;
        HistoryStore.AddFavorite(new Favorite
        {
            Tool = h.Tool,
            ToolTitle = h.ToolTitle,
            Target = h.Target,
            Name = string.IsNullOrEmpty(h.Target) ? L.T(h.ToolTitle) : $"{L.T(h.ToolTitle)}: {h.Target}",
        });
    }

    private void ClearHist_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(L.T("Gesamten Verlauf löschen?"), "Net-Stuff", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            HistoryStore.ClearHistory();
    }

    /// <summary>Einfacher Eingabedialog.</summary>
    public static string Prompt(string text, string value)
    {
        var box = new TextBox { Text = value, Margin = new Thickness(0, 8, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("Abbrechen"), IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = text });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = "Net-Stuff",
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextFillColorPrimaryBrush"),
        };
        ok.Click += (_, _) => win.DialogResult = true;
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
