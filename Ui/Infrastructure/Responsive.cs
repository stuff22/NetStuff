using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace NetStuff.Ui.Infrastructure;

/// <summary>
/// Anpassung an schmale Bildschirme (iPhone, iPad im geteilten Bildschirm) – umkehrbar beim Drehen/Vergrößern:
/// <list type="bullet">
/// <item>Grids mit <c>Classes="responsive"</c> (eine Zeile, mehrere Spalten) werden untereinander angeordnet.</item>
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

    public static void Apply(Control root, bool compact)
    {
        if (root == null) return;
        foreach (var g in root.GetLogicalDescendants().OfType<Grid>().Where(g => g.Classes.Contains("responsive")).ToList())
            if (compact) Stack(g); else Restore(g);
        foreach (var dg in root.GetLogicalDescendants().OfType<DataGrid>().ToList())
            ApplyDataGrid(dg, compact);
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
