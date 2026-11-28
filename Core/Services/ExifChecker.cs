using System.Globalization;
using System.Net.Http;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Png;
using MetadataExtractor.Formats.Xmp;
using NetStuff.Infrastructure;
using Directory = MetadataExtractor.Directory;

namespace NetStuff.Services;

/// <summary>Ergebnis des Exif-Checks: Abschnitte für die Anzeige, Standort (falls vorhanden) und ob sich Metadaten entfernen lassen.</summary>
public class ExifResult
{
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public long Size { get; init; }
    public List<CheckSection> Sections { get; } = new();
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool HasLocation => Latitude != null && Longitude != null;
    public bool CanStrip { get; init; }
    public int TagCount { get; set; }

    public string MapUrl => HasLocation
        ? string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={Latitude:0.######}&mlon={Longitude:0.######}#map=17/{Latitude:0.######}/{Longitude:0.######}")
        : null;
}

/// <summary>
/// Exif-Checker: zeigt, was ein Foto über Aufnahmeort, Gerät, Person und Bearbeitung verrät (EXIF, GPS, XMP, IPTC, ICC,
/// Herstellerdaten – JPEG, HEIC/HEIF, PNG, WebP, TIFF, RAW …), und speichert auf Wunsch eine Kopie ohne Metadaten.
/// </summary>
public static class ExifChecker
{
    public const long MaxBytes = 80L * 1024 * 1024;

    /* ------------------------------------------------------------------ Laden */

