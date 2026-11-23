using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Controls;

internal static class ChartText
{
    public static FormattedText Make(Visual v, string text, double size, Brush brush, bool bold = false)
    {
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);
    }

    public static Brush Axis => Palette.Resource("TextFillColorSecondaryBrush", Colors.Gray);
    public static Brush Text => Palette.Resource("TextFillColorPrimaryBrush", Colors.White);

    public static Pen Grid
    {
        get
        {
            var b = Palette.Resource("ControlStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128));
            var p = new Pen(b, 1) { DashStyle = DashStyles.Dot };
            p.Freeze();
            return p;
        }
    }
}

/// <summary>Einfaches Liniendiagramm für Zeitreihen (null = Paketverlust).</summary>
public class LineChart : FrameworkElement
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
    /// <summary>Lücken (null) als Paketverlust markieren.</summary>
    public bool ShowLoss { get; set; } = true;

    public LineChart()
    {
        MainWindow.ThemeChanged += (_, _) => InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 80 || h < 60) return;

        const double left = 52, right = 12, top = 28, bottom = 22;
        double pw = w - left - right, ph = h - top - bottom;

        double max = 0;
        foreach (var s in SeriesList)
            foreach (var v in s.Values)
                if (v.HasValue && v.Value > max) max = v.Value;
        double yMax = NiceMax(Math.Max(max * 1.15, 5));

        var axis = ChartText.Axis;
        var grid = ChartText.Grid;
        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph - ph * i / 4.0;
            dc.DrawLine(grid, new Point(left, y), new Point(left + pw, y));
            var t = ChartText.Make(this, $"{yMax * i / 4:0.#} {Unit}", 11, axis);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }

        double step = pw / Math.Max(1, MaxPoints - 1);
        var lossPen = new Pen(new SolidColorBrush(Palette.Bad), 2);

        foreach (var s in SeriesList)
        {
            var pts = s.Values.Skip(Math.Max(0, s.Values.Count - MaxPoints)).ToList();
            double offset = (MaxPoints - pts.Count) * step;
            var pen = new Pen(new SolidColorBrush(s.Color), 1.8) { LineJoin = PenLineJoin.Round };
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
                        if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                        else ctx.LineTo(p, true, true);
                    }
                    else
                    {
                        open = false;
                        if (ShowLoss) dc.DrawLine(lossPen, new Point(x, top + ph - 6), new Point(x, top + ph));
                    }
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        // Legende
        double lx = left;
        foreach (var s in SeriesList)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(s.Color), null, new Rect(lx, 8, 10, 10), 2, 2);
            var t = ChartText.Make(this, s.Name, 11.5, ChartText.Text);
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

/// <summary>WLAN-Spektrum: belegte Kanalbreite und Signalstärke je Netz.</summary>
public class SpectrumChart : FrameworkElement
{
    public IList<WlanBss> Networks { get; set; } = new List<WlanBss>();
    public string Band { get; set; } = "2,4 GHz";
    public string HighlightBssid { get; set; }

    public SpectrumChart()
    {
        MainWindow.ThemeChanged += (_, _) => InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    public static Color ColorFor(string ssid)
    {
        int hash = 17;
        foreach (var c in ssid ?? "") hash = hash * 31 + c;
        return Palette.Series[Math.Abs(hash % Palette.Series.Length)];
    }

    private (double Min, double Max, IEnumerable<(int Ch, double Mhz, bool Label)> Ticks) Range()
    {
        switch (Band)
        {
            case "5 GHz":
                return (5150, 5895, Enumerable.Range(0, 36).Select(i => 36 + i * 4)
                    .Where(c => c <= 64 || (c >= 100 && c <= 144) || c >= 149 && c <= 177)
                    .Select(c => (c, 5000 + c * 5.0, true)));
            case "6 GHz":
                return (5925, 7125, Enumerable.Range(0, 59).Select(i => 1 + i * 4)
                    .Select(c => (c, 5950 + c * 5.0, (c - 1) % 16 == 0)));
            default:
                return (2400, 2495, Enumerable.Range(1, 14)
                    .Select(c => (c, c == 14 ? 2484.0 : 2407 + c * 5.0, true)));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 100 || h < 80) return;

        const double left = 52, right = 14, top = 14, bottom = 30;
        double pw = w - left - right, ph = h - top - bottom;
        const double dbMin = -100, dbMax = -20;
        var (fMin, fMax, ticks) = Range();

        double X(double mhz) => left + (mhz - fMin) / (fMax - fMin) * pw;
        double Y(double db) => top + ph - (Math.Clamp(db, dbMin, dbMax) - dbMin) / (dbMax - dbMin) * ph;

        var axis = ChartText.Axis;
        var grid = ChartText.Grid;
        for (int db = -100; db <= -20; db += 10)
        {
            double y = Y(db);
            dc.DrawLine(grid, new Point(left, y), new Point(left + pw, y));
            var t = ChartText.Make(this, $"{db} dBm", 10.5, axis);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }
        foreach (var (ch, mhz, label) in ticks)
        {
            double x = X(mhz);
            dc.DrawLine(grid, new Point(x, top), new Point(x, top + ph));
            if (label)
            {
                var t = ChartText.Make(this, ch.ToString(), 10.5, axis);
                dc.DrawText(t, new Point(x - t.Width / 2, top + ph + 6));
            }
        }

        var nets = Networks.Where(n => n.Band == Band).OrderBy(n => n.Rssi).ToList();
        if (nets.Count == 0)
        {
            var t = ChartText.Make(this, NetStuff.Infrastructure.L.T("Keine Netze in diesem Band"), 13, axis);
            dc.DrawText(t, new Point(left + pw / 2 - t.Width / 2, top + ph / 2 - t.Height / 2));
            return;
        }

        double baseY = Y(dbMin);
        foreach (var n in nets)
        {
            var color = ColorFor(n.Ssid);
            bool hi = n.Bssid == HighlightBssid;
            double l = X(n.CenterMhz - n.WidthMhz / 2.0), r = X(n.CenterMhz + n.WidthMhz / 2.0);
            double peak = Y(n.Rssi);
            double edge = (r - l) * 0.12;

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(l, baseY), true, false);
                ctx.QuadraticBezierTo(new Point(l + edge * 0.4, peak), new Point(l + edge * 1.6, peak), true, true);
                ctx.LineTo(new Point(r - edge * 1.6, peak), true, true);
                ctx.QuadraticBezierTo(new Point(r - edge * 0.4, peak), new Point(r, baseY), true, true);
            }
            geo.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(hi ? (byte)110 : (byte)38, color.R, color.G, color.B));
            var pen = new Pen(new SolidColorBrush(color), hi || n.Connected ? 3 : 1.6);
            dc.DrawGeometry(fill, pen, geo);

            var label = ChartText.Make(this, n.DisplaySsid + (n.Connected ? " ●" : ""), 11, new SolidColorBrush(color), hi || n.Connected);
            double cx = (l + r) / 2;
            dc.DrawText(label, new Point(Math.Clamp(cx - label.Width / 2, left, left + pw - label.Width), Math.Max(top, peak - label.Height - 2)));
        }
    }
}
