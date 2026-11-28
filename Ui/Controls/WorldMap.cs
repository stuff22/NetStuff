using System.Globalization;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Controls;

/// <summary>
/// Weltkarte für den Traceroute (Gegenstück zur Leaflet-Karte der WPF-App): OpenStreetMap-Kacheln,
/// Route mit nummerierten Hops, Zoom per Mausrad/Doppelklick, Verschieben per Ziehen, Details beim Überfahren.
/// Kacheln werden im Datenordner zwischengespeichert (OSM-Nutzungsrichtlinie).
/// </summary>
public class WorldMap : Control
{
    private const int TileSize = 256;
    private const int MinZoom = 1, MaxZoom = 17;

    private static readonly HttpClient Http = CreateHttp();
    private static readonly SemaphoreSlim DownloadGate = new(4);
    private static readonly Dictionary<string, Bitmap> Memory = new();
    private static readonly HashSet<string> Pending = new();
    private static readonly HashSet<string> Failed = new();

    private List<MapPoint> _points = new();
    private int _zoom = 3;
    private Point _center;          // Weltpixel bei _zoom
    private Point? _dragStart;
    private Point _dragCenter;
    private MapPoint _hover;

    /// <summary>Zoomstufe bei nur einem Punkt (Traceroute: Land/Region, Exif: Straße).</summary>
    public int SinglePointZoom { get; set; } = 8;

