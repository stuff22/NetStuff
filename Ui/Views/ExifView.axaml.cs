using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using NetStuff.Infrastructure;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>Exif-Checker: alle Metadaten eines Fotos/Videos verständlich erklärt, Kopie ohne Metadaten speichern.</summary>
public partial class ExifView : ToolView
{
    private const long MaxInMemory = 300L * 1024 * 1024;
    private PanelCheckSink _sink;
    private byte[] _data;          // Dateiinhalt (bis 300 MB), sonst null und _path
    private string _path, _name;
    private ExifResult _result;

    private static readonly string[] Patterns =
    {
        "*.jpg", "*.jpeg", "*.heic", "*.heif", "*.png", "*.tif", "*.tiff", "*.webp", "*.gif", "*.bmp", "*.avif",
        "*.dng", "*.cr2", "*.cr3", "*.nef", "*.nrw", "*.arw", "*.orf", "*.rw2", "*.raf", "*.pef", "*.srw", "*.psd",
        "*.mp4", "*.mov", "*.m4v", "*.3gp", "*.avi",
    };

    public ExifView()
    {
        InitializeComponent();
        PhotosButton.IsVisible = NetStuff.Platform.PhotoPicker != null;
        HintText.Text = L.T(OperatingSystem.IsIOS()
            ? "„Aus Fotos wählen“ liefert das Original mit allen Daten. Bilder, die per WhatsApp, Signal oder Mail kamen, zuerst in „Dateien“ sichern und über „Datei öffnen“ laden."
            : "Tipp: Fotos einfach auf dieses Fenster ziehen. Unterstützt JPEG, HEIC, PNG, WebP, TIFF, RAW (DNG, CR2/CR3, NEF, ARW …) sowie MP4/MOV.");
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (!string.IsNullOrEmpty(parameter)) TargetBox.Text = parameter;
    }

