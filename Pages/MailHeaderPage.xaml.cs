using System.IO;
using System.Windows;
using Microsoft.Win32;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

/// <summary>E-Mail-Header-Analyse: Kopfbereich einfügen oder .eml laden, Auswertung in verständlicher Sprache.</summary>
public partial class MailHeaderPage : ToolPage
{
    private PanelCheckSink _sink;

    public MailHeaderPage()
    {
        InitializeComponent();
        HelpText.Text = L.T(MailHeaderAnalyzer.HowTo);
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s && s != "") HeaderBox.Text = s;
    }

    // Verlauf/Favoriten: der Betreff ist aussagekräftiger als der ganze Header
    public override string PrimaryTarget
    {
        get
        {
            var subject = MailHeaderAnalyzer.Parse(HeaderBox.Text).FirstOrDefault(h => h.Name.Equals("Subject", StringComparison.OrdinalIgnoreCase));
            return subject != null ? MailHeaderAnalyzer.Decode(subject.Value) : "";
        }
    }

    // Nur echte Header übernehmen (Verlauf/Favoriten speichern lediglich den Betreff)
    public override void SetPrimaryTarget(string value)
    {
        if (value != null && MailHeaderAnalyzer.Parse(value).Count >= 2) HeaderBox.Text = value;
    }

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

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        string text = null;
        try { text = Clipboard.GetText(); } catch { }
        if (string.IsNullOrWhiteSpace(text)) return;
        HeaderBox.Text = text;
        Check_Click(sender, e);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("E-Mail (*.eml, *.txt)") + "|*.eml;*.txt|" + L.T("Alle Dateien") + "|*.*" };
        if (dlg.ShowDialog() != true) return;
        using var r = new StreamReader(dlg.FileName);
        // nur der Kopfbereich ist nötig – große Anhänge nicht komplett laden
        var buffer = new char[512 * 1024];
        var n = r.ReadBlock(buffer, 0, buffer.Length);
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
