using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Exif-Checker: Foto wählen, hineinziehen oder per Adresse laden – Metadaten auswerten, Standort zeigen, bereinigte Kopie speichern.</summary>
public partial class ExifView : ToolView
{
    private static readonly string[] ImagePatterns =
        { "*.jpg", "*.jpeg", "*.heic", "*.heif", "*.png", "*.webp", "*.tif", "*.tiff", "*.dng", "*.cr2", "*.cr3", "*.nef", "*.arw", "*.orf", "*.rw2", "*.raf", "*.avif", "*.gif" };

    private byte[] _data;
    private ExifResult _result;
    private PanelCheckSink _sink;

    public ExifView()
    {
        InitializeComponent();
        // iPhone/iPad: Mediathek über den eigenen Foto-Auswahldialog (Originaldatei mit Standort); am Computer: Dateiauswahl
        PhotoButton.IsVisible = Platform.PhotoPicker != null;
        FileButton.Classes.Set("accent", Platform.PhotoPicker == null);
        DropHint.IsVisible = !OperatingSystem.IsIOS();
        if (!OperatingSystem.IsIOS())
        {
            DragDrop.SetAllowDrop(DropZone, true);
            DropZone.AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
            DropZone.AddHandler(DragDrop.DropEvent, Drop);
        }
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return;
        if (File.Exists(parameter)) _ = LoadFileAsync(parameter);
        else UrlBox.Text = parameter;
    }

    public override void RunNow()
    {
        if (!string.IsNullOrWhiteSpace(UrlBox.Text)) Url_Click(null, null);
    }

    /* ------------------------------------------------------------------ Laden */

    private async void Photo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = await Platform.PhotoPicker!();
            if (picked is { } p) Show(p.Data, p.Name);
        }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private async void File_Click(object sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanOpen) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                // „public.data“ statt Bildtypen, damit iOS die Dateien-App zeigt (der Bild-Auswahldialog liefert Kopien ohne Standort)
                new FilePickerFileType(L.T("Bilder")) { Patterns = ImagePatterns, AppleUniformTypeIdentifiers = new[] { "public.data" } },
            },
        });
        if (files.Count == 0) return;
        try
        {
            await using var s = await files[0].OpenReadAsync();
            Show(await ReadAllAsync(s), files[0].Name);
        }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private async void Drop(object sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFile() is not IStorageFile file) return;
        try
        {
            await using var s = await file.OpenReadAsync();
            Show(await ReadAllAsync(s), file.Name);
        }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private async Task LoadFileAsync(string path)
    {
        try { Show(await File.ReadAllBytesAsync(path), Path.GetFileName(path)); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Url_Click(sender, e);
    }

    private async void Url_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url) || !CheckButton.IsEnabled) return;
        CheckButton.IsEnabled = false;
        StatusText.Text = L.T("Bild wird geladen …");
        try
        {
            using var cts = new CancellationTokenSource(60000);
            var (data, name) = await ExifChecker.DownloadAsync(url, cts.Token);
            Show(data, name);
        }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException ? ex.Message : L.T("Fehler: ") + NetUtil.ErrorText(ex); }
        finally { CheckButton.IsEnabled = true; }
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > ExifChecker.MaxBytes) throw new InvalidOperationException(L.T("Die Datei ist zu groß (über 80 MB)."));
        }
        return ms.ToArray();
    }

    /* ------------------------------------------------------------------ Anzeigen */

    private void Show(byte[] data, string name)
    {
        _sink = new PanelCheckSink(ResultsPanel);
        try
        {
            _result = ExifChecker.Analyze(data, name);
            _data = data;
        }
        catch (Exception ex)
        {
            _result = null;
            _data = null;
            PreviewRow.IsVisible = false;
            StatusText.Text = ex is InvalidOperationException ? ex.Message : L.T("Fehler: ") + NetUtil.ErrorText(ex);
            return;
        }

        StatusText.Text = "";
        foreach (var section in _result.Sections) _sink.Add(section);
        _sink.Finish();

        PreviewRow.IsVisible = true;
        try
        {
            using var ms = new MemoryStream(data);
            Preview.Source = new Bitmap(ms);
            PreviewCard.IsVisible = true;
        }
        catch { Preview.Source = null; PreviewCard.IsVisible = false; } // HEIC/RAW kann die Oberfläche nicht darstellen

        var verdict = _result.Sections[0].Items[0];
        SummaryText.Text = $"{name} · {verdict.Value}";
        SummaryText.Foreground = Palette.For(verdict.Level);
        StripButton.IsEnabled = _result.CanStrip;
        ToolTip.SetTip(StripButton, _result.CanStrip ? null
            : L.T("Für dieses Format nicht möglich (nur JPEG, PNG, WebP). iPhone: beim Teilen unter „Optionen“ den Standort abwählen."));
        MapButton.IsVisible = _result.HasLocation;
        MapCard.IsVisible = _result.HasLocation;
        if (_result.HasLocation)
            Map.SetPoints(new[] { new MapPoint(1, "", name, _result.Latitude!.Value, _result.Longitude!.Value, L.T("Aufnahmeort"), "", "", "target") });
    }

    /* ------------------------------------------------------------------ Aktionen */

    private async void Strip_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _result == null) return;
        try
        {
            var clean = ExifChecker.StripMetadata(_data);
            if (clean == null) { StatusText.Text = L.T("Für dieses Format nicht möglich (nur JPEG, PNG, WebP)."); return; }
            var ext = Path.GetExtension(_result.FileName).TrimStart('.');
            if (ext == "") ext = _result.Format.ToLowerInvariant() == "jpeg" ? "jpg" : _result.Format.ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(_result.FileName) + "-ohne-metadaten." + ext;

            if (OperatingSystem.IsIOS())
            {
                // iPhone/iPad: Teilen-Dialog („Bild sichern“ legt die bereinigte Kopie in Fotos ab)
                var path = Path.Combine(Path.GetTempPath(), name);
                await File.WriteAllBytesAsync(path, clean);
                NetUtil.OpenUrl(path);
                StatusText.Text = L.F("Bereinigte Kopie: {0} ({1} statt {2})", name, SiteCopier.FormatSize(clean.Length), SiteCopier.FormatSize(_data.Length));
                return;
            }
            var file = await PickSaveFileAsync(name, L.T("Bild"), ext);
            if (file == null) return;
            await using (var s = await file.OpenWriteAsync()) await s.WriteAsync(clean);
            StatusText.Text = L.F("Gespeichert: {0} ({1} statt {2})", file.Name, SiteCopier.FormatSize(clean.Length), SiteCopier.FormatSize(_data.Length));
        }
        catch (Exception ex) { StatusText.Text = ex is InvalidOperationException ? ex.Message : L.T("Fehler: ") + NetUtil.ErrorText(ex); }
    }

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.MapUrl is { } url) NetUtil.OpenUrl(url);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null) CopyText(string.Concat(_result.Sections.Select(s => s.ToText())));
    }
}