    public override void RunNow() => Check_Click(null, null);

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    /* ------------------------------------------------------------ Laden */

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var text = (TargetBox.Text ?? "").Trim().Trim('"');
        if (text == "" || !CheckButton.IsEnabled) return;
        if (text == _name && _data != null) { await AnalyzeAsync(); return; }   // Beispiel / Fotos-Auswahl erneut auswerten
        if (text == ExifAnalyzer.SampleName && !File.Exists(text)) { Sample_Click(sender, e); return; }   // z. B. aus dem Verlauf
        try
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = L.T("Lade Bild herunter …");
                using var cts = new CancellationTokenSource(60000);
                var bytes = await NetUtil.Http.GetByteArrayAsync(text, cts.Token);
                SetData(bytes, Path.GetFileName(new Uri(text).AbsolutePath) is { Length: > 0 } n ? n : "download", null);
            }
            else
            {
                if (!File.Exists(text)) { StatusText.Text = L.F("„{0}“ wurde nicht gefunden.", text); return; }
                var len = new FileInfo(text).Length;
                SetData(len <= MaxInMemory ? await File.ReadAllBytesAsync(text) : null, Path.GetFileName(text), text);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
            return;
        }
        await AnalyzeAsync();
    }

    private void SetData(byte[] data, string name, string path)
    {
        _data = data;
        _name = name;
        _path = path;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanOpen) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L.T("Fotos und Videos")) { Patterns = Patterns, AppleUniformTypeIdentifiers = new[] { "public.image", "public.movie" }, MimeTypes = new[] { "image/*", "video/*" } },
                new FilePickerFileType(L.T("Alle Dateien")) { Patterns = new[] { "*" } },
            },
        });
        if (files.Count > 0) await LoadStorageFileAsync(files[0]);
    }

    private async Task LoadStorageFileAsync(IStorageFile file)
    {
        try
        {
            var local = file.TryGetLocalPath();
            if (local != null && File.Exists(local))
            {
                TargetBox.Text = local;
                var len = new FileInfo(local).Length;
                SetData(len <= MaxInMemory ? await File.ReadAllBytesAsync(local) : null, Path.GetFileName(local), local);
            }
            else
            {
                // iOS / Sandbox: nur als Datenstrom lesbar
                await using var s = await file.OpenReadAsync();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                SetData(ms.ToArray(), file.Name, null);
                TargetBox.Text = file.Name;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
            return;
        }
        await AnalyzeAsync();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file != null) await LoadStorageFileAsync(file);
    }

    private async void Photos_Click(object sender, RoutedEventArgs e)
    {
        var picker = NetStuff.Platform.PhotoPicker;
        if (picker == null) return;
        try
        {
            var picked = await picker();
            if (picked is not { } p || p.Data == null || p.Data.Length == 0) return;
            SetData(p.Data, p.Name, null);
            TargetBox.Text = p.Name;
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
            return;
        }
        await AnalyzeAsync();
    }

    private async void Sample_Click(object sender, RoutedEventArgs e)
    {
        SetData(ExifAnalyzer.Sample(), ExifAnalyzer.SampleName, null);
        TargetBox.Text = ExifAnalyzer.SampleName;
        await AnalyzeAsync();
    }

    /* ------------------------------------------------------------ Auswerten */

    private async Task AnalyzeAsync()
    {
        if (_data == null && _path == null) return;
        _sink = new PanelCheckSink(ResultsPanel);
        CheckButton.IsEnabled = false;
        StripButton.IsEnabled = false;
        MapButton.IsVisible = false;
        StatusText.Text = "";
        ShowPreview(null, 1);
        try
        {
            var data = _data;
            var path = _path;
            Func<Stream> open = data != null ? () => new MemoryStream(data, false) : () => File.OpenRead(path);
            long length = data?.LongLength ?? new FileInfo(path).Length;
            DateTime? modified = path != null ? File.GetLastWriteTimeUtc(path) : null;
            using var cts = new CancellationTokenSource(60000);
            _result = await ExifAnalyzer.AnalyzeAsync(open, _name, length, _sink, GeoBox.IsChecked == true, cts.Token, modified);
            StripButton.IsEnabled = _result.CanStrip;
            MapButton.IsVisible = _result.MapUrl != null;
            if (data != null) ShowPreview(data, _result.Orientation);
        }
        catch (Exception ex)
        {
            _sink.Error(ex is InvalidOperationException ? ex.Message : L.T("Fehler: ") + NetUtil.ErrorText(ex));
        }
        finally
        {
            _sink.Finish();
            CheckButton.IsEnabled = true;
        }
    }

    private void ShowPreview(byte[] data, int orientation)
    {
        PreviewCard.IsVisible = false;
        Preview.Source = null;
        if (data == null) return;
        try
        {
            using var ms = new MemoryStream(data, false);
            Preview.Source = Bitmap.DecodeToWidth(ms, 840);
            PreviewRotate.LayoutTransform = new RotateTransform(orientation switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 });
            PreviewCard.IsVisible = true;
        }
        catch { /* HEIC, RAW, Video: keine Vorschau */ }
    }

    /* ------------------------------------------------------------ Aktionen */

    private async void Strip_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.CanStrip != true) return;
        try
        {
            var data = _data ?? await File.ReadAllBytesAsync(_path);
            var clean = ExifAnalyzer.Strip(data);
            var ext = Path.GetExtension(_name);
            if (string.IsNullOrEmpty(ext)) ext = data[0] == 0xFF ? ".jpg" : ".png";
            var name = Path.GetFileNameWithoutExtension(_name) + L.T("-ohne-Metadaten") + ext;
            string saved;
            if (OperatingSystem.IsIOS())
            {
                // Teilen-Dialog: „Bild sichern“ (Fotos), Dateien, AirDrop …
                saved = Path.Combine(Path.GetTempPath(), name);
                await File.WriteAllBytesAsync(saved, clean);
                NetUtil.OpenUrl(saved);
            }
            else
            {
                var file = await PickSaveFileAsync(name, L.T("Bild"), ext.TrimStart('.'));
                if (file == null) return;
                await using (var s = await file.OpenWriteAsync()) await s.WriteAsync(clean);
                saved = file.TryGetLocalPath() ?? file.Name;
            }
            StatusText.Text = L.F("Gespeichert: {0} ({1} → {2}, Metadaten entfernt; Farbprofil und Ausrichtung bleiben erhalten)",
                saved, NetUtil.FormatBytes(data.Length), NetUtil.FormatBytes(clean.Length));
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + (ex is InvalidOperationException ? ex.Message : NetUtil.ErrorText(ex));
        }
    }

    private void Map_Click(object sender, RoutedEventArgs e)
    {
        if (_result?.MapUrl is { } url) NetUtil.OpenUrl(url);
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
