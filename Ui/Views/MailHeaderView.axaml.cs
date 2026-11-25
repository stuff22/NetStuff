using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

/// <summary>E-Mail-Header-Analyse: Kopfbereich einfügen oder .eml laden, Auswertung in verständlicher Sprache.</summary>
public partial class MailHeaderView : ToolView
{
    private PanelCheckSink _sink;

    public MailHeaderView()
    {
        InitializeComponent();
        HelpText.Text = L.T(MailHeaderAnalyzer.HowTo);
    }

    public override void OnNavigatedTo(string parameter)
    {
        if (!string.IsNullOrEmpty(parameter)) HeaderBox.Text = parameter;
    }

    public override void RunNow() => Check_Click(null, null);

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var raw = HeaderBox.Text ?? "";
        if (raw.Trim() == "" || !CheckButton.IsEnabled) return;
        _sink = new PanelCheckSink(ResultsPanel);
        CheckButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(30000);
            await MailHeaderAnalyzer.AnalyzeAsync(raw, _sink, GeoBox.IsChecked == true, cts.Token);
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

    private async void Paste_Click(object sender, RoutedEventArgs e)
    {
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip == null) return;
        var text = await clip.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;
        HeaderBox.Text = text;
        Check_Click(sender, e);
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
                new FilePickerFileType(L.T("E-Mail (*.eml, *.txt)")) { Patterns = new[] { "*.eml", "*.txt", "*.msg.txt" }, AppleUniformTypeIdentifiers = new[] { "com.apple.mail.email", "public.plain-text" } },
                new FilePickerFileType(L.T("Alle Dateien")) { Patterns = new[] { "*" } },
            },
        });
        if (files.Count == 0) return;
        await using var s = await files[0].OpenReadAsync();
        using var r = new StreamReader(s);
        // nur der Kopfbereich ist nötig – große Anhänge nicht komplett laden
        var buffer = new char[512 * 1024];
        var n = await r.ReadBlockAsync(buffer, 0, buffer.Length);
        HeaderBox.Text = new string(buffer, 0, n);
        Check_Click(sender, e);
    }

    private void Sample_Click(object sender, RoutedEventArgs e)
    {
        HeaderBox.Text = MailHeaderAnalyzer.Sample;
        Check_Click(sender, e);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        HeaderBox.Text = "";
        ResultsPanel.Children.Clear();
        _sink = null;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(_sink?.Text.ToString());
}
