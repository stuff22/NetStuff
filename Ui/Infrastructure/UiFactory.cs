using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Ergebnis-Karten für Prüfungen aus dem Kern (Gegenstück zu UiFactory der WPF-App).</summary>
public static class UiFactory
{
    public static string Glyph(Level l) => l switch
    {
        Level.Ok => "✓",
        Level.Warn => "!",
        Level.Bad => "✕",
        _ => "•",
    };

    public static Control Section(CheckSection s)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = s.Title, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        if (!string.IsNullOrEmpty(s.Subtitle))
            panel.Children.Add(new TextBlock { Text = s.Subtitle, Classes = { "Status" }, Margin = new Thickness(0, -4, 0, 8) });
        foreach (var i in s.Items) panel.Children.Add(Item(i));
        return new Border { Classes = { "Card" }, Child = panel };
    }

    public static Control Item(CheckItem i)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("24,190,*"), Margin = new Thickness(0, 3) };
        g.Children.Add(new TextBlock { Text = Glyph(i.Level), Foreground = Palette.For(i.Level), FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Left });
        var name = new SelectableTextBlock { Text = i.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(name, 1);
        g.Children.Add(name);
        var value = new SelectableTextBlock { Text = i.Value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(value, 2);
        if (i.Name == "") { Grid.SetColumn(value, 1); Grid.SetColumnSpan(value, 2); }
        g.Children.Add(value);
        return g;
    }

    public static Control Message(string text, Level level = Level.Info) => Section(CheckSection.Message(text, level));
}

/// <summary>Zeigt schrittweise gelieferte Prüfergebnisse (<see cref="ICheckSink"/>) als Karten an.</summary>
public sealed class PanelCheckSink : ICheckSink
{
    private readonly Panel _panel;
    private Control _status;

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

    private static void Run(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
