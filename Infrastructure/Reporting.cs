using System.Collections;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NetStuff.Controls;

namespace NetStuff.Infrastructure;

/// <summary>Erstellt aus der aktuell sichtbaren Werkzeugseite einen Berichtseintrag (Tabellen, Prüfergebnisse, Diagramme, Karte, Textausgaben).</summary>
public static class ReportCapture
{
    public static async Task<ReportEntry> CaptureAsync(ToolPage page, string toolKey, string toolTitle)
    {
        var entry = new ReportEntry { Tool = toolKey, Title = toolTitle, Target = page.PrimaryTarget };
        string heading = null;
        var sectionTitle = Application.Current.TryFindResource("SectionTitle");
        var pageTitle = Application.Current.TryFindResource("PageTitle");

        async Task Walk(DependencyObject node)
        {
            if (node is UIElement ui && !ui.IsVisible) return;

            switch (node)
            {
                case TextBlock tb when tb.Style == sectionTitle && !string.IsNullOrWhiteSpace(tb.Text):
                    heading = tb.Text;
                    return;
                case TextBlock tb when tb.FontSize >= 26 && tb.Style != pageTitle && !string.IsNullOrWhiteSpace(tb.Text):
                    entry.Blocks.Add(new ReportBlock { Kind = "headline", Text = tb.Text });
                    return;
                case DataGrid dg:
                    var table = Table(dg, heading);
                    if (table != null) entry.Blocks.Add(table);
                    return;
                case ItemsControl ic when ic.ItemsSource is IEnumerable src && src.Cast<object>().FirstOrDefault() is CheckItem:
                    entry.Blocks.Add(new ReportBlock
                    {
                        Kind = "section",
                        Title = heading,
                        Items = src.Cast<CheckItem>().Select(c => new ReportItem { Name = c.Name, Value = c.Value, Level = c.Level }).ToList(),
                    });
                    heading = null;
                    return;
                case LineChart or SpectrumChart:
                    var img = Render((FrameworkElement)node);
                    if (img != null) entry.Blocks.Add(new ReportBlock { Kind = "image", Title = heading, ImageBase64 = img });
                    return;
                case WebView2 wv when wv.CoreWebView2 != null:
                    try
                    {
                        using var ms = new MemoryStream();
                        await wv.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                        entry.Blocks.Add(new ReportBlock { Kind = "image", Title = L.T("Karte"), ImageBase64 = Convert.ToBase64String(ms.ToArray()) });
                    }
                    catch { }
                    return;
                case TextBox box when box.IsReadOnly && box.AcceptsReturn && !string.IsNullOrWhiteSpace(box.Text):
                    var t = box.Text.Length > 30000 ? box.Text[..30000] + "\n…" : box.Text;
                    entry.Blocks.Add(new ReportBlock { Kind = "text", Title = heading, Text = t });
                    return;
            }

            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++) await Walk(VisualTreeHelper.GetChild(node, i));
        }

        await Walk(page);
        return entry;
    }

    private static ReportBlock Table(DataGrid dg, string heading)
    {
        if (dg.Items.Count == 0) return null;
        var cols = new List<(string Header, string Path)>();
        foreach (var c in dg.Columns.Where(c => c.Visibility == Visibility.Visible))
        {
            string path = Linkify.GetPath(c) ?? c.SortMemberPath;
            if (c is DataGridBoundColumn bc && bc.Binding is Binding b) path = b.Path?.Path;
            var header = c.Header?.ToString();
            if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(path)) continue;
            cols.Add((L.T(header), path));
        }
        if (cols.Count == 0) return null;

        var rows = new List<List<string>>();
        foreach (var item in dg.Items.Cast<object>().Take(1000))
        {
            rows.Add(cols.Select(c => Value(item, c.Path)).ToList());
        }
        return new ReportBlock { Kind = "table", Title = heading, Headers = cols.Select(c => c.Header).ToList(), Rows = rows };
    }

    private static string Value(object item, string path)
    {
        object cur = item;
        foreach (var part in path.Split('.'))
        {
            if (cur == null) return "";
            var prop = cur.GetType().GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
            cur = prop?.GetValue(cur);
        }
        return cur switch
        {
            null => "",
            bool b => b ? "✓" : "",
            _ => L.T(cur.ToString()),
        };
    }

    private static string Render(FrameworkElement el)
    {
        try
        {
            int w = (int)el.ActualWidth, h = (int)el.ActualHeight;
            if (w < 10 || h < 10) return null;
            const double scale = 1.5;
            var bg = Palette.Resource("ApplicationBackgroundBrush", MainWindow.IsDark ? Color.FromRgb(32, 32, 32) : Colors.White);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }
}

