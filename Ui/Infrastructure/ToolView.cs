using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Basisklasse aller Werkzeug-Ansichten (entspricht ToolPage der WPF-App).</summary>
public class ToolView : UserControl
{
    /// <summary>Parameter beim Öffnen (z. B. Hostname aus einem anderen Werkzeug oder --param).</summary>
    public virtual void OnNavigatedTo(string parameter) { }

    /// <summary>Werkzeug sofort starten (--run).</summary>
    public virtual void RunNow() { }

    protected async void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(text);
    }
}
