using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class ProjectsPage : ToolPage
{
    private Project _current;
    private bool _loading;
    private bool _webReady;

    public ProjectsPage()
    {
        InitializeComponent();
        ProjectList.ItemsSource = ProjectStore.Projects;
        Loaded += async (_, _) =>
        {
            ProjectList.SelectedItem = ProjectStore.Active ?? ProjectStore.Projects.FirstOrDefault();
            await InitWebAsync();
            ShowPreview();
        };
    }

    public override string PrimaryTarget => "";

    public override void OnNavigatedTo(object parameter)
    {
        if (_current != null) { RefreshEntries(); ShowPreview(); }
    }

    private async Task InitWebAsync()
    {
        if (_webReady) return;
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDir, "WebView2"));
            await Preview.EnsureCoreWebView2Async(env);
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webReady = true;
        }
        catch (Exception ex) { StatusText.Text = L.T("Vorschau nicht verfügbar: ") + NetUtil.ErrorText(ex); }
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveFields();
        _current = ProjectList.SelectedItem as Project;
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
        _current.Customer = CustomerBox.Text.Trim();
        _current.Site = SiteBox.Text.Trim();
        _current.Address = AddressBox.Text.Trim();
        _current.ContactPerson = ContactBox.Text.Trim();
        _current.Notes = NotesBox.Text.Trim();
        ProjectStore.Save(_current);
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveFields();
        // Anzeige in Listen aktualisieren (gemeinsame Ansicht mit der Projektauswahl im Hauptfenster)
        ProjectList.Items.Refresh();
        ShowPreview();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var p = ProjectStore.Create(L.T("Neuer Kunde"));
        ProjectList.SelectedItem = p;
        CustomerBox.Focus();
        CustomerBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(L.F("Projekt „{0}“ mit {1} Einträgen löschen?", _current.Display, _current.Entries.Count), "Net-Stuff", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var p = _current;
        _current = null;
        ProjectStore.Delete(p);
        ProjectList.SelectedItem = ProjectStore.Projects.FirstOrDefault();
    }

    private ReportEntry SelectedEntry => EntryList.SelectedItem as ReportEntry;

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CommentBox.Text = SelectedEntry?.Comment ?? "";
    }

    private void Comment_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry == null || _current == null) return;
        SelectedEntry.Comment = CommentBox.Text.Trim();
        ProjectStore.Save(_current);
        ShowPreview();
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

    private void ShowPreview()
    {
        if (!_webReady) return;
        if (_current == null)
        {
            Preview.NavigateToString($"<html><body style='font-family:Segoe UI;color:#888;padding:40px'>{System.Net.WebUtility.HtmlEncode(L.T("Kein Projekt ausgewählt."))}</body></html>");
            return;
        }
        var html = ReportRenderer.Html(_current);
        // NavigateToString ist auf 2 MB begrenzt – große Berichte über Datei laden
        var file = Path.Combine(App.DataDir, "preview.html");
        File.WriteAllText(file, html);
        Preview.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        SaveFields();
        ShowPreview();
    }

    private string FileName(string ext)
    {
        var name = string.Join("_", ($"Netzwerkbericht {_current.Display} {DateTime.Now:yyyy-MM-dd}").Split(Path.GetInvalidFileNameChars()));
        return name + ext;
    }

    private void Html_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        SaveFields();
        var dlg = new SaveFileDialog { Filter = "HTML|*.html", FileName = FileName(".html") };
        if (dlg.ShowDialog() != true) return;
        File.WriteAllText(dlg.FileName, ReportRenderer.Html(_current));
        StatusText.Text = L.F("Gespeichert: {0}", dlg.FileName);
    }

    private async void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || !_webReady) return;
        SaveFields();
        var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = FileName(".pdf") };
        if (dlg.ShowDialog() != true) return;
        ShowPreview();
        await Task.Delay(1200); // Bilder laden lassen
        var settings = Preview.CoreWebView2.Environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.MarginTop = settings.MarginBottom = 0.5;
        settings.MarginLeft = settings.MarginRight = 0.45;
        bool ok = await Preview.CoreWebView2.PrintToPdfAsync(dlg.FileName, settings);
        StatusText.Text = ok ? L.F("PDF gespeichert: {0}", dlg.FileName) : L.T("PDF konnte nicht erstellt werden.");
        if (ok) Services.NetUtil.OpenUrl(dlg.FileName);
    }
}
