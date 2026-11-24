using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Controls;

/// <summary>WLAN-Spektrum: jedes Netz als Trapez über seiner Kanalbreite, Höhe = Signalstärke (Gegenstück zum WPF-SpectrumChart).</summary>
public class SpectrumChart : Control
{
    public IList<WlanBss> Networks { get; set; } = new List<WlanBss>();
    public string Band { get; set; } = "2,4 GHz";
    public string HighlightBssid { get; set; }

    public SpectrumChart()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    public static Color ColorFor(string ssid)
    {
        int hash = 17;
        foreach (var c in ssid ?? "") hash = hash * 31 + c;
        return Palette.Series[Math.Abs(hash % Palette.Series.Length)];
    }

    private IBrush Res(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var r) && r is IBrush b ? b : new SolidColorBrush(fallback);

    private static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    private (double Min, double Max, IEnumerable<(int Ch, double Mhz, bool Label)> Ticks) Range() => Band switch
    {
        "5 GHz" => (5150, 5895, Enumerable.Range(0, 36).Select(i => 36 + i * 4)
            .Where(c => c <= 64 || (c >= 100 && c <= 144) || c >= 149 && c <= 177)
            .Select(c => (c, 5000 + c * 5.0, true))),
        "6 GHz" => (5925, 7125, Enumerable.Range(0, 59).Select(i => 1 + i * 4)
            .Select(c => (c, 5950 + c * 5.0, (c - 1) % 16 == 0))),
        _ => (2400, 2495, Enumerable.Range(1, 14).Select(c => (c, c == 14 ? 2484.0 : 2407 + c * 5.0, true))),
    };

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (w < 100 || h < 80) return;

        const double left = 58, right = 14, top = 14, bottom = 30;
        double pw = w - left - right, ph = h - top - bottom;
        const double dbMin = -100, dbMax = -20;
        var (fMin, fMax, ticks) = Range();

        double X(double mhz) => left + (mhz - fMin) / (fMax - fMin) * pw;
        double Y(double db) => top + ph - (Math.Clamp(db, dbMin, dbMax) - dbMin) / (dbMax - dbMin) * ph;

        var axis = Res("SubtleTextBrush", Colors.Gray);
        var grid = new Pen(Res("CardStrokeBrush", Color.FromArgb(60, 128, 128, 128)), 1, DashStyle.Dot);
        for (int db = -100; db <= -20; db += 10)
        {
            double y = Y(db);
            dc.DrawLine(grid, new Point(left, y), new Point(left + pw, y));
            var t = Text($"{db} dBm", 10.5, axis);
            dc.DrawText(t, new Point(left - t.Width - 6, y - t.Height / 2));
        }
        foreach (var (ch, mhz, label) in ticks)
        {
            double x = X(mhz);
            dc.DrawLine(grid, new Point(x, top), new Point(x, top + ph));
            if (!label) continue;
            var t = Text(ch.ToString(), 10.5, axis);
            dc.DrawText(t, new Point(x - t.Width / 2, top + ph + 6));
        }

        var nets = Networks.Where(n => n.Band == Band).OrderBy(n => n.Rssi).ToList();
        if (nets.Count == 0)
        {
            var t = Text(L.T("Keine Netze in diesem Band"), 13, axis);
            dc.DrawText(t, new Point(left + pw / 2 - t.Width / 2, top + ph / 2 - t.Height / 2));
            return;
        }

        double baseY = Y(dbMin);
        using (dc.PushClip(new Rect(left, 0, pw, h)))
        {
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
                    ctx.BeginFigure(new Point(l, baseY), true);
                    ctx.QuadraticBezierTo(new Point(l + edge * 0.4, peak), new Point(l + edge * 1.6, peak));
                    ctx.LineTo(new Point(r - edge * 1.6, peak));
                    ctx.QuadraticBezierTo(new Point(r - edge * 0.4, peak), new Point(r, baseY));
                    ctx.EndFigure(false);
                }
                var fill = new SolidColorBrush(Color.FromArgb(hi ? (byte)110 : (byte)38, color.R, color.G, color.B));
                var pen = new Pen(new SolidColorBrush(color), hi || n.Connected ? 3 : 1.6);
                dc.DrawGeometry(fill, pen, geo);

                var label = Text(n.DisplaySsid == "(versteckt)" ? L.T("(versteckt)") : n.DisplaySsid + (n.Connected ? " ●" : ""), 11,
                    new SolidColorBrush(color), hi || n.Connected);
                double cx = (l + r) / 2;
                dc.DrawText(label, new Point(Math.Clamp(cx - label.Width / 2, left, Math.Max(left, left + pw - label.Width)), Math.Max(top, peak - label.Height - 2)));
            }
        }
    }
}
