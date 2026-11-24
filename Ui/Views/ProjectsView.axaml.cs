using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class ProjectsView : ToolView
{
    private Project _current;
    private bool _loading, _refreshing;
    private DateTime _deleteArmed = DateTime.MinValue;

    public ProjectsView()
    {
        InitializeComponent();
        ProjectList.ItemsSource = ProjectStore.Projects;
        ProjectList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(Project.Display));
        EntryList.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(ReportEntry.Summary));
        ProjectList.SelectionChanged += (_, _) => { if (!_refreshing) SelectProject(ProjectList.SelectedItem as Project); };
        EntryList.SelectionChanged += (_, _) => CommentBox.Text = SelectedEntry?.Comment ?? "";
        foreach (var box in new[] { CustomerBox, SiteBox, AddressBox, ContactBox, NotesBox }) box.LostFocus += (_, _) => FieldsChanged();
        CommentBox.LostFocus += (_, _) => CommentChanged();
    }

    public override bool IsTool => false;
    public override string PrimaryTarget => "";

    public override void OnShown()
    {
        var want = _current ?? ProjectStore.Active ?? ProjectStore.Projects.FirstOrDefault();
        if (ProjectList.SelectedItem != want) ProjectList.SelectedItem = want;
        else { RefreshEntries(); ShowPreview(); }
    }

    private ReportEntry SelectedEntry => EntryList.SelectedItem as ReportEntry;

    private void SelectProject(Project p)
    {
        SaveFields();
        _current = p;
        DetailPanel.IsEnabled = _current != null;
        _loading = true;
        CustomerBox.Text = _current?.Customer ?? "";
        SiteBox.Text = _current?.Site ?? "";
        AddressBox.Text = _current?.Address ?? "";
        ContactBox.Text = _current?.ContactPerson ?? "";
        NotesBox.Text = _current?.Notes ?? "";
        _loading = false;
        RefreshEntries();
        if (_current != null && ProjectStore.Active != _current) ProjectStore.Active = _current;
        ShowPreview();
    }

    private void RefreshEntries()
    {
        EntryList.ItemsSource = null;
        EntryList.ItemsSource = _current?.Entries;
        CommentBox.Text = "";
    }

    private void SaveFields()
    {
        if (_current == null || _loading) return;
        _current.Customer = (CustomerBox.Text ?? "").Trim();
        _current.Site = (SiteBox.Text ?? "").Trim();
        _current.Address = (AddressBox.Text ?? "").Trim();
        _current.ContactPerson = (ContactBox.Text ?? "").Trim();
        _current.Notes = (NotesBox.Text ?? "").Trim();
        ProjectStore.Save(_current);
    }

    private void FieldsChanged()
    {
        SaveFields();
        // Anzeige der Projektliste aktualisieren (Name kann sich geändert haben)
        _refreshing = true;
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = ProjectStore.Projects;
        ProjectList.SelectedItem = _current;
        _refreshing = false;
        ShowPreview();
    }

    private void CommentChanged()
    {
        if (SelectedEntry == null || _current == null) return;
        SelectedEntry.Comment = (CommentBox.Text ?? "").Trim();
        ProjectStore.Save(_current);
        ShowPreview();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var p = ProjectStore.Create(L.T("Neuer Kunde"));
        ProjectList.SelectedItem = p;
        CustomerBox.Focus();
        CustomerBox.SelectAll();
    }

    /// <summary>Zweimal klicken (innerhalb von 4 s) zum Löschen – ohne modalen Dialog.</summary>
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (DateTime.Now - _deleteArmed > TimeSpan.FromSeconds(4))
        {
            _deleteArmed = DateTime.Now;
            StatusText.Text = L.F("Projekt „{0}“ mit {1} Einträgen löschen? Zum Bestätigen nochmals „Löschen“ klicken.", _current.Display, _current.Entries.Count);
            return;
        }
        _deleteArmed = DateTime.MinValue;
        var p = _current;
        _current = null;
        ProjectStore.Delete(p);
        ProjectList.SelectedItem = ProjectStore.Projects.FirstOrDefault();
        StatusText.Text = "";
    }

    private void Move(int delta)
    {
        if (_current == null || SelectedEntry == null) return;
        var e = SelectedEntry;
        int i = _current.Entries.IndexOf(e), j = i + delta;
        if (j < 0 || j >= _current.Entries.Count) return;
        _current.Entries.RemoveAt(i);
        _current.Entries.Insert(j, e);
        ProjectStore.Save(_current);
        RefreshEntries();
        EntryList.SelectedItem = e;
        ShowPreview();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Move(1);

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || SelectedEntry == null) return;
        _current.Entries.Remove(SelectedEntry);
        ProjectStore.Save(_current);
        RefreshEntries();
        ShowPreview();
    }

    /* ------------------------------------------------------------------ Export */

    private string FileName(string ext) =>
        string.Join("_", $"Netzwerkbericht {_current.Display} {DateTime.Now:yyyy-MM-dd}".Split(Path.GetInvalidFileNameChars())) + ext;

    private string WriteTempHtml()
    {
        var file = Path.Combine(Platform.DataDir, "preview.html");
        File.WriteAllText(file, ReportRenderer.Html(_current));
        return file;
    }

    private async void Html_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SaveFields();
        try
        {
            var path = await SaveTextAsync(FileName(".html"), "HTML", "html", ReportRenderer.Html(_current), bom: false);
            if (path != null) StatusText.Text = L.F("Gespeichert: {0}", path);
        }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + ex.Message; }
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SaveFields();
        NetUtil.OpenUrl(WriteTempHtml());
    }

    private async void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SaveFields();
        if (PdfExport.FindBrowser() == null)
        {
            NetUtil.OpenUrl(WriteTempHtml());
            StatusText.Text = L.T("Für den direkten PDF-Export wird Chrome, Edge oder Brave benötigt. Der Bericht wurde im Browser geöffnet – dort „Drucken → Als PDF sichern“ wählen.");
            return;
        }
        var file = await PickSaveFileAsync(FileName(".pdf"), "PDF", "pdf");
        var path = file?.TryGetLocalPath();
        if (path == null) return;
        StatusText.Text = L.T("PDF wird erstellt …");
        bool ok = await PdfExport.HtmlToPdfAsync(WriteTempHtml(), path);
        StatusText.Text = ok ? L.F("PDF gespeichert: {0}", path) : L.T("PDF konnte nicht erstellt werden.");
        if (ok) NetUtil.OpenUrl(path);
    }

    /* ------------------------------------------------------------------ Vorschau */

    private void ShowPreview()
    {
        PreviewPanel.Children.Clear();
        if (_current == null)
        {
            PreviewPanel.Children.Add(new TextBlock { Text = L.T("Kein Projekt ausgewählt."), Classes = { "Status" } });
            return;
        }
        var p = _current;
        var s = AppSettings.Current;
        PreviewPanel.Children.Add(new TextBlock { Text = L.T("Netzwerkbericht"), FontSize = 24, FontWeight = FontWeight.SemiBold });
        PreviewPanel.Children.Add(new TextBlock { Text = p.Display, Classes = { "Status" }, Margin = new Thickness(0, 0, 0, 10) });

        var meta = new List<CheckItem>();
        void M(string name, string value) { if (!string.IsNullOrWhiteSpace(value)) meta.Add(new CheckItem(name, value)); }
        M("Kunde", p.Customer);
        M("Standort", p.Site);
        M("Adresse", p.Address);
        M("Ansprechpartner", p.ContactPerson);
        M("Techniker", s.Technician);
        M("Erstellt von", s.CompanyName);
        M("Datum", DateTime.Now.ToString("dd.MM.yyyy"));
        PreviewPanel.Children.Add(UiFactory.Section(new CheckSection("Projekt", meta)));
        if (!string.IsNullOrWhiteSpace(p.Notes))
            PreviewPanel.Children.Add(new SelectableTextBlock { Text = p.Notes, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });

        if (p.Entries.Count == 0)
            PreviewPanel.Children.Add(new TextBlock
            {
                Text = L.T("Noch keine Einträge – in einem Werkzeug „+ Zum Bericht“ klicken."),
                Classes = { "Status" }, TextWrapping = TextWrapping.Wrap,
            });

        foreach (var entry in p.Entries) PreviewPanel.Children.Add(EntryPreview(entry));
    }

    private static Control EntryPreview(ReportEntry entry)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 14, 0, 0) };
        panel.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) });
        panel.Children.Add(new TextBlock
        {
            Text = entry.Title + (string.IsNullOrEmpty(entry.Target) ? "" : " – " + entry.Target),
            FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = entry.Time.ToString("dd.MM.yyyy HH:mm"), Classes = { "Status" } });
        if (!string.IsNullOrWhiteSpace(entry.Comment))
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD4, 0xA7, 0x2C)), BorderThickness = new Thickness(4, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromArgb(40, 0xD4, 0xA7, 0x2C)), Padding = new Thickness(10, 6),
                Child = new TextBlock { Text = entry.Comment, TextWrapping = TextWrapping.Wrap },
            });

        foreach (var b in entry.Blocks)
        {
            switch (b.Kind)
            {
                case "headline":
                    panel.Children.Add(new TextBlock { Text = b.Text, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                    break;
                case "section":
                    panel.Children.Add(UiFactory.Section(new CheckSection(b.Title ?? "", (b.Items ?? new()).Select(i => new CheckItem(i.Name, i.Value, i.Level)))));
                    break;
                case "table":
                    panel.Children.Add(TablePreview(b));
                    break;
                case "image":
                    var img = ImageOf(b.ImageBase64);
                    if (img != null) panel.Children.Add(new Image { Source = img, MaxHeight = 380, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform });
                    break;
                case "text":
                    var t = b.Text ?? "";
                    panel.Children.Add(new SelectableTextBlock
                    {
                        Text = t.Length > 4000 ? t[..4000] + "\n…" : t,
                        FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                    });
                    break;
            }
        }
        return panel;
    }

    private static Control TablePreview(ReportBlock b)
    {
        const int maxRows = 40;
        var headers = b.Headers ?? new();
        var rows = (b.Rows ?? new()).Take(maxRows).ToList();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", headers.Select(_ => "Auto"))) };
        for (int r = 0; r <= rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < headers.Count; c++)
        {
            var h = new TextBlock { Text = headers[c], FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 14, 4) };
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < headers.Count && c < rows[r].Count; c++)
            {
                var cell = new TextBlock { Text = rows[r][c], Margin = new Thickness(0, 1, 14, 1), MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        var panel = new StackPanel();
        panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        int more = (b.Rows?.Count ?? 0) - rows.Count;
        if (more > 0) panel.Children.Add(new TextBlock { Text = L.F("… {0} weitere Zeilen (im Bericht vollständig)", more), Classes = { "Status" } });
        return panel;
    }

    private static Bitmap ImageOf(string base64)
    {
        try { return string.IsNullOrEmpty(base64) ? null : new Bitmap(new MemoryStream(Convert.FromBase64String(base64))); }
        catch { return null; }
    }
}
