using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

/// <summary>Exif-Checker: alle Metadaten eines Fotos/Videos verständlich erklärt, Kopie ohne Metadaten speichern.</summary>
public partial class ExifPage : ToolPage
{
    private const long MaxInMemory = 300L * 1024 * 1024;
    private PanelCheckSink _sink;
    private byte[] _data;
    private string _path, _name;
    private ExifResult _result;

    private const string Filter =
        "Fotos und Videos|*.jpg;*.jpeg;*.heic;*.heif;*.png;*.tif;*.tiff;*.webp;*.gif;*.bmp;*.avif;*.dng;*.cr2;*.cr3;*.nef;*.nrw;*.arw;*.orf;*.rw2;*.raf;*.pef;*.srw;*.psd;*.mp4;*.mov;*.m4v;*.3gp;*.avi|Alle Dateien|*.*";

    public ExifPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s && s != "") TargetBox.Text = s;
    }

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Check_Click(sender, e);
    }

    /* ------------------------------------------------------------ Laden */

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var text = (TargetBox.Text ?? "").Trim().Trim('"');
        if (text == "" || !CheckButton.IsEnabled) return;
        if (text == _name && _data != null && _path == null) { await AnalyzeAsync(); return; }
        if (text == ExifAnalyzer.SampleName && !File.Exists(text)) { Sample_Click(sender, e); return; }   // z. B. aus dem Verlauf
        try
        {
            if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = L.T("Lade Bild herunter …");
                using var cts = new CancellationTokenSource(60000);
                var bytes = await NetUtil.Http.GetByteArrayAsync(text, cts.Token);
                var n = Path.GetFileName(new Uri(text).AbsolutePath);
                SetData(bytes, n.Length > 0 ? n : "download", null);
            }
            else
            {
                if (!File.Exists(text)) { StatusText.Text = L.F("„{0}“ wurde nicht gefunden.", text); return; }
                LoadFile(text);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex);
            return;
        }
        await AnalyzeAsync();
    }

    private void LoadFile(string path)
    {
        var len = new FileInfo(path).Length;
        SetData(len <= MaxInMemory ? File.ReadAllBytes(path) : null, Path.GetFileName(path), path);
        TargetBox.Text = path;
    }

    private void SetData(byte[] data, string name, string path)
    {
        _data = data;
        _name = name;
        _path = path;
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T(Filter.Split('|')[0]) + "|" + Filter.Split('|')[1] + "|" + L.T("Alle Dateien") + "|*.*" };
        if (dlg.ShowDialog() != true) return;
        try { LoadFile(dlg.FileName); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); return; }
        await AnalyzeAsync();
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files || !File.Exists(files[0])) return;
        try { LoadFile(files[0]); }
        catch (Exception ex) { StatusText.Text = L.T("Fehler: ") + NetUtil.ErrorText(ex); return; }
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
        MapButton.Visibility = Visibility.Collapsed;
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
            MapButton.Visibility = _result.MapUrl != null ? Visibility.Visible : Visibility.Collapsed;
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
        PreviewCard.Visibility = Visibility.Collapsed;
        Preview.Source = null;
        if (data == null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(data, false);
            bmp.DecodePixelWidth = 840;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();
            Preview.Source = bmp;
            Preview.LayoutTransform = new RotateTransform(orientation switch { 3 or 4 => 180, 5 or 6 => 90, 7 or 8 => 270, _ => 0 });
            PreviewCard.Visibility = Visibility.Visible;
        }
        catch { /* RAW, Video, HEIC ohne Codec: keine Vorschau */ }
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
            var dlg = new SaveFileDialog
            {
                FileName = Path.GetFileNameWithoutExtension(_name) + L.T("-ohne-Metadaten") + ext,
                Filter = $"{L.T("Bild")} (*{ext})|*{ext}",
            };
            if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
            if (dlg.ShowDialog() != true) return;
            await File.WriteAllBytesAsync(dlg.FileName, clean);
            StatusText.Text = L.F("Gespeichert: {0} ({1} → {2}, Metadaten entfernt; Farbprofil und Ausrichtung bleiben erhalten)",
                dlg.FileName, NetUtil.FormatBytes(data.Length), NetUtil.FormatBytes(clean.Length));
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
