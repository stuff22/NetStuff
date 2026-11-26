using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetStuff.Infrastructure;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Speicher-Checker: Ordnergrößen wie TreeSize, Aufräum-Kandidaten, alte Dateien, Duplikate – auch auf Netzwerkfreigaben.</summary>
public partial class DiskUsageView : ToolView
{
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource _cts;
    private DiskAnalyzer _scanner;
    private DiskNode _root, _current;
    private DiskReport _report;
    private string _text = "";
    private bool _locationsLoaded;

    /// <summary>Mac-App-Store-Build: macOS gibt nur Ordner frei, die im Auswahldialog gewählt wurden.</summary>
    private static bool Sandboxed => Environment.GetEnvironmentVariable("APP_SANDBOX_CONTAINER_ID") != null;

    private static string RevealText => OperatingSystem.IsWindows() ? "Im Explorer zeigen" : "Im Finder zeigen";

    public DiskUsageView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => ShowProgress();

        RevealCurrentButton.Content = L.T(RevealText);
        foreach (var g in Grids)
            if (g.ContextMenu?.Items.Count > 0 && g.ContextMenu.Items[0] is MenuItem m) m.Header = L.T(RevealText);
        if (OperatingSystem.IsWindows()) TargetBox.PlaceholderText = @"C:\  ·  D:\Daten  ·  \\server\freigabe";
        NetworkHint.Text = L.T(OperatingSystem.IsWindows()
            ? "Netzwerkfreigaben: UNC-Pfad wie \\\\server\\freigabe eingeben oder ein verbundenes Netzlaufwerk wählen. Über das Netzwerk dauert der Scan länger – Unterordner werden parallel gelesen."
            : Sandboxed
                ? "Netzwerkfreigaben zuerst im Finder verbinden (⌘K, z. B. smb://nas/freigabe). macOS gibt der App nur Ordner frei, die über „…“ ausgewählt werden – die Schnellauswahl öffnet daher den Auswahldialog."
                : "Netzwerkfreigaben zuerst im Finder verbinden (⌘K, z. B. smb://nas/freigabe) – sie erscheinen dann unter /Volumes in der Schnellauswahl. Über das Netzwerk dauert der Scan länger – Unterordner werden parallel gelesen.");
        if (UiOptions.DataDir != null) TargetBox.Text = Platform.DataDir;   // Testmodus
        Loaded += (_, _) => LoadLocations();
    }

    private DataGrid[] Grids => new[] { ContentGrid, LargestGrid, OldGrid, DupGrid };

    public override void OnNavigatedTo(string parameter)
    {
        if (!string.IsNullOrEmpty(parameter) && _cts == null) TargetBox.Text = parameter;
    }

    public override void RunNow() => Start_Click(null, null);

    private async void LoadLocations()
    {
        if (_locationsLoaded) return;
        _locationsLoaded = true;
        // Getrennte Netzlaufwerke können beim Abfragen hängen – darum im Hintergrund
        var list = await Task.Run(DiskAnalyzer.Locations);
        foreach (var loc in list)
        {
            var content = new StackPanel { MinWidth = 170 };
            content.Children.Add(new TextBlock { Text = (loc.Kind == "Netzlaufwerk" ? "🌐 " : loc.Kind == "Ordner" ? "📁 " : "💽 ") + loc.Title, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = loc.Detail, FontSize = 11, Opacity = 0.75 });
            if (loc.Total > 0)
                content.Children.Add(new ProgressBar { Value = loc.UsedPercent, Maximum = 100, Height = 4, MinHeight = 0, MinWidth = 0, Margin = new Thickness(0, 4, 0, 0) });
            var b = new Button { Content = content, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 6), HorizontalContentAlignment = HorizontalAlignment.Left };
            b.Click += async (_, _) =>
            {
                if (_cts != null) return;
                if (Sandboxed)
                {
                    var picked = await PickFolderAsync(L.T("Ordner wählen"));
                    if (picked == null) return;
                    TargetBox.Text = picked;
                }
                else TargetBox.Text = loc.Path;
                Start_Click(null, null);
            };
            LocationsPanel.Children.Add(b);
        }
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync(L.T("Ordner wählen"));
        if (folder != null) TargetBox.Text = folder;
    }

    /* ------------------------------------------------------------ Scan */

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var path = (TargetBox.Text ?? "").Trim();
        if (path == "") { StatusText.Text = L.T("Bitte einen Ordner, ein Laufwerk oder eine Freigabe angeben."); return; }

        _cts = new CancellationTokenSource();
        _scanner = new DiskAnalyzer();
        SetBusy(true);
        StatusText.Text = DiskAnalyzer.IsNetworkPath(path) ? L.T("Lese Netzwerkfreigabe …") : L.T("Lese Ordner …");
        _timer.Start();
        try
        {
            _root = await _scanner.ScanAsync(path, _cts.Token);
            StatusText.Text = L.T("Werte aus …");
            int years = SelectedYears();
            var root = _root;
            var (report, old) = await Task.Run(() => (DiskAnalyzer.Analyze(root), DiskAnalyzer.OldFiles(root, years)));
            _report = report;
            ShowResults(old);
            StatusText.Text = (_scanner.Cancelled ? L.T("Abgebrochen – Teilergebnis") : L.T("Fertig")) + " · "
                + L.F("{0} in {1:N0} Dateien, {2:N0} Ordner, {3:0.0} s", _root.SizeText, _root.Files, _root.Dirs, _scanner.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is InvalidOperationException ? ex.Message : L.T("Fehler: ") + NetUtil.ErrorText(ex);
        }
        finally
        {
            _timer.Stop();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
        DupButton.IsEnabled = !busy && _root != null;
        CopyButton.IsEnabled = CsvButton.IsEnabled = !busy && _root != null;
        Tabs.IsEnabled = !busy && _root != null;
        Progress.IsIndeterminate = busy;
        if (!busy) Progress.Value = 0;
    }

    private void ShowProgress()
    {
        var s = _scanner;
        if (s == null || _cts == null) return;
        StatusText.Text = L.F("{0:N0} Dateien · {1:N0} Ordner · {2}", s.FilesFound, s.DirsFound, DiskAnalyzer.FormatSize(s.BytesFound))
            + (s.CurrentPath != null ? " · " + s.CurrentPath : "");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = L.T("Wird abgebrochen …");
    }

    private void ShowResults(OldFilesResult old)
    {
        var sections = DiskAnalyzer.Sections(_scanner, _report, old);
        SummaryPanel.Children.Clear();
        foreach (var s in sections) SummaryPanel.Children.Add(UiFactory.Section(s));
        _text = string.Concat(sections.Select(s => s.ToText()));

        FolderTree.ItemsSource = new[] { _root };
        ShowFolder(_root);
        Dispatcher.UIThread.Post(() =>
        {
            if (FolderTree.ContainerFromIndex(0) is TreeViewItem item) item.IsExpanded = true;
        }, DispatcherPriority.Loaded);
        LargestGrid.ItemsSource = _report.LargestFiles;
        TypesGrid.ItemsSource = _report.Types;
        ShowOld(old);
        DupGrid.ItemsSource = null;
        DupStatus.Text = "";
        Tabs.SelectedIndex = 0;
    }

    /* ------------------------------------------------------------ Ordner */

    private void ShowFolder(DiskNode node)
    {
        if (node == null || !node.IsDir) return;
        _current = node;
        CurrentPathText.Text = node.FullPath + "  ·  " + node.SizeText + (node.Error != null ? "  ·  ⚠ " + node.Error : "");
        ContentGrid.ItemsSource = node.Items;
    }

    private void FolderTree_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderTree.SelectedItem is DiskNode n) ShowFolder(n);
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Parent != null) ShowFolder(_current.Parent);
    }

    private void RevealCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null) DiskAnalyzer.Reveal(_current.FullPath);
    }

    private void ContentGrid_DoubleTapped(object sender, TappedEventArgs e)
    {
        if (ContentGrid.SelectedItem is not DiskNode n) return;
        if (n.IsDir) ShowFolder(n);
        else DiskAnalyzer.Reveal(n.FullPath);
    }

    private void Grid_DoubleTapped(object sender, TappedEventArgs e)
    {
        var path = PathOf((sender as DataGrid)?.SelectedItem);
        if (path != null) DiskAnalyzer.Reveal(path);
    }

    private static string PathOf(object item) => item switch
    {
        DiskNode n => n.FullPath,
        DuplicateGroup g => g.FirstPath,
        _ => null,
    };

    /// <summary>Ausgewählte Zeile der Tabelle im sichtbaren Reiter.</summary>
    private object SelectedRow() => Tabs.SelectedIndex switch
    {
        1 => ContentGrid.SelectedItem,
        2 => LargestGrid.SelectedItem,
        4 => OldGrid.SelectedItem,
        5 => DupGrid.SelectedItem,
        _ => null,
    };

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var path = PathOf(SelectedRow());
        if (path != null) DiskAnalyzer.Reveal(path);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedRow();
        CopyText(item is DuplicateGroup g ? g.PathsText : PathOf(item));
    }

    /* ------------------------------------------------------------ Alte Dateien */

    private int SelectedYears() => (YearsBox.SelectedItem as ComboBoxItem)?.Tag is string s && int.TryParse(s, out var y) ? y : 2;

    private async void Years_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_root == null || OldGrid == null) return;
        int years = SelectedYears();
        var root = _root;
        ShowOld(await Task.Run(() => DiskAnalyzer.OldFiles(root, years)));
    }

    private void ShowOld(OldFilesResult old)
    {
        OldGrid.ItemsSource = old.Largest;
        OldSummary.Text = L.F("{0} in {1:N0} Dateien ({2:0.0} % des Ordners)", DiskAnalyzer.FormatSize(old.Size), old.Count, old.Percent)
            + (old.Count > old.Largest.Count ? " · " + L.F("die {0:N0} größten werden angezeigt", old.Largest.Count) : "");
    }

    /* ------------------------------------------------------------ Duplikate */

    private async void Dup_Click(object sender, RoutedEventArgs e)
    {
        if (_root == null || _cts != null) return;
        long min = (MinSizeBox.SelectedItem as ComboBoxItem)?.Tag is string s && long.TryParse(s, out var m) ? m : 1 << 20;
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = DupButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        Progress.IsIndeterminate = false;
        Progress.Maximum = 1;
        Progress.Value = 0;
        DupGrid.ItemsSource = null;
        DupStatus.Text = L.T("Vergleiche Dateien …");
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total <= 0) return;
            Progress.Maximum = p.Total;
            Progress.Value = p.Done;
            DupStatus.Text = L.F("{0} von {1} gelesen", DiskAnalyzer.FormatSize(p.Done), DiskAnalyzer.FormatSize(p.Total));
        });
        try
        {
            var groups = await DiskAnalyzer.FindDuplicatesAsync(_root, min, progress, _cts.Token);
            DupGrid.ItemsSource = groups;
            DupStatus.Text = groups.Count == 0
                ? L.T("Keine Duplikate gefunden.")
                : L.F("{0:N0} Gruppen · {1} verschwendet", groups.Count, DiskAnalyzer.FormatSize(groups.Sum(g => g.Wasted)));
            if (groups.Count > 0)
                _text += new CheckSection("Duplikate", groups.Take(30).Select(g => new CheckItem(g.Name, $"{g.CountText} × {g.SizeText} → {g.WastedText}\n{g.PathsText}"))).ToText();
        }
        catch (OperationCanceledException) { DupStatus.Text = L.T("Abgebrochen"); }
        catch (Exception ex) { DupStatus.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally
        {
            _cts = null;
            StartButton.IsEnabled = DupButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Progress.Value = 0;
        }
    }

    /* ------------------------------------------------------------ Export */

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_text);

    private async void Csv_Click(object sender, RoutedEventArgs e)
    {
        if (_root == null) return;
        var name = string.Concat((_root.Name == "" ? _root.FullPath : _root.Name).Split(Path.GetInvalidFileNameChars())).Trim();
        try
        {
            var saved = await SaveTextAsync($"Speicher {(name == "" ? "Laufwerk" : name)} {DateTime.Now:yyyy-MM-dd}.csv", "CSV", "csv", DiskAnalyzer.FoldersCsv(_root));
            if (saved != null) StatusText.Text = L.F("Gespeichert: {0}", saved);
        }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }
}
