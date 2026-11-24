using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using NetStuff.Ui.Views;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Basisklasse aller Werkzeug-Ansichten (entspricht ToolPage der WPF-App).</summary>
public class ToolView : UserControl
{
    /// <summary>Namen der Haupteingabefelder (wie in der WPF-App) – das erste gefüllte ist das Ziel.</summary>
    public static readonly string[] InputNames = { "TargetBox", "TargetsBox", "HostBox", "NameBox", "QueryBox", "DomainBox", "UrlBox", "RangeBox", "InputBox", "ServerBox", "RoomBox" };

    /// <summary>Namen der Start-Buttons (ein Klick darauf wird im Verlauf vermerkt).</summary>
    public static readonly string[] RunButtonNames = { "StartButton", "CheckButton", "QueryButton", "WhoisButton", "RunButton" };

    /// <summary>Parameter beim Öffnen (z. B. Hostname aus einem anderen Werkzeug oder --param).</summary>
    public virtual void OnNavigatedTo(string parameter) { }

    /// <summary>Werkzeug sofort starten (--run).</summary>
    public virtual void RunNow() { }

    /// <summary>Wird bei jedem Anzeigen aufgerufen (z. B. Listen aktualisieren).</summary>
    public virtual void OnShown() { }

    /// <summary>false bei Seiten ohne Messergebnisse (Projekte, Verlauf, Einstellungen) – kein Verlauf, kein Bericht.</summary>
    public virtual bool IsTool => true;

    /// <summary>Aktuelles Ziel (Haupteingabe) – für Verlauf, Favoriten und Berichte.</summary>
    public virtual string PrimaryTarget
    {
        get
        {
            var scope = NameScope.GetNameScope(this);
            if (scope == null) return "";
            foreach (var n in InputNames)
            {
                var text = scope.Find(n) switch
                {
                    TextBox tb => tb.Text,
                    AutoCompleteBox ac => ac.Text,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
            return "";
        }
    }

    /// <summary>Anderes Werkzeug öffnen (z. B. Port-Scan für ein Gerät aus dem Netzwerkscan).</summary>
    protected void Navigate(string key, string parameter = null, bool run = false) =>
        this.FindAncestorOfType<MainView>()?.Open(key, parameter, run);

    protected async void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(text);
    }

    /// <summary>Speichern-Dialog; schreibt den Text (UTF-8 mit BOM, damit Excel Umlaute erkennt). Rückgabe: Pfad oder null.</summary>
    protected async Task<string> SaveTextAsync(string suggestedName, string typeName, string extension, string text, bool bom = true)
    {
        var file = await PickSaveFileAsync(suggestedName, typeName, extension);
        if (file == null) return null;
        await using (var s = await file.OpenWriteAsync())
        await using (var w = new StreamWriter(s, new System.Text.UTF8Encoding(bom)))
            await w.WriteAsync(text);
        return file.TryGetLocalPath() ?? file.Name;
    }

    protected async Task<IStorageFile> PickSaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanSave) return null;
        return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            FileTypeChoices = new[] { new FilePickerFileType(typeName) { Patterns = new[] { "*." + extension } } },
        });
    }

    protected async Task<string> PickOpenFileAsync(string typeName, params string[] patterns)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanOpen) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(typeName) { Patterns = patterns } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    protected async Task<string> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null || !storage.CanPickFolder) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
