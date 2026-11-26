using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace NetStuff.Ui.Infrastructure;

/// <summary>
/// Anpassung an schmale Bildschirme (iPhone, iPad im geteilten Bildschirm) – umkehrbar beim Drehen/Vergrößern:
/// <list type="bullet">
/// <item>Grids mit <c>Classes="responsive"</c> (eine Zeile, mehrere Spalten) werden untereinander angeordnet.</item>
/// <item>Ebenso einzeilige Grids mit Karten nebeneinander, Text links und Knöpfen rechts, oder Fortschrittsbalken + Statuszeile.</item>
/// <item>Eingabefelder mit fester Breite werden auf die Bildschirmbreite gekürzt.</item>
/// <item>Tabellen: „Rest“-Spalten (Width="*") bekommen eine feste Breite, damit die Tabelle waagrecht scrollt statt zu stauchen.</item>
/// </list>
/// </summary>
public static class Responsive
{
    private sealed class SavedGrid
    {
        public List<GridLength> Columns;
        public List<GridLength> Rows;
        public List<(Control Child, int Column, int Row, int ColumnSpan, Thickness Margin)> Children;
    }

    private static readonly ConditionalWeakTable<Grid, SavedGrid> Grids = new();
    private static readonly ConditionalWeakTable<DataGridColumn, object> Columns = new();

    private static readonly ConditionalWeakTable<Control, object> Widths = new();

    public static void Apply(Control root, bool compact, double width = 0)
    {
        if (root == null) return;
        foreach (var g in root.GetLogicalDescendants().OfType<Grid>().Where(ShouldStack).ToList())
            if (compact) Stack(g); else Restore(g);
        foreach (var dg in root.GetLogicalDescendants().OfType<DataGrid>().ToList())
            ApplyDataGrid(dg, compact);
        // Feldbreite höchstens Bildschirm minus Ränder (Seite 2×12, Karte 2×16, etwas Luft)
        double max = Math.Max(160, width - 80);
        foreach (var c in root.GetLogicalDescendants().OfType<Control>().Where(c => c is TextBox or ComboBox or NumericUpDown).ToList())
        {
            if (compact)
            {
                if (Widths.TryGetValue(c, out var orig)) { c.Width = Math.Min((double)orig, max); continue; }
                if (double.IsNaN(c.Width) || c.Width <= max) continue;
                Widths.Add(c, c.Width);
                c.Width = max;
            }
            else if (Widths.TryGetValue(c, out var w))
            {
                Widths.Remove(c);
                c.Width = (double)w;
            }
        }
    }

    /// <summary>Schmale Ansicht: Braucht die Ansicht eine feste Höhe (Tabelle, Protokollfeld, Diagramm) oder darf sie so hoch wie ihr Inhalt werden?</summary>
    public static bool NeedsFixedHeight(Control root)
    {
        var all = root.GetLogicalDescendants().OfType<Control>().ToList();
        bool hasTable = all.Any(c => c is DataGrid or ListBox || c is TextBox { AcceptsReturn: true });
        bool hasScroll = all.OfType<ScrollViewer>().Any();
        return hasTable || !hasScroll;
    }

    /// <summary>
    /// Einzeilige Grids mit mehreren Spalten, die schmal untereinander gehören: ausdrücklich markierte,
    /// Karten nebeneinander, oder Text links + Knopfleiste rechts (sonst wird der Text Buchstabe für Buchstabe umbrochen).
    /// </summary>
    private static bool ShouldStack(Grid g)
    {
        if (g.Classes.Contains("responsive")) return true;
        if (Grids.TryGetValue(g, out _)) return true;
        if (g.RowDefinitions.Count > 1 || g.ColumnDefinitions.Count < 2) return false;
        if (g.GetLogicalAncestors().Any(a => a is DataGrid or ItemsControl)) return false;
        var kids = g.Children.OfType<Control>().ToList();
        if (kids.Count(k => k is Border b && b.Classes.Contains("Card")) >= 1 && kids.Count >= 2) return true;
        if (kids.Any(k => k is ProgressBar) && kids.Any(k => k is TextBlock)) return true;   // Fortschritt + Statuszeile
        bool wrapText = kids.Any(k => k is TextBlock { TextWrapping: not Avalonia.Media.TextWrapping.NoWrap });
        bool buttonBar = kids.Any(k => k is StackPanel { Orientation: Avalonia.Layout.Orientation.Horizontal } sp && sp.Children.OfType<Button>().Count() >= 2);
        return wrapText && buttonBar;
    }

    private static void Stack(Grid g)
    {
        if (Grids.TryGetValue(g, out _) || g.ColumnDefinitions.Count < 2 || g.RowDefinitions.Count > 1) return;
        var saved = new SavedGrid
        {
            Columns = g.ColumnDefinitions.Select(c => c.Width).ToList(),
            Rows = g.RowDefinitions.Select(r => r.Height).ToList(),
            Children = g.Children.Select(c => (c, Grid.GetColumn(c), Grid.GetRow(c), Grid.GetColumnSpan(c), c.Margin)).ToList(),
        };
        Grids.Add(g, saved);

        // Spalte → Zeile: leere schmale Spalten bleiben Abstände, Auto bleibt Auto, sonst anteilig
        var rows = new RowDefinitions();
        for (int i = 0; i < saved.Columns.Count; i++)
        {
            var w = saved.Columns[i];
            bool empty = !saved.Children.Any(c => c.Column == i);
            rows.Add(new RowDefinition(empty && w.IsAbsolute && w.Value <= 24 ? new GridLength(w.Value)
                : w.IsAuto ? GridLength.Auto
                : w.IsStar ? new GridLength(w.Value, GridUnitType.Star)
                : new GridLength(2, GridUnitType.Star)));
        }
        g.ColumnDefinitions = new ColumnDefinitions("*");
        g.RowDefinitions = rows;
        foreach (var (child, col, _, _, margin) in saved.Children)
        {
            Grid.SetColumn(child, 0);
            Grid.SetColumnSpan(child, 1);
            Grid.SetRow(child, Math.Min(col, rows.Count - 1));
            if (child is TextBlock tb) tb.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            // Abstand nach links (Spaltenabstand) wird zum Abstand nach oben
            if (margin.Left > 0 && margin.Left <= 24 && margin.Top == 0)
                child.Margin = new Thickness(0, margin.Left, margin.Right, margin.Bottom);
        }
    }

    private static void Restore(Grid g)
    {
        if (!Grids.TryGetValue(g, out var saved)) return;
        Grids.Remove(g);
        var cols = new ColumnDefinitions();
        foreach (var w in saved.Columns) cols.Add(new ColumnDefinition(w));
        var rows = new RowDefinitions();
        foreach (var h in saved.Rows) rows.Add(new RowDefinition(h));
        g.ColumnDefinitions = cols;
        g.RowDefinitions = rows;
        foreach (var (child, col, row, span, margin) in saved.Children)
        {
            Grid.SetColumn(child, col);
            Grid.SetRow(child, row);
            Grid.SetColumnSpan(child, span);
            child.Margin = margin;
        }
    }

    private static void ApplyDataGrid(DataGrid dg, bool compact)
    {
        foreach (var c in dg.Columns)
        {
            if (compact)
            {
                if (!c.Width.IsStar || Columns.TryGetValue(c, out _)) continue;
                Columns.Add(c, c.Width);
                c.Width = new DataGridLength(Math.Max(c.MinWidth, 240));
            }
            else if (Columns.TryGetValue(c, out var w))
            {
                Columns.Remove(c);
                c.Width = (DataGridLength)w;
            }
        }
    }
}
