using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Controls;

/// <summary>Einfaches Liniendiagramm für Zeitreihen (null = Paketverlust) – Gegenstück zum WPF-LineChart.</summary>
public class LineChart : Control
{
    public class Series
    {
        public string Name { get; set; }
        public Color Color { get; set; }
        public List<double?> Values { get; } = new();
    }

    public List<Series> SeriesList { get; } = new();
    public int MaxPoints { get; set; } = 120;
    public string Unit { get; set; } = "ms";
    public bool ShowLoss { get; set; } = true;

    public LineChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    private IBrush Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (w < 80 || h < 60) return;

        const double left = 56, right = 12, top = 28, bottom = 22;
        double pw = w - left - right, ph = h - top - bottom;

        double max = 0;
        foreach (var s in SeriesList)
            foreach (var v in s.Values)
                if (v.HasValue && v.Value > max) max = v.Value;
        double yMax = NiceMax(Math.Max(max * 1.15, 5));

        var axis = Res("SubtleTextBrush", Colors.Gray);
        var text = Res("TextFillColorPrimaryBrush", Colors.Gray);
        var grid = new Pen(Res("CardStrokeBrush", Color.FromArgb(60, 128, 128, 128)), 1, DashStyle.Dot);
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph - ph * i / 4.0;
            dc.DrawLine(grid, new Point(left, y), new Point(left + pw, y));
            var t = Text($"{yMax * i / 4:0.#} {Unit}", 11, axis);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }

        double step = pw / Math.Max(1, MaxPoints - 1);
        var lossPen = new Pen(new SolidColorBrush(Palette.Bad), 2);

        foreach (var s in SeriesList)
        {
            var pts = s.Values.Skip(Math.Max(0, s.Values.Count - MaxPoints)).ToList();
            double offset = (MaxPoints - pts.Count) * step;
            var pen = new Pen(new SolidColorBrush(s.Color), 1.8, lineJoin: PenLineJoin.Round);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool open = false;
                for (int i = 0; i < pts.Count; i++)
                {
                    double x = left + offset + i * step;
                    if (pts[i] is double v)
                    {
                        var p = new Point(x, top + ph - ph * Math.Min(v, yMax) / yMax);
                        if (!open) { ctx.BeginFigure(p, false); open = true; }
                        else ctx.LineTo(p);
                    }
                    else
                    {
                        if (open) { ctx.EndFigure(false); open = false; }
                        if (ShowLoss) dc.DrawLine(lossPen, new Point(x, top + ph - 6), new Point(x, top + ph));
                    }
                }
                if (open) ctx.EndFigure(false);
            }
            dc.DrawGeometry(null, pen, geo);
        }

        double lx = left;
        foreach (var s in SeriesList)
        {
            dc.DrawRectangle(new SolidColorBrush(s.Color), null, new Rect(lx, 8, 10, 10), 2, 2);
            var t = Text(s.Name, 11.5, text);
            dc.DrawText(t, new Point(lx + 14, 5));
            lx += t.Width + 32;
            if (lx > w - 60) break;
        }
    }

    private static double NiceMax(double v)
    {
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * mag >= v) return m * mag;
        return 10 * mag;
    }
}
