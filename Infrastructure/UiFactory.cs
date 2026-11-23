using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace NetStuff.Infrastructure;

/// <summary>Erzeugt einheitliche Ergebnis-Karten aus Prüfergebnissen.</summary>
public static class UiFactory
{
    public static Border Section(string title, IEnumerable<CheckItem> items, string subtitle = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T(title), Style = (Style)Application.Current.FindResource("SectionTitle") });
        if (!string.IsNullOrEmpty(subtitle))
            panel.Children.Add(new TextBlock { Text = L.T(subtitle), Style = (Style)Application.Current.FindResource("Muted"), Margin = new Thickness(0, -4, 0, 8) });
        panel.Children.Add(new ItemsControl
        {
            ItemsSource = items.ToList(),
            ItemTemplate = (DataTemplate)Application.Current.FindResource("CheckItemTemplate"),
            Focusable = false,
        });
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Child = panel };
    }

    public static Border Section(CheckSection s) => Section(s.Title, s.Items, s.Subtitle);

    public static Border Message(string text, Level level = Level.Info)
    {
        return Section(level == Level.Bad ? "Fehler" : "Hinweis", new[] { new CheckItem("", text, level) });
    }

    /// <summary>Text aller Ergebnisse für die Zwischenablage.</summary>
    public static string ToText(string title, IEnumerable<CheckItem> items) =>
        L.T(title) + "\n" + string.Join("\n", items.Select(i => $"  [{i.Level}] {i.Name}: {i.Value}")) + "\n";
}

/// <summary>
/// Zeigt Prüfergebnisse aus dem Kern (<see cref="ICheckSink"/>) als Karten in einem Panel an.
/// Statusmeldungen erscheinen als Hinweis-Karte am Ende und verschwinden mit dem nächsten Abschnitt bzw. <see cref="Finish"/>.
/// </summary>
public sealed class PanelCheckSink : ICheckSink
{
    private readonly Panel _panel;
    private Border _status;

    public PanelCheckSink(Panel panel)
    {
        _panel = panel;
        _panel.Children.Clear();
    }

    public StringBuilder Text { get; } = new();
    public List<CheckSection> Sections { get; } = new();

    public void Add(CheckSection section) => Run(() =>
    {
        RemoveStatus();
        Sections.Add(section);
        Text.Append(section.ToText());
        _panel.Children.Add(UiFactory.Section(section));
    });

    /// <summary>Abschnitt ganz oben einfügen (z. B. Zusammenfassung).</summary>
    public void Insert(CheckSection section) => Run(() =>
    {
        Sections.Insert(0, section);
        Text.Insert(0, section.ToText());
        _panel.Children.Insert(0, UiFactory.Section(section));
    });

    public void Status(string text) => Run(() =>
    {
        RemoveStatus();
        _status = UiFactory.Message(text);
        _panel.Children.Add(_status);
    });

    public void Error(string text) => Run(() =>
    {
        RemoveStatus();
        _panel.Children.Add(UiFactory.Message(text, Level.Bad));
    });

    public void Finish() => Run(RemoveStatus);

    private void RemoveStatus()
    {
        if (_status != null) _panel.Children.Remove(_status);
        _status = null;
    }

    private void Run(Action a)
    {
        if (_panel.Dispatcher.CheckAccess()) a();
        else _panel.Dispatcher.Invoke(a);
    }
}
