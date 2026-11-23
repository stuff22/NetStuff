namespace NetStuff.Infrastructure;

/// <summary>Ein Ergebnisabschnitt einer Prüfung (Titel, optionaler Untertitel, Prüfzeilen). Titel werden übersetzt.</summary>
public class CheckSection
{
    public CheckSection(string title, IEnumerable<CheckItem> items, string subtitle = null)
    {
        Title = L.T(title ?? "");
        Subtitle = string.IsNullOrEmpty(subtitle) ? null : L.T(subtitle);
        Items = items.ToList();
    }

    public string Title { get; }
    public string Subtitle { get; }
    public List<CheckItem> Items { get; }

    /// <summary>Text für die Zwischenablage.</summary>
    public string ToText() => Title + "\n" + string.Join("\n", Items.Select(i => $"  [{i.Level}] {i.Name}: {i.Value}")) + "\n";

    public static CheckSection Message(string text, Level level = Level.Info) =>
        new(level == Level.Bad ? "Fehler" : "Hinweis", new[] { new CheckItem("", text, level) });
}

/// <summary>
/// Empfänger für schrittweise gelieferte Prüfergebnisse. Die Oberfläche zeigt Abschnitte sofort an
/// und blendet eine Statusmeldung („… wird geprüft“) ein, bis der nächste Abschnitt kommt.
/// </summary>
public interface ICheckSink
{
    void Add(CheckSection section);
    void Status(string text);
}

/// <summary>Sammelt Abschnitte nur (z. B. für Tests oder Berichte ohne Oberfläche).</summary>
public sealed class CheckCollector : ICheckSink
{
    public List<CheckSection> Sections { get; } = new();
    public List<string> StatusLog { get; } = new();
    public void Add(CheckSection section) => Sections.Add(section);
    public void Status(string text) => StatusLog.Add(text);
    public IEnumerable<CheckItem> AllItems => Sections.SelectMany(s => s.Items);
    public string ToText() => string.Concat(Sections.Select(s => s.ToText()));
}
