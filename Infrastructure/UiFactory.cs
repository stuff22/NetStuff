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

    public static Border Message(string text, Level level = Level.Info)
    {
        return Section(level == Level.Bad ? "Fehler" : "Hinweis", new[] { new CheckItem("", text, level) });
    }

    /// <summary>Text aller Ergebnisse für die Zwischenablage.</summary>
    public static string ToText(string title, IEnumerable<CheckItem> items) =>
        L.T(title) + "\n" + string.Join("\n", items.Select(i => $"  [{i.Level}] {i.Name}: {i.Value}")) + "\n";
}
