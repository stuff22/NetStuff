using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NetStuff.Ui.Views;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Basisklasse aller Werkzeug-Ansichten (entspricht ToolPage der WPF-App).</summary>
public class ToolView : UserControl
{
    /// <summary>Parameter beim Öffnen (z. B. Hostname aus einem anderen Werkzeug oder --param).</summary>
    public virtual void OnNavigatedTo(string parameter) { }

    /// <summary>Werkzeug sofort starten (--run).</summary>
    public virtual void RunNow() { }

    /// <summary>Anderes Werkzeug öffnen (z. B. Port-Scan für ein Gerät aus dem Netzwerkscan).</summary>
    protected void Navigate(string key, string parameter = null, bool run = false) =>
        this.FindAncestorOfType<MainView>()?.Open(key, parameter, run);

    protected async void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(text);
    }

    /// <summary>Speichern-Dialog; schreibt den Text (UTF-8 mit BOM, damit Excel Umlaute erkennt). false = abgebrochen.</summary>
    protected async Task<bool> SaveTextAsync(string suggestedName, string typeName, string extension, string text)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanSave) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } } },
        });
        if (file == null) return false;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s, new System.Text.UTF8Encoding(true));
        await w.WriteAsync(text);
        return true;
    }
}
