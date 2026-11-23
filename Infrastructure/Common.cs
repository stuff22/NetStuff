using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace NetStuff.Infrastructure;

/// <summary>Basisklasse für alle Werkzeug-Seiten. Jede Seite lebt in einem eigenen Tab (laufende Tests laufen weiter).</summary>
public class ToolPage : UserControl
{
    /// <summary>Namen der Haupt-Eingabefelder (in Reihenfolge der Priorität).</summary>
    public static readonly string[] InputNames = { "TargetBox", "TargetsBox", "HostBox", "NameBox", "QueryBox", "DomainBox", "UrlBox", "RangeBox", "InputBox", "ServerBox", "RoomBox" };

    /// <summary>Namen der Start-Buttons.</summary>
    public static readonly string[] RunButtonNames = { "StartButton", "CheckButton", "QueryButton", "WhoisButton", "RunButton" };

    public virtual void OnNavigatedTo(object parameter) { }

    /// <summary>Aktuelles Ziel (Haupteingabe) – für Verlauf, Favoriten und Berichte.</summary>
    public virtual string PrimaryTarget
    {
        get
        {
            foreach (var n in InputNames)
                if (FindName(n) is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Trim();
            return "";
        }
    }

    public virtual void SetPrimaryTarget(string value)
    {
        foreach (var n in InputNames)
            if (FindName(n) is TextBox tb) { tb.Text = value; return; }
    }

    /// <summary>Startet das Werkzeug (klickt den Start-Button).</summary>
    public virtual void Run()
    {
        foreach (var n in RunButtonNames)
        {
            if (FindName(n) is System.Windows.Controls.Primitives.ButtonBase b && b.IsEnabled)
            {
                b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                return;
            }
        }
    }

    /// <summary>Beim Schließen des Tabs laufende Vorgänge stoppen.</summary>
    public virtual void OnClosing()
    {
        if (FindName("StopButton") is System.Windows.Controls.Primitives.ButtonBase b && b.IsEnabled)
            b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    protected static MainWindow Main => (MainWindow)Application.Current.MainWindow;

    protected static void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }
}

// Bindable, Level und CheckItem liegen in NetStuff.Core (Infrastructure/Common.cs).

public static class Palette
{
    public static readonly Color Ok = Color.FromRgb(0x3F, 0xB9, 0x50);
    public static readonly Color Warn = Color.FromRgb(0xD2, 0x99, 0x22);
    public static readonly Color Bad = Color.FromRgb(0xF8, 0x51, 0x49);

    public static readonly Color[] Series =
    {
        Color.FromRgb(0x4C, 0xA3, 0xFF), Color.FromRgb(0x3F, 0xB9, 0x50), Color.FromRgb(0xF0, 0x88, 0x3E),
        Color.FromRgb(0xC2, 0x7A, 0xF5), Color.FromRgb(0xF8, 0x51, 0x49), Color.FromRgb(0x2D, 0xD4, 0xBF),
        Color.FromRgb(0xE3, 0xB3, 0x41), Color.FromRgb(0xFF, 0x7B, 0xAC), Color.FromRgb(0x8B, 0xA4, 0xFF),
        Color.FromRgb(0x9B, 0xD0, 0x5F), Color.FromRgb(0xFF, 0xA6, 0x57), Color.FromRgb(0x56, 0xC2, 0xE6),
    };

    public static Brush Resource(string key, Color fallback)
    {
        return Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    }
}

public class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Level l
            ? l switch
            {
                Level.Ok => new SolidColorBrush(Palette.Ok),
                Level.Warn => new SolidColorBrush(Palette.Warn),
                Level.Bad => new SolidColorBrush(Palette.Bad),
                _ => Palette.Resource("TextFillColorSecondaryBrush", Colors.Gray),
            }
            : Binding.DoNothing;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public class LevelToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Level l
            ? l switch
            {
                Level.Ok => "",   // Häkchen
                Level.Warn => "", // Warnung
                Level.Bad => "",  // X
                _ => "",          // Info
            }
            : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