    public WorldMap()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        _center = Project(48, 12, _zoom);
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var version = typeof(WorldMap).Assembly.GetName().Version?.ToString(3) ?? "1.0";
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Net-Stuff/{version} (network diagnostics tool)");
        return h;
    }

    /// <summary>Neue Punkte anzeigen und den Ausschnitt passend wählen.</summary>
    public void SetPoints(IEnumerable<MapPoint> points)
    {
        _points = points?.ToList() ?? new();
        _hover = null;
        FitToPoints();
        InvalidateVisual();
    }

    /* ------------------------------------------------------------------ Projektion */

    private static double WorldSize(int z) => TileSize * Math.Pow(2, z);

    private static Point Project(double lat, double lon, int z)
    {
        lat = Math.Clamp(lat, -85.05, 85.05);
        double size = WorldSize(z);
        double x = (lon + 180) / 360 * size;
        double r = lat * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(r) + 1 / Math.Cos(r)) / Math.PI) / 2 * size;
        return new Point(x, y);
    }

    private Point ToScreen(Point world) =>
        new(world.X - _center.X + Bounds.Width / 2, world.Y - _center.Y + Bounds.Height / 2);

    private void FitToPoints()
    {
        if (_points.Count == 0) return;
        double w = Math.Max(200, Bounds.Width), h = Math.Max(150, Bounds.Height);
        int zoom = MinZoom;
        for (int z = 12; z >= MinZoom; z--)
        {
            var ps = _points.Select(p => Project(p.Lat, p.Lon, z)).ToList();
            double dx = ps.Max(p => p.X) - ps.Min(p => p.X), dy = ps.Max(p => p.Y) - ps.Min(p => p.Y);
            if (dx <= w - 100 && dy <= h - 100) { zoom = z; break; }
        }
        if (_points.Select(p => (p.Lat, p.Lon)).Distinct().Count() == 1) zoom = SinglePointZoom;
        _zoom = zoom;
        var all = _points.Select(p => Project(p.Lat, p.Lon, _zoom)).ToList();
        _center = new Point((all.Min(p => p.X) + all.Max(p => p.X)) / 2, (all.Min(p => p.Y) + all.Max(p => p.Y)) / 2);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width < 10 && _points.Count > 0) FitToPoints();
    }

    /* ------------------------------------------------------------------ Bedienung */

    private void ZoomAt(int newZoom, Point screen)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        if (newZoom == _zoom) return;
        // Punkt unter dem Mauszeiger bleibt stehen
        var world = new Point(screen.X - Bounds.Width / 2 + _center.X, screen.Y - Bounds.Height / 2 + _center.Y);
        double f = Math.Pow(2, newZoom - _zoom);
        _center = new Point(world.X * f - (screen.X - Bounds.Width / 2), world.Y * f - (screen.Y - Bounds.Height / 2));
        _zoom = newZoom;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(_zoom + (e.Delta.Y > 0 ? 1 : -1), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2) { ZoomAt(_zoom + 1, e.GetPosition(this)); return; }
        _dragStart = e.GetPosition(this);
        _dragCenter = _center;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_dragStart is Point start)
        {
            _center = new Point(_dragCenter.X - (pos.X - start.X), _dragCenter.Y - (pos.Y - start.Y));
            InvalidateVisual();
            return;
        }
        var hit = _points.LastOrDefault(p =>
        {
            var s = ToScreen(Project(p.Lat, p.Lon, _zoom));
            return Math.Abs(s.X - pos.X) <= 12 && Math.Abs(s.Y - pos.Y) <= 12;
        });
        if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    /* ------------------------------------------------------------------ Kacheln */

    private static string TilePath(int z, int x, int y) => Path.Combine(Platform.DataDir, "tiles", z.ToString(), x.ToString(), y + ".png");

    private Bitmap Tile(int z, int x, int y)
    {
        var key = $"{z}/{x}/{y}";
        lock (Memory)
        {
            if (Memory.TryGetValue(key, out var bmp)) return bmp;
            if (Pending.Contains(key) || Failed.Contains(key)) return null;
            Pending.Add(key);
        }
        _ = LoadTileAsync(z, x, y, key);
        return null;
    }

    private async Task LoadTileAsync(int z, int x, int y, string key)
    {
        Bitmap bmp = null;
        try
        {
            var file = TilePath(z, x, y);
            if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromDays(14))
            {
                bmp = await Task.Run(() => new Bitmap(file));
            }
            else
            {
                await DownloadGate.WaitAsync();
                try
                {
                    var bytes = await Http.GetByteArrayAsync($"https://tile.openstreetmap.org/{z}/{x}/{y}.png");
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await File.WriteAllBytesAsync(file, bytes);
                    }
                    catch { }
                    bmp = await Task.Run(() => new Bitmap(new MemoryStream(bytes)));
                }
                finally { DownloadGate.Release(); }
            }
        }
        catch { }
        lock (Memory)
        {
            Pending.Remove(key);
            if (bmp != null) Memory[key] = bmp;
            else Failed.Add(key);
        }
        if (bmp != null) Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    /* ------------------------------------------------------------------ Zeichnen */

    private static FormattedText Text(string s, double size, IBrush brush, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        dc.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x1c, 0x1c, 0x1c) : Color.FromRgb(0xE8, 0xEC, 0xEF)), new Rect(0, 0, w, h));
        if (w < 20 || h < 20) return;

        // Kacheln
        int n = 1 << _zoom;
        double left = _center.X - w / 2, top = _center.Y - h / 2;
        int x0 = (int)Math.Floor(left / TileSize), x1 = (int)Math.Floor((left + w) / TileSize);
        int y0 = Math.Max(0, (int)Math.Floor(top / TileSize)), y1 = Math.Min(n - 1, (int)Math.Floor((top + h) / TileSize));
        for (int ty = y0; ty <= y1; ty++)
            for (int tx = x0; tx <= x1; tx++)
            {
                var bmp = Tile(_zoom, ((tx % n) + n) % n, ty);
                if (bmp == null) continue;
                var dest = new Rect(tx * TileSize - left, ty * TileSize - top, TileSize, TileSize);
                dc.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height), dest);
            }
        if (dark) dc.FillRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), new Rect(0, 0, w, h));

        if (_points.Count == 0)
        {
            var t = Text(L.T("Starte einen Traceroute, um die Route auf der Karte zu sehen."), 13, new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
            dc.FillRectangle(new SolidColorBrush(Color.FromArgb(200, dark ? (byte)30 : (byte)255, dark ? (byte)30 : (byte)255, dark ? (byte)30 : (byte)255)),
                new Rect(w / 2 - t.Width / 2 - 12, h / 2 - t.Height / 2 - 6, t.Width + 24, t.Height + 12), 6);
            dc.DrawText(t, new Point(w / 2 - t.Width / 2, h / 2 - t.Height / 2));
        }
        else
        {
            var screen = _points.Select(p => ToScreen(Project(p.Lat, p.Lon, _zoom))).ToList();

            // Route
            var line = new Pen(new SolidColorBrush(Color.FromArgb(220, 0x4C, 0xA3, 0xFF)), 3, lineCap: PenLineCap.Round);
            var halo = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 6, lineCap: PenLineCap.Round);
            for (int i = 1; i < screen.Count; i++) dc.DrawLine(halo, screen[i - 1], screen[i]);
            for (int i = 1; i < screen.Count; i++) dc.DrawLine(line, screen[i - 1], screen[i]);

            // Marker
            var white = new Pen(Brushes.White, 2);
            for (int i = 0; i < _points.Count; i++)
            {
                var p = _points[i];
                var color = p.Kind switch
                {
                    "start" => Palette.Ok,
                    "end" => Palette.Bad,
                    _ => Color.FromRgb(0x4C, 0xA3, 0xFF),
                };
                dc.DrawEllipse(new SolidColorBrush(color), white, screen[i], 11, 11);
                var label = Text(p.Kind == "start" ? "●" : p.Hop.ToString(), 10.5, Brushes.White, true);
                dc.DrawText(label, new Point(screen[i].X - label.Width / 2, screen[i].Y - label.Height / 2));
            }

            // Details zum überfahrenen Punkt
            if (_hover != null)
            {
                var s = ToScreen(Project(_hover.Lat, _hover.Lon, _zoom));
                var lines = new List<string>
                {
                    _hover.Kind == "start" ? L.T("Ihr Standort") : L.F("Hop {0}", _hover.Hop),
                    _hover.Ip + (string.IsNullOrEmpty(_hover.Host) ? "" : " · " + _hover.Host),
                };
                if (!string.IsNullOrEmpty(_hover.Place)) lines.Add(_hover.Place);
                if (!string.IsNullOrEmpty(_hover.Isp)) lines.Add(_hover.Isp);
                if (!string.IsNullOrEmpty(_hover.Rtt)) lines.Add("RTT " + _hover.Rtt);
                var fg = dark ? Brushes.White : Brushes.Black;
                var texts = lines.Select((l, i) => Text(l, 12, fg, i == 0)).ToList();
                double bw = texts.Max(t => t.Width) + 20, bh = texts.Sum(t => t.Height) + 14;
                double bx = Math.Clamp(s.X + 16, 4, Math.Max(4, w - bw - 4)), by = Math.Clamp(s.Y - bh / 2, 4, Math.Max(4, h - bh - 4));
                dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromArgb(235, 40, 40, 40) : Color.FromArgb(240, 255, 255, 255)),
                    new Pen(new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)), 1), new Rect(bx, by, bw, bh), 6, 6);
                double y = by + 7;
                foreach (var t in texts) { dc.DrawText(t, new Point(bx + 10, y)); y += t.Height; }
            }
        }

        // Quellenangabe (Pflicht laut OSM-Lizenz)
        var attr = Text("© OpenStreetMap-Mitwirkende", 10, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
        dc.FillRectangle(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), new Rect(w - attr.Width - 10, h - attr.Height - 4, attr.Width + 10, attr.Height + 4));
        dc.DrawText(attr, new Point(w - attr.Width - 5, h - attr.Height - 2));
    }
}
