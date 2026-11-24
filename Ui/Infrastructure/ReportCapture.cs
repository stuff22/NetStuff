using System.Collections;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NetStuff.Ui.Controls;

namespace NetStuff.Ui.Infrastructure;

/// <summary>
/// Erstellt aus der sichtbaren Werkzeugansicht einen Berichtseintrag: Ergebniskarten (CheckSection),
/// Tabellen, Diagramme/Karte als Bild, Textausgaben und große Überschriften (Gegenstück zu ReportCapture der WPF-App).
/// </summary>
public static class ReportCapture
{
    public static ReportEntry Capture(ToolView view, string toolKey, string toolTitle)
    {
        var entry = new ReportEntry { Tool = toolKey, Title = toolTitle, Target = view.PrimaryTarget };

        void Walk(Visual node)
        {
            if (node is Control { IsVisible: false }) return;
            switch (node)
            {
                case Border { Tag: CheckSection s }:
                    entry.Blocks.Add(new ReportBlock
                    {
                        Kind = "section",
                        Title = s.Title,
                        Items = s.Items.Select(i => new ReportItem { Name = i.Name, Value = i.Value, Level = i.Level }).ToList(),
                    });
                    return;
                case TextBlock tb when tb.FontSize >= 26 && !tb.Classes.Contains("PageTitle") && !string.IsNullOrWhiteSpace(tb.Text):
                    entry.Blocks.Add(new ReportBlock { Kind = "headline", Text = tb.Text });
                    return;
                case DataGrid dg:
                    var table = Table(dg);
                    if (table != null) entry.Blocks.Add(table);
                    return;
                case LineChart or SpectrumChart:
                    var img = Render((Control)node);
                    if (img != null) entry.Blocks.Add(new ReportBlock { Kind = "image", ImageBase64 = img });
                    return;
                case TextBox { IsReadOnly: true, AcceptsReturn: true } box when !string.IsNullOrWhiteSpace(box.Text):
                    var t = box.Text.Length > 30000 ? box.Text[..30000] + "\n…" : box.Text;
                    entry.Blocks.Add(new ReportBlock { Kind = "text", Text = t });
                    return;
            }
            foreach (var child in node.GetVisualChildren()) Walk(child);
        }

        Walk(view);
        return entry;
    }

    private static ReportBlock Table(DataGrid dg)
    {
        if (dg.ItemsSource is not IEnumerable src) return null;
        var items = src.Cast<object>().Take(1000).ToList();
        if (items.Count == 0) return null;

        var cols = new List<(string Header, string Path)>();
        foreach (var c in dg.Columns.Where(c => c.IsVisible).OrderBy(c => c.DisplayIndex))
        {
            var header = c.Header?.ToString();
            var path = c is DataGridBoundColumn bc ? PathOf(bc.Binding) : null;
            path ??= c.SortMemberPath;
            if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(path)) continue;
            cols.Add((header, path));
        }
        if (cols.Count == 0) return null;

        var rows = items.Select(item => cols.Select(c => Value(item, c.Path)).ToList()).ToList();
        return new ReportBlock { Kind = "table", Headers = cols.Select(c => c.Header).ToList(), Rows = rows };
    }

    /// <summary>Eigenschaftspfad einer (kompilierten oder klassischen) Bindung.</summary>
    private static string PathOf(object binding)
    {
        var p = binding?.GetType().GetProperty("Path")?.GetValue(binding)?.ToString();
        if (string.IsNullOrEmpty(p)) return null;
        p = p.Trim('{', '}', ' ');
        return new string(p.Where(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_').ToArray()).Trim('.');
    }

    private static string Value(object item, string path)
    {
        object cur = item;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur == null) return "";
            cur = cur.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance)?.GetValue(cur);
        }
        return cur switch
        {
            null => "",
            bool b => b ? "✓" : "",
            _ => L.T(cur.ToString()),
        };
    }

    /// <summary>Steuerelement als PNG (Base64), 1,5-fach für scharfe Ausdrucke.</summary>
    public static string Render(Control el)
    {
        try
        {
            int w = (int)el.Bounds.Width, h = (int)el.Bounds.Height;
            if (w < 10 || h < 10) return null;
            const double scale = 1.5;
            using var bmp = new RenderTargetBitmap(new PixelSize((int)(w * scale), (int)(h * scale)), new Vector(96 * scale, 96 * scale));
            bmp.Render(el);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}