    /// <summary>Bild von einer Webadresse laden (prüft, was ein Bild auf einer Website preisgibt).</summary>
    public static async Task<(byte[] Data, string Name)> DownloadAsync(string url, CancellationToken ct)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await NetUtil.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxBytes) throw new InvalidOperationException(L.T("Die Datei ist zu groß (über 80 MB)."));
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("html")) throw new InvalidOperationException(L.T("Die Adresse liefert eine Webseite, kein Bild. Bitte die Adresse des Bildes selbst eingeben (Rechtsklick → Bildadresse kopieren)."));
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        return (data, string.IsNullOrEmpty(name) ? "bild" : Uri.UnescapeDataString(name));
    }

    /// <summary>Format anhand der ersten Bytes.</summary>
    public static string DetectFormat(byte[] d)
    {
        if (d.Length < 12) return "";
        if (d[0] == 0xFF && d[1] == 0xD8) return "JPEG";
        if (d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return "PNG";
        if (Ascii(d, 0, 4) == "RIFF" && Ascii(d, 8, 4) == "WEBP") return "WebP";
        if (Ascii(d, 4, 4) == "ftyp")
        {
            var brand = Ascii(d, 8, 4);
            return brand is "avif" or "avis" ? "AVIF" : brand is "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" ? "HEIC" : brand.StartsWith("qt") ? "MOV" : "MP4/HEIF";
        }
        if (d[0] == 'I' && d[1] == 'I' && d[2] == 42 || d[0] == 'M' && d[1] == 'M' && d[3] == 42) return "TIFF/RAW";
        if (Ascii(d, 0, 3) == "GIF") return "GIF";
        return "";
    }

    private static string Ascii(byte[] d, int off, int len) => off + len <= d.Length ? Encoding.ASCII.GetString(d, off, len) : "";

    /* ------------------------------------------------------------------ Auswerten */

    public static ExifResult Analyze(byte[] data, string fileName)
    {
        var format = DetectFormat(data);
        IReadOnlyList<Directory> dirs;
        try { dirs = ImageMetadataReader.ReadMetadata(new MemoryStream(data, writable: false), fileName); }
        catch (ImageProcessingException) { throw new InvalidOperationException(L.T("Unbekanntes oder beschädigtes Bildformat.")); }

        var r = new ExifResult { FileName = fileName, Format = format, Size = data.Length, CanStrip = format is "JPEG" or "PNG" or "WebP" };
        var x = new Reader(dirs);

        // Standort
        var gps = dirs.OfType<GpsDirectory>().FirstOrDefault();
        if (gps != null && gps.TryGetGeoLocation(out var geo) && !(geo.Latitude == 0 && geo.Longitude == 0))
        {
            r.Latitude = geo.Latitude;
            r.Longitude = geo.Longitude;
        }

        var people = new List<string>();
        void Person(string label, string v) { if (!string.IsNullOrWhiteSpace(v) && !people.Any(p => p.EndsWith(v.Trim()))) people.Add($"{L.T(label)}: {v.Trim()}"); }
        Person("Fotograf/Autor", x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagArtist));
        Person("Besitzer der Kamera", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagCameraOwnerName));
        Person("Urheberrecht", x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagCopyright));
        Person("Autor (Windows)", x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagWinAuthor));
        Person("Urheber (IPTC)", x.Get<IptcDirectory>(IptcDirectory.TagByLine));
        Person("Copyright (IPTC)", x.Get<IptcDirectory>(IptcDirectory.TagCopyrightNotice));
        Person("Urheber (XMP)", x.Xmp("dc:creator"));
        Person("Rechte (XMP)", x.Xmp("dc:rights"));

        var serials = new List<string>();
        void Serial(string label, string v) { if (!string.IsNullOrWhiteSpace(v) && v.Trim('0', ' ') != "" && !serials.Any(s => s.EndsWith(v.Trim()))) serials.Add($"{L.T(label)}: {v.Trim()}"); }
        Serial("Kamera", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagBodySerialNumber));
        Serial("Objektiv", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagLensSerialNumber));
        Serial("Kamera (XMP)", x.Xmp("aux:SerialNumber") ?? x.Xmp("exifEX:BodySerialNumber"));
        Serial("Objektiv (XMP)", x.Xmp("aux:LensSerialNumber") ?? x.Xmp("exifEX:LensSerialNumber"));
        // Herstellerdaten (Canon, Nikon, Sony, Olympus …) nennen sie oft zusätzlich
        foreach (var d in dirs.Where(d => d.Name.Contains("Makernote", StringComparison.OrdinalIgnoreCase)))
            foreach (var t in d.Tags.Where(t => t.Name.Contains("Serial", StringComparison.OrdinalIgnoreCase)))
                Serial(t.Name, t.Description);

        var camera = Join(x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagMake), x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagModel));
        var software = x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagSoftware) ?? x.Xmp("xmp:CreatorTool");
        var taken = x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagDateTimeOriginal);
        var offset = x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagTimeZoneOriginal);
        var description = First(x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagImageDescription), x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagUserComment),
            x.Get<IptcDirectory>(IptcDirectory.TagCaption), x.Xmp("dc:description"), x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagWinComment));
        bool thumb = dirs.OfType<ExifThumbnailDirectory>().Any(d => d.ContainsTag(ExifThumbnailDirectory.TagThumbnailOffset) || d.ContainsTag(ExifDirectoryBase.TagCompression));
        var history = x.XmpCount("xmpMM:History");

        // ---- Datenschutz
        var p = new List<CheckItem>();
        var verdict = r.HasLocation ? Level.Bad : people.Count > 0 || serials.Count > 0 ? Level.Warn : Level.Ok;
        p.Add(new CheckItem("Gesamturteil", verdict switch
        {
            Level.Bad => L.T("Das Foto verrät, wo es aufgenommen wurde – vor dem Weitergeben Metadaten entfernen."),
            Level.Warn => L.T("Kein Standort, aber persönliche Angaben (Namen oder Seriennummern) enthalten."),
            _ => dirs.Count <= 3 ? L.T("Keine oder kaum Metadaten – unbedenklich.") : L.T("Kein Standort und keine persönlichen Angaben gefunden."),
        }, verdict));
        p.Add(r.HasLocation
            ? new CheckItem("Aufnahmeort", Coord(r.Latitude!.Value, r.Longitude!.Value) + L.T(" (GPS)"), Level.Bad)
            : new CheckItem("Aufnahmeort", "nicht gespeichert", Level.Ok));
        if (people.Count > 0) p.Add(new CheckItem("Namen", string.Join(" · ", people), Level.Warn));
        if (serials.Count > 0) p.Add(new CheckItem("Seriennummern", string.Join(" · ", serials), Level.Warn));
        if (taken != null) p.Add(new CheckItem("Aufnahmezeit", taken + (offset != null ? $" (UTC{offset})" : "")));
        if (camera != "") p.Add(new CheckItem("Gerät", camera));
        if (software != null) p.Add(new CheckItem("Software", software));
        if (description != null) p.Add(new CheckItem("Beschreibung", Shorten(description, 300), Level.Warn));
        if (thumb) p.Add(new CheckItem("Vorschaubild", "eingebettet – kann den Zustand vor einer Bearbeitung (z. B. vor dem Zuschneiden) zeigen", Level.Warn));
        if (history > 0) p.Add(new CheckItem("Bearbeitungsverlauf", L.F("{0} Schritte gespeichert (XMP)", history), Level.Warn));
        r.Sections.Add(new CheckSection("Datenschutz", p));

        // ---- Datei
        var f = new List<CheckItem>
        {
            new("Datei", fileName),
            new("Format", x.Get<FileTypeDirectory>(FileTypeDirectory.TagDetectedFileTypeLongName) ?? (format == "" ? "unbekannt" : format)),
            new("Größe", SiteCopier.FormatSize(data.Length)),
        };
        var (w, h) = x.Dimensions();
        if (w > 0 && h > 0) f.Add(new CheckItem("Abmessungen", L.F("{0} × {1} Pixel ({2:0.0} MP)", w, h, w * (double)h / 1_000_000)));
        if (dirs.OfType<ExifIfd0Directory>().FirstOrDefault(d => d.ContainsTag(ExifDirectoryBase.TagOrientation)) is { } od && od.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orient))
            f.Add(new CheckItem("Ausrichtung", orient switch
            {
                1 => "normal", 2 => "gespiegelt", 3 => "um 180° gedreht", 4 => "um 180° gedreht und gespiegelt",
                5 => "um 90° gedreht und gespiegelt", 6 => "um 90° im Uhrzeigersinn gedreht", 7 => "um 270° gedreht und gespiegelt",
                8 => "um 90° gegen den Uhrzeigersinn gedreht", _ => orient.ToString(),
            }));
        if (x.Get<IccDirectory>(IccDirectory.TagTagDesc) is { } icc) f.Add(new CheckItem("Farbprofil", icc));
        else if (x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagColorSpace) is { } cs) f.Add(new CheckItem("Farbraum", cs));
        if (x.Get<ExifIfd0Directory>(ExifDirectoryBase.TagDateTime) is { } changed) f.Add(new CheckItem("Zuletzt geändert", changed));
        r.Sections.Add(new CheckSection("Datei", f));

        // ---- Aufnahme
        var a = new List<CheckItem>();
        void A(string name, string v) { if (!string.IsNullOrWhiteSpace(v)) a.Add(new CheckItem(name, v.Trim())); }
        A("Kamera", camera);
        A("Objektiv", Join(x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagLensMake), x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagLensModel)));
        A("Aufgenommen", taken);
        A("Digitalisiert", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagDateTimeDigitized));
        A("Belichtungszeit", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagExposureTime));
        A("Blende", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagFNumber));
        A("ISO", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagIsoEquivalent));
        var focal = x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.Tag35MMFilmEquivFocalLength);
        A("Brennweite", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagFocalLength) + (focal != null ? L.F(" (KB-äquivalent {0})", focal) : ""));
        A("Blitz", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagFlash));
        A("Belichtungsprogramm", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagExposureProgram));
        A("Weißabgleich", x.Get<ExifSubIfdDirectory>(ExifDirectoryBase.TagWhiteBalanceMode));
        A("Software", software);
        if (a.Count > 0) r.Sections.Add(new CheckSection("Aufnahme", a));

        // ---- Standort
        if (r.HasLocation)
        {
            var g = new List<CheckItem>
            {
                new("Koordinaten", Coord(r.Latitude!.Value, r.Longitude!.Value), Level.Bad),
                new("Grad/Minuten/Sekunden", GeoLocation.DecimalToDegreesMinutesSecondsString(r.Latitude.Value) + ", " + GeoLocation.DecimalToDegreesMinutesSecondsString(r.Longitude.Value)),
            };
            void G(string name, string v) { if (!string.IsNullOrWhiteSpace(v)) g.Add(new CheckItem(name, v)); }
            G("Höhe", gps!.GetDescription(GpsDirectory.TagAltitude));
            G("Blickrichtung", gps.GetDescription(GpsDirectory.TagImgDirection) is { } dir ? dir + " (" + gps.GetDescription(GpsDirectory.TagImgDirectionRef) + ")" : null);
            G("Geschwindigkeit", gps.GetDescription(GpsDirectory.TagSpeed));
            G("Genauigkeit", gps.GetDescription(GpsDirectory.TagHPositioningError));
            if (gps.TryGetGpsDate(out var gd)) G("GPS-Zeit (UTC)", gd.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
            G("Karte", r.MapUrl);
            r.Sections.Add(new CheckSection("Standort", g, "Diese Angaben stehen im Foto und gehen beim Weitergeben mit"));
        }
        else if (x.Xmp("photoshop:City") is { } city || x.Get<IptcDirectory>(IptcDirectory.TagCity) is { } city2)
        {
            var place = Join(x.Xmp("Iptc4xmpCore:Location") ?? x.Get<IptcDirectory>(IptcDirectory.TagSubLocation), x.Xmp("photoshop:City") ?? x.Get<IptcDirectory>(IptcDirectory.TagCity),
                x.Xmp("photoshop:Country") ?? x.Get<IptcDirectory>(IptcDirectory.TagCountryOrPrimaryLocationName));
            r.Sections.Add(new CheckSection("Standort", new[] { new CheckItem("Ort (Text)", place, Level.Warn) }));
        }

        // ---- Urheber & Beschreibung
        var u = new List<CheckItem>();
        foreach (var person in people) { var i = person.IndexOf(": ", StringComparison.Ordinal); u.Add(new CheckItem(person[..i], person[(i + 2)..])); }
        void U(string name, string v) { if (!string.IsNullOrWhiteSpace(v)) u.Add(new CheckItem(name, Shorten(v.Trim(), 500))); }
        U("Titel", x.Xmp("dc:title") ?? x.Get<IptcDirectory>(IptcDirectory.TagObjectName));
        U("Beschreibung", description);
        U("Stichwörter", x.Get<IptcDirectory>(IptcDirectory.TagKeywords) ?? x.XmpList("dc:subject"));
        U("Bewertung", x.Xmp("xmp:Rating"));
        if (u.Count > 0) r.Sections.Add(new CheckSection("Urheber & Beschreibung", u));

        // ---- Alle Metadaten
        var all = new List<CheckItem>();
        foreach (var d in dirs)
        {
            if (d is XmpDirectory xd)
            {
                foreach (var (k, v) in xd.GetXmpProperties().OrderBy(kv => kv.Key))
                    if (!string.IsNullOrWhiteSpace(v)) all.Add(new CheckItem("XMP · " + k, Shorten(v, 300)));
                continue;
            }
            foreach (var t in d.Tags)
            {
                var v = t.Description ?? "";
                if (v == "" || t.Name.StartsWith("Unknown tag", StringComparison.OrdinalIgnoreCase)) continue;
                all.Add(new CheckItem(d.Name + " · " + t.Name, Shorten(v, 300)));
            }
            foreach (var err in d.Errors) all.Add(new CheckItem(d.Name, err, Level.Warn));
        }
        r.TagCount = all.Count;
        r.Sections.Add(new CheckSection("Alle Metadaten", all, L.F("{0} Einträge in {1} Bereichen", all.Count, dirs.Count)));
        return r;
    }

    private static string Coord(double lat, double lon) => string.Create(CultureInfo.InvariantCulture, $"{lat:0.000000}, {lon:0.000000}");
    private static string Join(params string[] parts)
    {
        var list = new List<string>();
        foreach (var p in parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()))
            if (!list.Any(l => l.Contains(p, StringComparison.OrdinalIgnoreCase))) list.Add(p);
        // „Apple“ + „iPhone 15 Pro“ bleibt, „Canon“ + „Canon EOS R6“ wird zu „Canon EOS R6“
        if (list.Count == 2 && list[1].StartsWith(list[0], StringComparison.OrdinalIgnoreCase)) list.RemoveAt(0);
        return string.Join(" ", list);
    }
    private static string First(params string[] v) => v.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && s.Trim('\0', ' ') != "")?.Trim();
    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    /// <summary>Bequemer Zugriff auf Tags aller Verzeichnisse eines Typs und auf XMP-Eigenschaften.</summary>
    private sealed class Reader(IReadOnlyList<Directory> dirs)
    {
        private readonly IDictionary<string, string> _xmp = dirs.OfType<XmpDirectory>().FirstOrDefault()?.GetXmpProperties() ?? new Dictionary<string, string>();

        public string Get<T>(int tag) where T : Directory
        {
            foreach (var d in dirs.OfType<T>())
                if (d.ContainsTag(tag) && d.GetDescription(tag) is { } s && s.Trim('\0', ' ') != "") return s.Trim('\0', ' ');
            return null;
        }

        /// <summary>XMP-Wert; bei Listen/Alternativen (dc:creator[1], dc:title[1]) der erste Eintrag.</summary>
        public string Xmp(string key) =>
            _xmp.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v
            : _xmp.TryGetValue(key + "[1]", out v) && !string.IsNullOrWhiteSpace(v) ? v : null;

        public string XmpList(string key)
        {
            var items = _xmp.Where(kv => kv.Key.StartsWith(key + "[")).OrderBy(kv => kv.Key).Select(kv => kv.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
            return items.Count > 0 ? string.Join(", ", items) : null;
        }

        public int XmpCount(string key) =>
            _xmp.Keys.Where(k => k.StartsWith(key + "[")).Select(k => k[(key.Length + 1)..].Split(']')[0]).Distinct().Count();

        public (int W, int H) Dimensions()
        {
            foreach (var d in dirs)
            {
                (int, int)? Try(int wt, int ht) => d.TryGetInt32(wt, out var w) && d.TryGetInt32(ht, out var h) && w > 0 && h > 0 ? (w, h) : null;
                var dim = d switch
                {
                    JpegDirectory => Try(JpegDirectory.TagImageWidth, JpegDirectory.TagImageHeight),
                    PngDirectory => Try(PngDirectory.TagImageWidth, PngDirectory.TagImageHeight),
                    ExifSubIfdDirectory => Try(ExifDirectoryBase.TagExifImageWidth, ExifDirectoryBase.TagExifImageHeight),
                    // HEIC/HEIF, WebP, PNG-Varianten …: Tags heißen „Image Width“/„Image Height“
                    _ => d.Tags.FirstOrDefault(t => t.Name == "Image Width") is { } wt && d.Tags.FirstOrDefault(t => t.Name == "Image Height") is { } ht
                        ? Try(wt.Type, ht.Type) : null,
                };
                if (dim is { } r) return r;
            }
            return (0, 0);
        }
    }

    /* ------------------------------------------------------------------ Metadaten entfernen */

    /// <summary>
    /// Kopie ohne Metadaten. JPEG: EXIF/XMP/IPTC/Kommentare raus, Farbprofil (ICC) und Adobe-Farbinfo bleiben, Ausrichtung
    /// wird als minimaler EXIF-Block erhalten (sonst stünde das Bild ggf. auf dem Kopf). PNG: Text-, Zeit- und EXIF-Blöcke raus.
    /// WebP: EXIF- und XMP-Block raus. Andere Formate: null.
    /// </summary>
    public static byte[] StripMetadata(byte[] data)
    {
        return DetectFormat(data) switch
        {
            "JPEG" => StripJpeg(data),
            "PNG" => StripPng(data),
            "WebP" => StripWebP(data),
            _ => null,
        };
    }

    private static byte[] StripJpeg(byte[] d)
    {
        int orientation = 1;
        try
        {
            var dirs = ImageMetadataReader.ReadMetadata(new MemoryStream(d, false));
            if (dirs.OfType<ExifIfd0Directory>().FirstOrDefault() is { } ifd0 && ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var o) && o is >= 2 and <= 8)
                orientation = o;
        }
        catch { }

        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 2); // SOI
        int pos = 2;
        bool orientationWritten = orientation == 1;
        while (pos + 4 <= d.Length)
        {
            if (d[pos] != 0xFF) throw new InvalidOperationException(L.T("JPEG-Datei ist beschädigt."));
            byte marker = d[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // Füllbytes
            if (marker == 0xDA || marker == 0xD9) { ms.Write(d, pos, d.Length - pos); break; } // Bilddaten (SOS) bis zum Ende unverändert
            int len = d[pos + 2] << 8 | d[pos + 3];
            if (len < 2 || pos + 2 + len > d.Length) throw new InvalidOperationException(L.T("JPEG-Datei ist beschädigt."));
            bool keep = marker switch
            {
                0xE0 => true,                                                          // APP0 JFIF
                0xE2 => len >= 14 && Ascii(d, pos + 4, 11) == "ICC_PROFILE",           // Farbprofil
                0xEE => true,                                                          // APP14 Adobe (Farbumrechnung)
                >= 0xE1 and <= 0xEF => false,                                          // EXIF, XMP, IPTC/Photoshop, Herstellerdaten
                0xFE => false,                                                         // Kommentar
                _ => true,                                                             // Tabellen, Frame-Header …
            };
            if (!orientationWritten && marker != 0xE0)
            {
                var app1 = OrientationApp1(orientation);
                ms.Write(app1, 0, app1.Length);
                orientationWritten = true;
            }
            if (keep) ms.Write(d, pos, 2 + len);
            pos += 2 + len;
        }
        return ms.ToArray();
    }

    /// <summary>Minimaler EXIF-Block, der nur die Ausrichtung enthält.</summary>
    private static byte[] OrientationApp1(int orientation)
    {
        var tiff = new byte[]
        {
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,   // Big Endian, IFD0 bei Offset 8
            0, 1,                                       // ein Eintrag
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation, SHORT, 1 Wert
            0, 0, 0, 0,                                 // kein weiteres IFD
        };
        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        int len = payload.Length + 2;
        return new byte[] { 0xFF, 0xE1, (byte)(len >> 8), (byte)len }.Concat(payload).ToArray();
    }

    private static readonly HashSet<string> PngDrop = new() { "eXIf", "tEXt", "zTXt", "iTXt", "tIME" };

    private static byte[] StripPng(byte[] d)
    {
        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 8);
        int pos = 8;
        while (pos + 12 <= d.Length)
        {
            int len = d[pos] << 24 | d[pos + 1] << 16 | d[pos + 2] << 8 | d[pos + 3];
            if (len < 0 || pos + 12 + len > d.Length) throw new InvalidOperationException(L.T("PNG-Datei ist beschädigt."));
            var type = Ascii(d, pos + 4, 4);
            if (!PngDrop.Contains(type)) ms.Write(d, pos, 12 + len);
            pos += 12 + len;
            if (type == "IEND") break;
        }
        return ms.ToArray();
    }

    private static byte[] StripWebP(byte[] d)
    {
        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 12);
        int pos = 12;
        while (pos + 8 <= d.Length)
        {
            var type = Ascii(d, pos, 4);
            int len = BitConverter.ToInt32(d, pos + 4);
            int total = 8 + len + (len & 1);
            if (len < 0 || pos + 8 + len > d.Length) throw new InvalidOperationException(L.T("WebP-Datei ist beschädigt."));
            if (type is not "EXIF" and not "XMP ")
            {
                var chunk = d.AsSpan(pos, Math.Min(total, d.Length - pos)).ToArray();
                if (type == "VP8X" && chunk.Length > 8) chunk[8] &= unchecked((byte)~(0x08 | 0x04)); // Flags „EXIF“ und „XMP“ löschen
                ms.Write(chunk);
            }
            pos += total;
        }
        var result = ms.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4); // RIFF-Größe anpassen
        return result;
    }
}
