using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class HistoryView : ToolView
{
    private Favorite _renaming;
    private DateTime _clearArmed = DateTime.MinValue;

    public HistoryView()
    {
        InitializeComponent();
        FavGrid.ItemsSource = HistoryStore.Favorites;
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        HistoryStore.History.CollectionChanged += (_, _) => ApplyFilter();
        ApplyFilter();
    }

    public override bool IsTool => false;
    public override string PrimaryTarget => "";

    /// <summary>Werkzeugtitel in der aktuellen Sprache (gespeichert ist der deutsche Titel).</summary>
    private static string Title(string t) => L.T(t ?? "");

    private void ApplyFilter()
    {
        var q = (FilterBox.Text ?? "").Trim();
        HistGrid.ItemsSource = q == ""
            ? HistoryStore.History
            : HistoryStore.History.Where(h => (h.Target ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                              || Title(h.ToolTitle).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Run(string tool, string target) => Navigate(tool, target ?? "", run: true);

    private void RunFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavGrid.SelectedItem is Favorite f) Run(f.Tool, f.Target);
    }

    private void FavGrid_DoubleTapped(object sender, TappedEventArgs e) => RunFav_Click(sender, e);

    private void RenameFav_Click(object sender, RoutedEventArgs e)
    {
        if (RenameBox.IsVisible) { CommitRename(); return; }
        if (FavGrid.SelectedItem is not Favorite f) return;
        _renaming = f;
        RenameBox.Text = f.Name;
        RenameBox.IsVisible = true;
        RenameButton.Content = L.T("Übernehmen");
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitRename();
        else if (e.Key == Key.Escape) EndRename();
    }

    private void CommitRename()
    {
        if (_renaming != null && !string.IsNullOrWhiteSpace(RenameBox.Text))
        {
            _renaming.Name = RenameBox.Text.Trim();
            HistoryStore.SaveFavorites();
            FavGrid.ItemsSource = null;
            FavGrid.ItemsSource = HistoryStore.Favorites;
        }
        EndRename();
    }

    private void EndRename()
    {
        _renaming = null;
        RenameBox.IsVisible = false;
        RenameButton.Content = L.T("Umbenennen");
    }

    private void RemoveFav_Click(object sender, RoutedEventArgs e)
    {
        if (FavGrid.SelectedItem is not Favorite f) return;
        HistoryStore.Favorites.Remove(f);
        HistoryStore.SaveFavorites();
    }

    private void RunHist_Click(object sender, RoutedEventArgs e)
    {
        if (HistGrid.SelectedItem is HistoryEntry h) Run(h.Tool, h.Target);
    }

    private void HistGrid_DoubleTapped(object sender, TappedEventArgs e) => RunHist_Click(sender, e);

    private void FavFromHist_Click(object sender, RoutedEventArgs e)
    {
        if (HistGrid.SelectedItem is not HistoryEntry h) return;
        HistoryStore.AddFavorite(new Favorite
        {
            Tool = h.Tool,
            ToolTitle = h.ToolTitle,
            Target = h.Target,
            Name = string.IsNullOrEmpty(h.Target) ? Title(h.ToolTitle) : $"{Title(h.ToolTitle)}: {h.Target}",
        });
    }

    /// <summary>Zweimal klicken (innerhalb von 4 s) zum Leeren – ohne modalen Dialog.</summary>
    private void ClearHist_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryStore.History.Count == 0) return;
        if (DateTime.Now - _clearArmed > TimeSpan.FromSeconds(4))
        {
            _clearArmed = DateTime.Now;
            this.FindAncestorOfType<MainView>()?.ShowInfo("Zum Löschen des gesamten Verlaufs nochmals „Verlauf leeren“ klicken.");
            return;
        }
        _clearArmed = DateTime.MinValue;
        HistoryStore.ClearHistory();
    }
}
