using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SiteCopyView : ToolView
{
    private readonly ObservableCollection<CopyLogEntry> _log = new();
    private readonly ObservableCollection<ForeignHost> _hosts = new();
    private readonly ConcurrentQueue<CopyLogEntry> _incoming = new();
    private readonly DispatcherTimer _uiTimer;
    private CancellationTokenSource _cts;
    private SiteCopier _copier;
    private Stopwatch _sw;
    private string _projectFolder;
    private string _overwriteArmed;

    /// <summary>Standard: Downloads-Ordner (Dokumente liegt oft in OneDrive/iCloud – tausende Dateien würden synchronisiert).</summary>
    public static string DefaultFolder => OperatingSystem.IsIOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Websites")   // in der Dateien-App sichtbar
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Net-Stuff Websites");

    public SiteCopyView()
    {
        InitializeComponent();
        // Testmodus (--datadir): nichts in den echten Downloads-Ordner schreiben
        FolderBox.Text = UiOptions.DataDir != null ? Path.Combine(Platform.DataDir, "websites") : DefaultFolder;
        LogGrid.ItemsSource = _log;
        ForeignList.ItemsSource = _hosts;
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _uiTimer.Tick += (_, _) => FlushUi();
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (_cts == null) UrlBox.Text = parameter;
    }

    public override void RunNow() => Start_Click(null, null);

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && StartButton.IsEnabled) Start_Click(sender, e);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync(L.T("Zielordner wählen"));
        if (folder != null) FolderBox.Text = folder;
    }

    private static Uri ParseUrl(string text)
    {
        text = (text ?? "").Trim();
        if (text == "") return null;
        if (!text.Contains("://")) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;
    }

    private SiteCopyOptions ReadOptions() => new()
    {
        MaxDepth = (int)(DepthBox.Value ?? 5),
        MaxFiles = (int)(MaxFilesBox.Value ?? 5000),
        MaxFileBytes = (long)((MaxSizeBox.Value ?? 100) * 1024 * 1024),
        Parallel = (int)(ParallelBox.Value ?? 4),
        DelayMs = (int)(DelayBox.Value ?? 0),
        IncludeSubdomains = SubdomainsBox.IsChecked == true,
        StayBelowStartPath = BelowBox.IsChecked == true,
        RespectRobots = RobotsBox.IsChecked == true,
        Foreign = ForeignBox.SelectedIndex switch { 1 => ForeignMode.AutoResources, 2 => ForeignMode.Never, _ => ForeignMode.Ask },
        ExcludeExtensions = new HashSet<string>(
            (ExcludeBox.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('*').TrimStart('.')),
            StringComparer.OrdinalIgnoreCase),
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null) return;
        var url = ParseUrl(UrlBox.Text);
        if (url == null) { StatusText.Text = L.T("Bitte eine gültige URL eingeben (http/https)."); return; }
        var baseFolder = (FolderBox.Text ?? "").Trim();
        if (baseFolder == "") baseFolder = FolderBox.Text = DefaultFolder;

        var folderName = url.IsDefaultPort ? url.Host : $"{url.Host}_{url.Port}";
        var projectFolder = Path.Combine(baseFolder, folderName);
        // Vorhandene Kopie: zweiter Klick bestätigt das Überschreiben (ohne modalen Dialog)
        if (Directory.Exists(projectFolder) && Directory.EnumerateFileSystemEntries(projectFolder).Any() && _overwriteArmed != projectFolder)
        {
            _overwriteArmed = projectFolder;
            StatusText.Text = L.F("„{0}“ enthält bereits eine Kopie – zum Überschreiben nochmals „Kopieren“ klicken.", projectFolder);
            return;
        }
        _overwriteArmed = null;
        _projectFolder = projectFolder;

        _cts = new CancellationTokenSource();
        _log.Clear();
        _all.Clear();
        _hosts.Clear();
        while (_incoming.TryDequeue(out _)) { }
        SummaryPanel.Children.Clear();
        ForeignCard.IsVisible = false;
        WaitBanner.IsVisible = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenOfflineButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        StatusText.Text = L.T("Starte …");

        var copier = new SiteCopier(ReadOptions(), _projectFolder);
        _copier = copier;
        copier.FileProcessed += entry => _incoming.Enqueue(entry);
        copier.ForeignHostFound += host => Dispatcher.UIThread.Post(() =>
        {
            _hosts.Add(host);
            ForeignCard.IsVisible = true;
        });
        copier.WaitingForDecision += waiting => Dispatcher.UIThread.Post(() =>
        {
            WaitBanner.IsVisible = waiting;
            if (waiting) this.FindAncestorOfType<MainView>()?.ShowInfo("Website-Kopierer wartet auf Ihre Entscheidung zu fremden Domains");
        });
        copier.Phase += text => Dispatcher.UIThread.Post(() => StatusText.Text = text);

        _sw = Stopwatch.StartNew();
        _uiTimer.Start();
        string outcome;
        bool cancelled = false;
        try
        {
            await copier.RunAsync(url, _cts.Token);
            outcome = L.T("Fertig");
        }
        catch (OperationCanceledException)
        {
            outcome = L.T("Abgebrochen – bisher geladene Dateien sind offline nutzbar");
            cancelled = true;
        }
        catch (Exception ex)
        {
            outcome = L.T("Fehler: ") + NetUtil.ErrorText(ex);
        }
        finally
        {
            _uiTimer.Stop();
            _cts = null;
            FlushUi();
            Progress.IsIndeterminate = false;
            Progress.Value = Progress.Maximum;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            WaitBanner.IsVisible = false;
        }

        StatusText.Text = $"{outcome} · {Stats(copier)}";
        OpenFolderButton.IsEnabled = Directory.Exists(_projectFolder);
        OpenOfflineButton.IsEnabled = File.Exists(copier.RootIndex) && copier.StartFile != null;
        ShowSummary(copier, url, outcome, cancelled);
    }

    private string Stats(SiteCopier c) =>
        L.F("{0} Dateien ({1} Seiten), {2}, {3} Fehler, {4:0} s", c.FilesDone, c.PagesDone, SiteCopier.FormatSize(c.BytesDone), c.Errors, _sw.Elapsed.TotalSeconds);

    private void FlushUi()
    {
        int n = 0;
        bool errorsOnly = ErrorsOnlyBox.IsChecked == true;
        while (n < 2000 && _incoming.TryDequeue(out var entry))
        {
            _all.Add(entry);
            if (!errorsOnly || IsProblem(entry)) _log.Add(entry);
            n++;
        }
        var c = _copier;
        if (c == null || _cts == null) return;
        int queued = c.QueueCount;
        StatusText.Text = L.F("{0} in Warteschlange · ", queued) + Stats(c);
        int total = c.FilesDone + c.Errors + queued;
        if (total > 0 && c.FilesDone > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = total;
            Progress.Value = c.FilesDone + c.Errors;
        }
    }

    private readonly List<CopyLogEntry> _all = new();
    private static bool IsProblem(CopyLogEntry c) => c.IsError || c.Status != "OK";

    private void ShowSummary(SiteCopier c, Uri url, string outcome, bool cancelled)
    {
        var items = new List<CheckItem>
        {
            new("Ergebnis", outcome, cancelled ? Level.Warn : c.Errors > 0 ? Level.Warn : Level.Ok),
            new("Website", url.AbsoluteUri),
            new("Gespeichert in", _projectFolder),
            new("Startdatei", c.StartFile != null ? Path.GetRelativePath(_projectFolder, c.StartFile) : L.T("Startseite konnte nicht geladen werden"),
                c.StartFile != null ? Level.Info : Level.Bad),
            new("Dateien", L.F("{0} ({1} Seiten)", c.FilesDone, c.PagesDone)),
            new("Datenmenge", SiteCopier.FormatSize(c.BytesDone)),
            new("Fehler", c.Errors.ToString(), c.Errors > 0 ? Level.Warn : Level.Ok),
            new("Dauer", $"{_sw.Elapsed:hh\\:mm\\:ss}"),
        };
        if (c.LimitReached)
            items.Add(new CheckItem("Dateilimit", L.T("Maximale Dateianzahl erreicht – weitere Seiten verweisen auf die Online-Version (Limit unter Optionen erhöhen)."), Level.Warn));
        var allowed = _hosts.Where(h => h.Decision == HostDecision.Allowed).Select(h => h.Host).ToList();
        var denied = _hosts.Where(h => h.Decision != HostDecision.Allowed).Select(h => h.Host).ToList();
        if (allowed.Count > 0) items.Add(new CheckItem("Fremde Domains geladen", string.Join(", ", allowed)));
        if (denied.Count > 0) items.Add(new CheckItem("Fremde Domains nicht geladen", string.Join(", ", denied)));
        SummaryPanel.Children.Add(UiFactory.Section(new CheckSection("Zusammenfassung", items,
            "Dynamische Inhalte (Formulare, Suche, per JavaScript nachgeladene Daten) funktionieren offline nicht.")));
    }

    /* ------------------------------------------------------------ fremde Domains */

    private void Decide(string host, bool allow) => _copier?.Decide(host, allow);

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string host) Decide(host, true);
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string host) Decide(host, false);
    }

    private void AllowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var h in _hosts.Where(h => h.IsPending).ToList()) Decide(h.Host, true);
    }

    private void DenyAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var h in _hosts.Where(h => h.IsPending).ToList()) Decide(h.Host, false);
    }

    /* ------------------------------------------------------------ Sonstiges */

    private void ErrorsOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (_log == null) return;
        bool errorsOnly = ErrorsOnlyBox.IsChecked == true;
        _log.Clear();
        foreach (var entry in errorsOnly ? _all.Where(IsProblem) : _all) _log.Add(entry);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = L.T("Wird abgebrochen …");
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_projectFolder != null && Directory.Exists(_projectFolder)) NetUtil.OpenUrl(_projectFolder);
    }

    private void OpenOffline_Click(object sender, RoutedEventArgs e)
    {
        var index = _copier?.RootIndex;
        if (index != null && File.Exists(index)) NetUtil.OpenUrl(index);
    }
}
