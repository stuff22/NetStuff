using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.FileType;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;
using NetStuff.Infrastructure;
using MDirectory = MetadataExtractor.Directory;

namespace NetStuff.Services;

/// <summary>Kurzinfo zur ausgewerteten Datei (für Vorschau, Kartenlink, „Kopie ohne Metadaten“).</summary>
public sealed class ExifResult
{
    public string FileName { get; init; }
    public long Length { get; init; }
    public string Format { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string MapUrl => Latitude is { } lat && Longitude is { } lon ? ExifAnalyzer.OsmUrl(lat, lon) : null;
    /// <summary>1 = normal, 3 = 180°, 6 = 90° rechts, 8 = 90° links (EXIF-Ausrichtung).</summary>
    public int Orientation { get; init; } = 1;
    /// <summary>JPEG oder PNG: Metadaten lassen sich entfernen.</summary>
    public bool CanStrip { get; init; }
    public int MetadataCount { get; init; }
}

/// <summary>
/// Exif-Checker: liest alle Metadaten eines Fotos oder Videos (EXIF, GPS, XMP, IPTC, ICC, Herstellerdaten von Canon,
/// Nikon, Sony, Apple …; JPEG, HEIC, PNG, WebP, TIFF, RAW, MP4/MOV) und erklärt sie verständlich: Kamera, Belichtung,
/// Zeit, Aufnahmeort, Bearbeitung, KI-Herkunft und was das Bild beim Weitergeben über den Fotografen verrät.
/// </summary>
public static class ExifAnalyzer
{
    // EXIF-Tags (IFD0 / Exif-SubIFD)
    private const int TMake = 0x010F, TModel = 0x0110, TOrientation = 0x0112, TXRes = 0x011A, TYRes = 0x011B, TResUnit = 0x0128,
        TSoftware = 0x0131, TDateTime = 0x0132, TArtist = 0x013B, THost = 0x013C, TDescription = 0x010E, TCopyright = 0x8298,
        TExposure = 0x829A, TFNumber = 0x829D, TProgram = 0x8822, TIso = 0x8827, TOriginal = 0x9003, TDigitized = 0x9004,
        TOffset = 0x9010, TOffsetOriginal = 0x9011, TBias = 0x9204, TMaxAperture = 0x9205, TDistance = 0x9206, TMetering = 0x9207,
        TLight = 0x9208, TFlash = 0x9209, TFocal = 0x920A, TUserComment = 0x9286, TSubSec = 0x9291, TColorSpace = 0xA001,
        TWidth = 0xA002, THeight = 0xA003, TExposureMode = 0xA402, TWhiteBalance = 0xA403, TZoom = 0xA404, TFocal35 = 0xA405,
        TScene = 0xA406, TUniqueId = 0xA420, TOwner = 0xA430, TBodySerial = 0xA431, TLensSpec = 0xA432, TLensMake = 0xA433,
        TLensModel = 0xA434, TLensSerial = 0xA435;

    // GPS-Tags
    private const int GAltRef = 5, GAlt = 6, GSatellites = 8, GDop = 11, GSpeedRef = 12, GSpeed = 13, GImgDirRef = 16, GImgDir = 17,
        GMapDatum = 18, GProcessing = 27, GHError = 31;

    /* ------------------------------------------------------------------ Hilfsklasse */

    private sealed class Meta
    {
        public IReadOnlyList<MDirectory> Dirs;
        public List<MDirectory> Exif = new();
        public GpsDirectory Gps;
        public IDictionary<string, string> Xmp = new Dictionary<string, string>();
        public IptcDirectory Iptc;
        public IccDirectory Icc;
        public JpegDirectory Jpeg;
        public FileTypeDirectory FileType;
        public ExifThumbnailDirectory Thumb;

        public Meta(IReadOnlyList<MDirectory> dirs)
        {
            Dirs = dirs;
            foreach (var d in dirs)
            {
                switch (d)
                {
                    case ExifIfd0Directory or ExifSubIfdDirectory: Exif.Add(d); break;
                    case GpsDirectory g: Gps ??= g; break;
                    case XmpDirectory x:
                        try { foreach (var kv in x.GetXmpProperties()) Xmp[kv.Key] = kv.Value; } catch { }
                        break;
                    case IptcDirectory i: Iptc ??= i; break;
                    case IccDirectory c: Icc ??= c; break;
                    case JpegDirectory j: Jpeg ??= j; break;
                    case FileTypeDirectory f: FileType ??= f; break;
                    case ExifThumbnailDirectory t: Thumb ??= t; break;
                }
            }
        }

        public string S(int tag)
        {
            foreach (var d in Exif)
            {
                var v = Clean(d.GetString(tag));
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        public string Desc(int tag)
        {
            foreach (var d in Exif)
            {
                var v = Clean(d.GetDescription(tag));
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        public double? R(int tag)
        {
            foreach (var d in Exif)
                if (d.TryGetRational(tag, out var r) && r.Denominator != 0) return r.ToDouble();
            return null;
        }

        public int? I(int tag)
        {
            foreach (var d in Exif)
                if (d.TryGetInt32(tag, out var v)) return v;
            return null;
        }

        /// <summary>XMP-Eigenschaft, deren Pfad auf einen der Namen endet (z. B. „xmp:CreatorTool“, „dc:title[1]“).</summary>
        public string X(params string[] names)
        {
            foreach (var n in names)
                foreach (var kv in Xmp)
                    if ((kv.Key.EndsWith(n, StringComparison.OrdinalIgnoreCase) || kv.Key.EndsWith(n + "[1]", StringComparison.OrdinalIgnoreCase))
                        && !string.IsNullOrWhiteSpace(kv.Value)) return kv.Value.Trim();
            return null;
        }

        public string Iptcs(int tag) => Clean(Iptc?.GetDescription(tag));

        public IEnumerable<Tag> AllTags => Dirs.SelectMany(d => d.Tags);
    }

    private static string Clean(string s) => s?.Trim().Trim('\0').Trim();

    /* ------------------------------------------------------------------ Auswertung */

    /// <param name="open">Öffnet die Datei (wird ggf. zweimal aufgerufen – Metadaten und Kopfbereich).</param>
    /// <param name="fileModified">Änderungsdatum der Datei im Dateisystem (falls bekannt).</param>
    public static async Task<ExifResult> AnalyzeAsync(Func<Stream> open, string fileName, long length, ICheckSink sink,
        bool geocode, CancellationToken ct = default, DateTime? fileModified = null)
    {
        sink.Status(L.T("Lese Metadaten …"));
        IReadOnlyList<MDirectory> dirs;
        byte[] head;
        try
        {
            (dirs, head) = await Task.Run(() =>
            {
                using (var s = open()) { var list = ImageMetadataReader.ReadMetadata(s); return ((IReadOnlyList<MDirectory>)list.ToList(), ReadHead(open)); }
            }, ct);
        }
        catch (ImageProcessingException ex)
        {
            throw new InvalidOperationException(L.T("Dateiformat wird nicht unterstützt oder die Datei ist beschädigt.") + " (" + ex.Message + ")");
        }

        var m = new Meta(dirs);
        var format = Clean(m.FileType?.GetDescription(FileTypeDirectory.TagDetectedFileTypeLongName)) ?? Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        var mime = Clean(m.FileType?.GetDescription(FileTypeDirectory.TagDetectedFileMimeType));
        bool isJpeg = head.Length > 3 && head[0] == 0xFF && head[1] == 0xD8;
        bool isPng = head.Length > 8 && head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G';
        bool isVideo = mime?.StartsWith("video/") == true;

        // Werte sammeln
        var make = m.S(TMake);
        var model = m.S(TModel);
        var camera = CameraName(make, model);
        var device = DeviceType(make, model);
        var (width, height) = Dimensions(m);
        int orientation = m.I(TOrientation) ?? 1;
        var (lat, lon, alt) = Location(m);
        var original = ParseExifDate(m.S(TOriginal));
        var offset = m.S(TOffsetOriginal) ?? m.S(TOffset);
        var modified = ParseExifDate(m.S(TDateTime));
        var software = m.S(TSoftware) ?? m.X("xmp:CreatorTool");
        if (software != null && Regex.IsMatch(software, @"^\d+(\.\d+)+$") && make?.StartsWith("Apple", StringComparison.OrdinalIgnoreCase) == true)
            software = (model?.Contains("iPad") == true ? "iPadOS " : model?.Contains("Mac") == true ? "macOS " : "iOS ") + software;   // iPhone schreibt nur „26.3.1“
        var ai = AiHints(m, head);
        bool c2pa = ContainsAscii(head, "c2pa") || ContainsAscii(head, "jumb");
        int metaCount = dirs.Where(d => d is not FileTypeDirectory && d.GetType().Name != "FileMetadataDirectory").Sum(d => d.TagCount);

        var privacy = PrivacyFindings(m, lat, lon, camera, software, original);
        string address = null;
        if (geocode && lat is { } la && lon is { } lo)
        {
            sink.Status(L.T("Ermittle Adresse zum Aufnahmeort …"));
            address = await ReverseGeocodeAsync(la, lo, ct);
        }

        // 1. Auf einen Blick
        var glance = new List<CheckItem>();
        var verdict = Verdict(privacy, lat != null, metaCount, isVideo);
        glance.Add(verdict);
        glance.Add(new CheckItem("Datei", $"{fileName} · {NetUtil.FormatBytes(length)} · {format}"));
        if (width > 0 && height > 0)
            glance.Add(new CheckItem("Bildgröße", L.F("{0} × {1} Pixel ({2:0.0} Megapixel)", width, height, width * (double)height / 1e6)));
        if (camera != null) glance.Add(new CheckItem("Aufgenommen mit", $"{camera} · {L.T(device)}"));
        if (original != null) glance.Add(new CheckItem("Aufnahmezeit", FormatDate(original.Value, offset)));
        var settings = SettingsLine(m);
        if (settings != null) glance.Add(new CheckItem("Einstellungen", settings));
        glance.Add(lat != null
            ? new CheckItem("Aufnahmeort", (address ?? FormatCoord(lat.Value, lon.Value)) + " – " + L.T("im Bild gespeichert!"), Level.Bad)
            : new CheckItem("Aufnahmeort", "nicht gespeichert", Level.Ok));
        if (software != null) glance.Add(new CheckItem("Software", software));
        if (ai.Count > 0) glance.Add(new CheckItem("KI-Hinweise", string.Join(" · ", ai.Select(L.T)), Level.Warn));
        else if (c2pa) glance.Add(new CheckItem("Herkunftsnachweis", "Content Credentials (C2PA) enthalten", Level.Info));
        sink.Add(new CheckSection("Auf einen Blick", glance));

        // 2. Datenschutz
        if (privacy.Count > 0)
            sink.Add(new CheckSection("Was verrät dieses Bild?", privacy,
                isJpeg || isPng
                    ? "Vor dem Weitergeben „Kopie ohne Metadaten“ speichern. Messenger wie WhatsApp oder Signal entfernen GPS meist – E-Mail, Cloud-Links, AirDrop und „als Datei senden“ dagegen nicht."
                    : "Messenger wie WhatsApp oder Signal entfernen GPS meist – E-Mail, Cloud-Links, AirDrop und „als Datei senden“ dagegen nicht. Zum Entfernen in JPEG umwandeln oder die Freigabe-Optionen nutzen (iPhone: Teilen › Optionen › Ort aus)."));

        // 3. Kamera & Objektiv
        var cam = new List<CheckItem>();
        if (make != null) cam.Add(new CheckItem("Hersteller", make));
        if (model != null) cam.Add(new CheckItem("Modell", model));
        if (camera != null) cam.Add(new CheckItem("Gerätetyp", device));
        var lens = m.S(TLensModel) ?? m.X("aux:Lens", "exifEX:LensModel");
        var lensMake = m.S(TLensMake);
        if (lens != null) cam.Add(new CheckItem("Objektiv", lensMake != null && !lens.StartsWith(lensMake, StringComparison.OrdinalIgnoreCase) ? $"{lensMake} {lens}" : lens));
        var spec = LensSpec(m);
        if (spec != null) cam.Add(new CheckItem("Objektiv-Daten", spec));
        if (m.S(TBodySerial) is { } bs) cam.Add(new CheckItem("Seriennummer Kamera", bs, Level.Warn));
        if (m.S(TLensSerial) is { } ls) cam.Add(new CheckItem("Seriennummer Objektiv", ls, Level.Warn));
        if (m.S(TOwner) is { } owner) cam.Add(new CheckItem("Besitzer (in der Kamera eingetragen)", owner, Level.Warn));
        if (m.S(THost) is { } host) cam.Add(new CheckItem("Gerät / Computer", host));
        if (LivePhoto(m)) cam.Add(new CheckItem("Live Photo", "Teil eines iPhone-Live-Photos (dazu gehört ein kurzes Video)"));
        if (cam.Count > 0) sink.Add(new CheckSection("Kamera & Objektiv", cam));

        // 4. Belichtung
        var exp = ExposureItems(m);
        if (exp.Count > 0)
            sink.Add(new CheckSection("Belichtung – verständlich erklärt", exp,
                "Belichtungszeit, Blende und ISO bestimmen zusammen die Helligkeit: kürzere Zeit, kleinere Blende (größere f-Zahl) oder niedrigere ISO machen das Bild dunkler."));

        // 5. Zeitangaben
        var times = new List<CheckItem>();
        if (original != null) times.Add(new CheckItem("Aufnahme", FormatDate(original.Value, offset)
            + (m.S(TSubSec) is { } ss ? " " + L.F("(+0,{0} s)", ss) : "")));
        var digitized = ParseExifDate(m.S(TDigitized));
        if (digitized != null && digitized != original) times.Add(new CheckItem("Digitalisiert", FormatDate(digitized.Value, m.S(0x9012))));
        if (modified != null) times.Add(new CheckItem("Zuletzt geändert (laut Bild)", FormatDate(modified.Value, m.S(TOffset))));
        if (offset != null) times.Add(new CheckItem("Zeitzone der Kamera", "UTC" + offset));
        if (m.Gps != null && m.Gps.TryGetGpsDate(out var gpsTime))
            times.Add(new CheckItem("GPS-Zeit (UTC, sehr genau)", gpsTime.ToString("g", CultureInfo.CurrentCulture)));
        if (fileModified != null) times.Add(new CheckItem("Datei geändert (Dateisystem)", fileModified.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
        if (original != null && modified != null && (modified.Value - original.Value).TotalMinutes > 1)
            times.Add(new CheckItem("Hinweis", L.F("Das Bild wurde {0} nach der Aufnahme gespeichert – vermutlich bearbeitet oder exportiert.", Span(modified.Value - original.Value)), Level.Info));
        if (original != null && original.Value > DateTime.Now.AddDays(1))
            times.Add(new CheckItem("Hinweis", "Aufnahmedatum liegt in der Zukunft – die Uhr der Kamera war falsch eingestellt.", Level.Warn));
        if (original != null && m.Gps != null && m.Gps.TryGetGpsDate(out var g2) && offset == null)
        {
            // Differenz Ortszeit – GPS-Zeit (UTC) ergibt die Zeitzone, wenn sie fast genau volle Stunden beträgt
            var hours = (original.Value - g2).TotalHours;
            var rounded = Math.Round(hours);
            if (rounded != 0 && Math.Abs(rounded) <= 14 && Math.Abs(hours - rounded) < 0.1)
                times.Add(new CheckItem("Zeitzone (aus GPS errechnet)", L.F("UTC{0:+0;-0} h", rounded)));
        }
        if (times.Count > 0) sink.Add(new CheckSection("Zeitangaben", times));

        // 6. Aufnahmeort
        if (lat != null)
        {
            var loc = new List<CheckItem>
            {
                new("Koordinaten", FormatCoord(lat.Value, lon.Value), Level.Bad),
                new("In Grad/Minuten/Sekunden", Dms(lat.Value, true) + "  " + Dms(lon.Value, false)),
            };
            if (address != null) loc.Add(new CheckItem("Adresse (ungefähr)", address, Level.Bad));
            if (alt != null) loc.Add(new CheckItem("Höhe", L.F("{0:0} m über dem Meeresspiegel", alt.Value)));
            if (m.Gps?.TryGetRational(GImgDir, out var dir) == true && dir.Denominator != 0)
                loc.Add(new CheckItem("Blickrichtung", L.F("{0:0}° (Blick nach {1})", dir.ToDouble(), L.T(Compass(dir.ToDouble())))
                    + (m.Gps.GetString(GImgDirRef) == "M" ? " · " + L.T("magnetisch") : "")));
            if (m.Gps?.TryGetRational(GSpeed, out var sp) == true && sp.Denominator != 0)
            {
                var unit = m.Gps.GetString(GSpeedRef) switch { "M" => "mph", "N" => "kn", _ => "km/h" };
                loc.Add(new CheckItem("Geschwindigkeit", $"{sp.ToDouble():0.#} {unit}"));
            }
            if (m.Gps?.TryGetRational(GHError, out var he) == true && he.Denominator != 0)
                loc.Add(new CheckItem("Genauigkeit", L.F("± {0:0} m", he.ToDouble())));
            else if (m.Gps?.TryGetRational(GDop, out var dop) == true && dop.Denominator != 0)
                loc.Add(new CheckItem("Genauigkeit (DOP)", L.F("{0:0.#} – je kleiner, desto genauer", dop.ToDouble())));
            if (Clean(m.Gps?.GetDescription(GSatellites)) is { Length: > 0 } sat) loc.Add(new CheckItem("Satelliten", sat));
            if (Clean(m.Gps?.GetDescription(GProcessing)) is { Length: > 0 } proc) loc.Add(new CheckItem("Ortungsverfahren", proc));
            if (Clean(m.Gps?.GetDescription(GMapDatum)) is { Length: > 0 } datum) loc.Add(new CheckItem("Kartenbezug", datum));
            loc.Add(new CheckItem("Karte", OsmUrl(lat.Value, lon.Value)));
            sink.Add(new CheckSection("Aufnahmeort", loc, "Die Koordinaten sind meist auf wenige Meter genau – bei Fotos von zu Hause also die Wohnadresse."));
        }

        // 7. Bild & Farbe
        var img = ImageItems(m, width, height, orientation, isJpeg, head);
        if (img.Count > 0) sink.Add(new CheckSection("Bild & Farbe", img));

        // 8. Bearbeitung & Herkunft
        var edit = EditItems(m, software, original, modified, ai, c2pa, metaCount, isJpeg, camera);
        if (edit.Count > 0) sink.Add(new CheckSection("Bearbeitung & Herkunft", edit));

        // 9. Urheber & Beschreibung
        var author = AuthorItems(m);
        if (author.Count > 0) sink.Add(new CheckSection("Urheber & Beschreibung", author));

        // 10. Alle Rohdaten (gleiche Blöcke – z. B. ICC-Profil bei HEIC mehrfach – nur einmal)
        var shown = new HashSet<string>();
        foreach (var d in dirs)
        {
            if (d.TagCount == 0 || d.GetType().Name == "FileMetadataDirectory") continue;
            var items = new List<CheckItem>();
            foreach (var t in d.Tags.Take(400))
            {
                var v = Clean(t.Description);
                if (string.IsNullOrEmpty(v)) continue;
                if (v.Length > 400) v = v[..400] + " …";
                items.Add(new CheckItem(t.Name, v));
            }
            if (d.TagCount > 400) items.Add(new CheckItem("", L.F("… {0} weitere Einträge", d.TagCount - 400)));
            if (d is XmpDirectory && m.Xmp.Count > 0)
                foreach (var kv in m.Xmp.Take(300))
                    if (!string.IsNullOrWhiteSpace(kv.Value)) items.Add(new CheckItem(kv.Key, kv.Value.Length > 300 ? kv.Value[..300] + " …" : kv.Value));
            foreach (var e in d.Errors) items.Add(new CheckItem("Fehler beim Lesen", e, Level.Warn));
            var section = new CheckSection(L.F("Alle Daten: {0}", DirName(d)), items);
            if (items.Count > 0 && shown.Add(section.ToText())) sink.Add(section);
        }

        return new ExifResult
        {
            FileName = fileName,
            Length = length,
            Format = format,
            Latitude = lat,
            Longitude = lon,
            Orientation = orientation,
            CanStrip = isJpeg || isPng,
            MetadataCount = metaCount,
        };
    }

    private static byte[] ReadHead(Func<Stream> open)
    {
        try
        {
            using var s = open();
            var buf = new byte[Math.Min(8 * 1024 * 1024, s.CanSeek ? (int)Math.Min(s.Length, int.MaxValue) : 8 * 1024 * 1024)];
            int total = 0, n;
            while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
            return total == buf.Length ? buf : buf[..total];
        }
        catch { return Array.Empty<byte>(); }
    }

    private static string DirName(MDirectory d) => d.Name switch
    {
        "Exif IFD0" => "Exif IFD0 (Kamera & Bild)",
        "Exif SubIFD" => "Exif SubIFD (Aufnahme)",
        "GPS" => "GPS (Ort)",
        "Exif Thumbnail" => "Exif Thumbnail (Vorschaubild)",
        "XMP" => "XMP (Adobe, Bearbeitung)",
        "IPTC" => "IPTC (Presse/Agentur)",
        "ICC Profile" => "ICC-Profil (Farbe)",
        _ when d.Name.Contains("Makernote", StringComparison.OrdinalIgnoreCase) => L.F("{0} (Herstellerdaten)", d.Name),
        _ => d.Name,
    };

    /* ------------------------------------------------------------------ Einzelteile */

    private static readonly string[] PhoneMakers = { "Apple", "Samsung", "Google", "Xiaomi", "Huawei", "OnePlus", "OPPO", "vivo", "Motorola", "Sony Mobile", "HMD", "Nokia", "realme", "Honor", "Nothing", "Fairphone", "LG Electronics", "HTC", "Redmi", "POCO", "ZTE", "Asus" };

    private static string CameraName(string make, string model)
    {
        if (make == null && model == null) return null;
        if (make == null) return model;
        if (model == null) return make;
        return model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase) ? model : $"{make} {model}";
    }

    private static string DeviceType(string make, string model)
    {
        var all = (make + " " + model).ToLowerInvariant();
        if (all.Contains("dji") && !all.Contains("osmo")) return "Drohne";
        if (all.Contains("gopro") || all.Contains("insta360") || all.Contains("osmo")) return "Action-Kamera";
        if (all.Contains("scan")) return "Scanner";
        if (make != null && (PhoneMakers.Any(p => make.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || all.Contains("iphone") || all.Contains("ipad") || all.Contains("pixel")))
            return all.Contains("ipad") ? "Tablet" : "Smartphone";
        return "Kamera";
    }

    private static (int W, int H) Dimensions(Meta m)
    {
        int w = m.I(TWidth) ?? 0, h = m.I(THeight) ?? 0;
        if (w > 0 && h > 0) return (w, h);
        if (m.Jpeg != null && m.Jpeg.TryGetInt32(JpegDirectory.TagImageWidth, out w) && m.Jpeg.TryGetInt32(JpegDirectory.TagImageHeight, out h)) return (w, h);
        // andere Formate (PNG, HEIC, WebP, Video): Tags „Image Width/Height“ bzw. „Width/Height“ suchen
        foreach (var d in m.Dirs)
        {
            int? ww = null, hh = null;
            foreach (var t in d.Tags)
            {
                if (t.Name is "Image Width" or "Width") ww ??= FirstInt(t.Description);
                if (t.Name is "Image Height" or "Height") hh ??= FirstInt(t.Description);
            }
            if (ww > 0 && hh > 0) return (ww.Value, hh.Value);
        }
        return (0, 0);
    }

    private static int? FirstInt(string s) => s != null && Regex.Match(s, @"\d+") is { Success: true } x && int.TryParse(x.Value, out var v) ? v : null;

    private static (double? Lat, double? Lon, double? Alt) Location(Meta m)
    {
        if (m.Gps != null)
        {
            var g = m.Gps.GetGeoLocation();
            if (g != null && !g.IsZero && !double.IsNaN(g.Latitude))
            {
                double? alt = null;
                if (m.Gps.TryGetRational(GAlt, out var a) && a.Denominator != 0)
                    alt = a.ToDouble() * (m.Gps.TryGetInt32(GAltRef, out var r) && r == 1 ? -1 : 1);
                return (g.Latitude, g.Longitude, alt);
            }
        }
        // Videos (QuickTime/MP4): ISO-6709, z. B. „+48.2085+016.3731+171.000/“
        foreach (var t in m.AllTags)
        {
            var v = t.Description;
            if (v == null || v.Length > 60) continue;
            var x = Regex.Match(v, @"^([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)([+-]\d+(?:\.\d+)?)?(?:CRS[^/]*)?/?$");
            if (x.Success)
            {
                double la = double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture), lo = double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture);
                double? al = x.Groups[3].Success ? double.Parse(x.Groups[3].Value, CultureInfo.InvariantCulture) : null;
                if (la != 0 || lo != 0) return (la, lo, al);
            }
        }
        // XMP (z. B. von Lightroom gesetzt): exif:GPSLatitude „48,12.51N“
        var xl = m.X("exif:GPSLatitude");
        var xo = m.X("exif:GPSLongitude");
        if (xl != null && xo != null && XmpCoord(xl) is { } xla && XmpCoord(xo) is { } xlo) return (xla, xlo, null);
        return (null, null, null);
    }

    private static double? XmpCoord(string s)
    {
        var x = Regex.Match(s, @"^(\d+),(\d+(?:\.\d+)?)(?:,(\d+(?:\.\d+)?))?([NSEW])$");
        if (!x.Success) return null;
        double v = double.Parse(x.Groups[1].Value, CultureInfo.InvariantCulture) + double.Parse(x.Groups[2].Value, CultureInfo.InvariantCulture) / 60
            + (x.Groups[3].Success ? double.Parse(x.Groups[3].Value, CultureInfo.InvariantCulture) / 3600 : 0);
        return x.Groups[4].Value is "S" or "W" ? -v : v;
    }

    public static string OsmUrl(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"https://www.openstreetmap.org/?mlat={lat:0.######}&mlon={lon:0.######}#map=17/{lat:0.######}/{lon:0.######}");

    public static string GoogleMapsUrl(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture, $"https://www.google.com/maps/search/?api=1&query={lat:0.######},{lon:0.######}");

    private static string FormatCoord(double lat, double lon) => string.Create(CultureInfo.InvariantCulture, $"{lat:0.######}, {lon:0.######}");

    private static string Dms(double v, bool lat)
    {
        var a = Math.Abs(v);
        int d = (int)a;
        double minF = (a - d) * 60;
        int mi = (int)minF;
        double s = (minF - mi) * 60;
        var hemi = lat ? (v >= 0 ? "N" : "S") : (v >= 0 ? "E" : "W");
        return string.Create(CultureInfo.InvariantCulture, $"{d}° {mi}′ {s:0.0}″ {hemi}");
    }

    private static string Compass(double deg) => ((int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8) switch
    {
        0 => "Norden", 1 => "Nordosten", 2 => "Osten", 3 => "Südosten", 4 => "Süden", 5 => "Südwesten", 6 => "Westen", _ => "Nordwesten",
    };

    internal static DateTime? ParseExifDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.StartsWith("0000")) return null;
        foreach (var f in new[] { "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy:MM:dd" })
            if (DateTime.TryParseExact(s.Trim()[..Math.Min(s.Trim().Length, f.Length)], f, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return null;
    }

    private static string FormatDate(DateTime d, string offset) =>
        (L.IsGerman ? d.ToString("dddd, d. MMMM yyyy, HH:mm:ss", CultureInfo.GetCultureInfo("de-AT")) : d.ToString("dddd, d MMMM yyyy, HH:mm:ss", CultureInfo.GetCultureInfo("en-GB")))
        + (string.IsNullOrEmpty(offset) ? "" : $" (UTC{offset})");

    private static string Span(TimeSpan t) =>
        t.TotalDays >= 2 ? L.F("{0:0} Tage", t.TotalDays) : t.TotalHours >= 2 ? L.F("{0:0} Stunden", t.TotalHours) : L.F("{0:0} Minuten", t.TotalMinutes);

    private static string SettingsLine(Meta m)
    {
        var parts = new List<string>();
        if (m.R(TExposure) is { } t) parts.Add(Shutter(t));
        if (m.R(TFNumber) is { } f) parts.Add(string.Create(CultureInfo.InvariantCulture, $"f/{f:0.#}"));
        if (m.I(TIso) is { } iso) parts.Add($"ISO {iso}");
        if (m.R(TFocal) is { } fl) parts.Add($"{fl:0.#} mm" + (m.I(TFocal35) is { } eq && Math.Abs(eq - fl) > 1 ? " " + L.F("(entspricht {0} mm)", eq) : ""));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string Shutter(double t) =>
        t >= 1 ? $"{t:0.#} s" : $"1/{Math.Round(1 / t):0} s";

    private static List<CheckItem> ExposureItems(Meta m)
    {
        var list = new List<CheckItem>();
        if (m.R(TExposure) is { } t)
            list.Add(new CheckItem("Belichtungszeit", Shutter(t) + " – " + L.T(
                t >= 1 ? "Langzeitbelichtung: nur mit Stativ scharf, Bewegungen verwischen (Lichtspuren, Wasser)"
                : t > 1 / 30.0 ? "lang: aus der Hand Verwacklungsgefahr"
                : t >= 1 / 250.0 ? "normal: gut für ruhige Motive aus der Hand"
                : t >= 1 / 1000.0 ? "kurz: Bewegungen werden eingefroren"
                : "sehr kurz: auch schnelle Bewegungen (Sport, Vögel) werden scharf")));
        if (m.R(TFNumber) is { } f)
            list.Add(new CheckItem("Blende", string.Create(CultureInfo.InvariantCulture, $"f/{f:0.#}") + " – " + L.T(
                f <= 2.8 ? "weit offen: viel Licht, unscharfer Hintergrund (Bokeh), typisch für Porträts und Smartphones"
                : f <= 5.6 ? "mittel: ausgewogene Schärfe"
                : f <= 11 ? "geschlossen: großer Schärfebereich von vorne bis hinten (Landschaft)"
                : "stark geschlossen: maximale Schärfentiefe, leichte Unschärfe durch Beugung möglich")));
        if (m.I(TIso) is { } iso)
            list.Add(new CheckItem("ISO (Lichtempfindlichkeit)", $"ISO {iso} – " + L.T(
                iso <= 200 ? "niedrig: gutes Licht, kaum Bildrauschen"
                : iso <= 800 ? "mittel: leicht gedämpftes Licht"
                : iso <= 3200 ? "hoch: wenig Licht, Rauschen kann sichtbar sein"
                : "sehr hoch: dunkle Umgebung, deutliches Bildrauschen")));
        if (m.R(TFocal) is { } fl)
        {
            int? eq = m.I(TFocal35);
            double basis = eq ?? fl;
            var kind = basis < 24 ? "Ultraweitwinkel: sehr großer Bildwinkel" : basis < 35 ? "Weitwinkel: viel aufs Bild, z. B. Räume, Landschaft"
                : basis <= 70 ? "Normalbrennweite: ähnlich dem menschlichen Sehen" : basis <= 135 ? "leichtes Tele: typisch für Porträts" : "Tele: holt Entferntes heran";
            var text = $"{fl:0.#} mm";
            if (eq is { } e && Math.Abs(e - fl) > 1)
                text += " " + L.F("(entspricht {0} mm Kleinbild, Crop-Faktor {1:0.0})", e, e / fl);
            list.Add(new CheckItem("Brennweite", text + " – " + L.T(kind)));
        }
        if (m.R(TZoom) is { } zoom && zoom > 1.01)
            list.Add(new CheckItem("Digitalzoom", L.F("{0:0.0}× – Ausschnittvergrößerung, kostet Bildqualität", zoom), Level.Warn));
        if (m.I(TProgram) is { } prog && prog > 0)
            list.Add(new CheckItem("Belichtungsprogramm", L.T(prog switch
            {
                1 => "Manuell (Zeit und Blende selbst gewählt)", 2 => "Programmautomatik (P)", 3 => "Zeitautomatik (A/Av – Blende vorgewählt)",
                4 => "Blendenautomatik (S/Tv – Zeit vorgewählt)", 5 => "Kreativprogramm (Schärfentiefe)", 6 => "Actionprogramm (kurze Zeiten)",
                7 => "Porträtprogramm", 8 => "Landschaftsprogramm", _ => "unbekannt",
            })));
        if (m.I(TExposureMode) is { } em && em > 0)
            list.Add(new CheckItem("Belichtungsmodus", L.T(em == 1 ? "manuell" : "Belichtungsreihe (Bracketing, z. B. für HDR)")));
        if (m.R(TBias) is { } bias && Math.Abs(bias) > 0.01)
            list.Add(new CheckItem("Belichtungskorrektur", string.Create(CultureInfo.InvariantCulture, $"{bias:+0.0#;-0.0#} EV – ") + L.T(bias > 0 ? "bewusst heller belichtet" : "bewusst dunkler belichtet")));
        if (m.I(TMetering) is { } met && met > 0)
            list.Add(new CheckItem("Belichtungsmessung", L.T(met switch
            {
                1 => "Integral (Durchschnitt des ganzen Bildes)", 2 => "mittenbetont", 3 => "Spot (kleiner Punkt)", 4 => "Mehrfeld-Spot",
                5 => "Mehrfeld/Matrix (ganzes Bild in Zonen)", 6 => "Teilmessung", _ => "andere",
            })));
        if (m.I(TFlash) is { } flash) list.Add(new CheckItem("Blitz", FlashText(flash)));
        if (m.I(TWhiteBalance) is { } wb) list.Add(new CheckItem("Weißabgleich", L.T(wb == 1 ? "manuell eingestellt" : "automatisch")));
        if (m.I(TLight) is { } light && light > 0)
            list.Add(new CheckItem("Lichtquelle", L.T(light switch
            {
                1 => "Tageslicht", 2 => "Leuchtstofflampe", 3 => "Glühlampe / Kunstlicht", 4 => "Blitz", 9 => "Sonnig", 10 => "Bewölkt",
                11 => "Schatten", >= 12 and <= 16 => "Leuchtstofflampe", 17 or 18 or 19 => "Normlicht", >= 20 and <= 23 => "Tageslicht (Normlicht D)",
                24 => "Studio-Kunstlicht", _ => "andere",
            })));
        if (m.I(TScene) is { } sc && sc > 0)
            list.Add(new CheckItem("Motivprogramm", L.T(sc switch { 1 => "Landschaft", 2 => "Porträt", 3 => "Nachtaufnahme", _ => "andere" })));
        if (m.R(TDistance) is { } dist && dist > 0 && dist < 1e6)
            list.Add(new CheckItem("Motivabstand", dist < 1 ? $"{dist * 100:0} cm" : $"{dist:0.##} m"));
        return list;
    }

    private static string FlashText(int v)
    {
        if ((v & 0x20) != 0) return L.T("Gerät hat keinen Blitz");
        var parts = new List<string> { L.T((v & 1) != 0 ? "ausgelöst" : "nicht ausgelöst") };
        switch ((v >> 3) & 3)
        {
            case 1: parts.Add(L.T("Blitz erzwungen")); break;
            case 2: parts.Add(L.T("Blitz ausgeschaltet")); break;
            case 3: parts.Add(L.T("Automatik")); break;
        }
        if ((v & 0x40) != 0) parts.Add(L.T("Rote-Augen-Reduzierung"));
        return string.Join(", ", parts);
    }

    private static string LensSpec(Meta m)
    {
        var d = m.Exif.FirstOrDefault(x => x.ContainsTag(TLensSpec));
        if (d?.GetObject(TLensSpec) is not Rational[] r || r.Length < 4) return null;
        double minF = r[0].ToDouble(), maxF = r[1].ToDouble(), minA = r[2].ToDouble(), maxA = r[3].ToDouble();
        if (minF <= 0) return null;
        var focal = Math.Abs(maxF - minF) < 0.1 ? $"{minF:0.#} mm" : $"{minF:0.#}–{maxF:0.#} mm";
        if (double.IsNaN(minA) || minA <= 0) return focal;
        var ap = Math.Abs(maxA - minA) < 0.05 || double.IsNaN(maxA) ? string.Create(CultureInfo.InvariantCulture, $"f/{minA:0.#}")
            : string.Create(CultureInfo.InvariantCulture, $"f/{minA:0.#}–{maxA:0.#}");
        return $"{focal} · {ap} " + L.T(Math.Abs(maxF - minF) < 0.1 ? "(Festbrennweite)" : "(Zoom)");
    }

    private static bool LivePhoto(Meta m) =>
        m.AllTags.Any(t => t.DirectoryName.Contains("Apple", StringComparison.OrdinalIgnoreCase) && t.Name.Contains("Content Identifier", StringComparison.OrdinalIgnoreCase))
        || m.X("apple-fi:LivePhoto") != null;

    private static List<CheckItem> PrivacyFindings(Meta m, double? lat, double? lon, string camera, string software, DateTime? original)
    {
        var list = new List<CheckItem>();
        if (lat != null) list.Add(new CheckItem("Genauer Aufnahmeort (GPS)", FormatCoord(lat.Value, lon.Value) + " – " + L.T("verrät z. B. die Wohnadresse"), Level.Bad));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in m.AllTags)
        {
            var v = Clean(t.Description);
            if (string.IsNullOrEmpty(v) || v.Length > 120 || v.All(c => c == '0' || c == ' ')) continue;
            string label = null;
            if (t.Name.Contains("Serial", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("Format", StringComparison.OrdinalIgnoreCase))
                label = "Seriennummer";
            else if (Regex.IsMatch(t.Name, @"Owner|Artist|By-line$|^Author|Creator$|Photographer", RegexOptions.IgnoreCase))
                label = "Name des Fotografen / Besitzers";
            if (label == null || !seen.Add(v)) continue;
            list.Add(new CheckItem(label, $"{v} ({t.Name})", Level.Warn));
        }
        foreach (var key in new[] { "dc:creator", "photoshop:AuthorsPosition", "Iptc4xmpCore:CreatorContactInfo/Iptc4xmpCore:CiEmailWork" })
            if (m.X(key) is { } v && seen.Add(v)) list.Add(new CheckItem("Name / Kontakt", v, Level.Warn));
        if (camera != null) list.Add(new CheckItem("Gerät", camera + " – " + L.T("lässt Rückschlüsse auf den Besitzer zu")));
        if (original != null) list.Add(new CheckItem("Aufnahmezeit", original.Value.ToString("g", CultureInfo.CurrentCulture)));
        if (m.Thumb != null) list.Add(new CheckItem("Eingebettetes Vorschaubild", "kann den Bildinhalt vor dem Zuschneiden oder Retuschieren zeigen"));
        if (m.S(TUserComment) is { } uc && uc.Length > 1) list.Add(new CheckItem("Kommentar", uc, Level.Warn));
        if (m.Dirs.Any(d => d.Tags.Any(t => t.Name.Contains("Face", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(t.Description) && t.Description != "0")))
            list.Add(new CheckItem("Gesichtserkennung", "Positionen erkannter Gesichter sind gespeichert"));
        if (m.Xmp.Keys.Any(k => k.Contains("mwg-rs:Regions", StringComparison.OrdinalIgnoreCase) && k.Contains("Name", StringComparison.OrdinalIgnoreCase)))
            list.Add(new CheckItem("Personen markiert", string.Join(", ", m.Xmp.Where(kv => kv.Key.Contains("mwg-rs:Name", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).Distinct()), Level.Warn));
        return list;
    }

    private static CheckItem Verdict(List<CheckItem> privacy, bool gps, int metaCount, bool video)
    {
        if (metaCount == 0)
            return new CheckItem("", "Keine Metadaten gefunden – das Bild wurde vermutlich bereinigt oder über einen Messenger/soziale Netzwerke weitergegeben.", Level.Ok);
        var warn = privacy.Where(p => p.Level >= Level.Warn).Select(p => p.Name).Distinct().ToList();
        if (gps)
            return new CheckItem("", L.F("Achtung: {0} enthält den genauen Aufnahmeort", L.T(video ? "Dieses Video" : "Dieses Bild"))
                + (warn.Count > 1 ? " " + L.F("sowie {0}", string.Join(", ", warn.Skip(1).Select(L.T))) : "") + ".", Level.Bad);
        if (warn.Count > 0)
            return new CheckItem("", L.F("Kein Aufnahmeort, aber persönliche Angaben: {0}.", string.Join(", ", warn.Select(L.T))), Level.Warn);
        return new CheckItem("", L.F("Kein Aufnahmeort und keine persönlichen Angaben – nur technische Daten ({0} Einträge).", metaCount), Level.Ok);
    }

    private static List<CheckItem> ImageItems(Meta m, int w, int h, int orientation, bool isJpeg, byte[] head)
    {
        var list = new List<CheckItem>();
        if (w > 0 && h > 0)
        {
            bool turned = orientation is 5 or 6 or 7 or 8;
            int dw = turned ? h : w, dh = turned ? w : h;
            list.Add(new CheckItem("Abmessungen", L.F("{0} × {1} Pixel · {2}", dw, dh, L.T(dw > dh ? "Querformat" : dw < dh ? "Hochformat" : "quadratisch"))
                + " · " + Ratio(dw, dh)));
            list.Add(new CheckItem("Druckgröße bei 300 dpi", string.Create(CultureInfo.CurrentCulture, $"{dw / 300.0 * 2.54:0.#} × {dh / 300.0 * 2.54:0.#} cm")
                + " – " + L.T("für scharfe Fotoabzüge")));
        }
        if (orientation != 1)
            list.Add(new CheckItem("Ausrichtung", L.T(orientation switch
            {
                2 => "horizontal gespiegelt", 3 => "um 180° gedreht anzeigen (Kamera stand auf dem Kopf)",
                4 => "vertikal gespiegelt", 5 => "gespiegelt und 90° gedreht",
                6 => "um 90° im Uhrzeigersinn drehen (Hochformat)", 7 => "gespiegelt und 90° gedreht",
                8 => "um 90° gegen den Uhrzeigersinn drehen (Hochformat)", _ => "unbekannt",
            }) + " – " + L.T("die Pixel sind gedreht gespeichert, Programme drehen beim Anzeigen")));
        if (m.I(TColorSpace) is { } cs)
            list.Add(new CheckItem("Farbraum", L.T(cs == 1 ? "sRGB (Standard für Bildschirm und Web)" : cs == 65535 ? "nicht kalibriert – meist Adobe RGB oder Display P3, siehe ICC-Profil" : "andere")));
        if (m.Icc != null)
        {
            var desc = m.Icc.Tags.FirstOrDefault(t => t.Name.Contains("Profile Description", StringComparison.OrdinalIgnoreCase))?.Description;
            if (!string.IsNullOrWhiteSpace(desc) && Regex.Match(desc, @"^\d+ \w{4}\((.*)\)$") is { Success: true } loc) desc = loc.Groups[1].Value;   // „1 enUS(Display P3)“
            if (!string.IsNullOrWhiteSpace(desc))
                list.Add(new CheckItem("Farbprofil (ICC)", desc.Trim() + (desc.Contains("P3") ? " – " + L.T("großer Farbraum, typisch für iPhone und moderne Displays") : "")));
        }
        if (m.R(TXRes) is { } dpi && dpi > 0)
        {
            var unit = m.I(TResUnit) == 3 ? "dpcm" : "dpi";
            list.Add(new CheckItem("Auflösungsangabe", $"{dpi:0} {unit} – " + L.T("nur ein Vorschlag für den Druck, ändert die Bildqualität nicht")));
        }
        if (m.Jpeg != null)
        {
            var comp = Clean(m.Jpeg.GetDescription(JpegDirectory.TagCompressionType));
            if (comp != null) list.Add(new CheckItem("JPEG-Kodierung", comp.Contains("Progressive", StringComparison.OrdinalIgnoreCase)
                ? L.T("progressiv (baut sich im Web schrittweise auf)") : L.T("Standard (Baseline)")));
            if (m.Jpeg.TryGetInt32(JpegDirectory.TagDataPrecision, out var bits)) list.Add(new CheckItem("Farbtiefe", L.F("{0} Bit je Farbkanal", bits)));
        }
        if (isJpeg && Quality(head) is { } q) list.Add(new CheckItem("JPEG-Qualität (geschätzt)", L.F("ca. {0} %", q) + " – " + L.T(q >= 90 ? "hoch" : q >= 75 ? "gut" : "stark komprimiert")));
        if (m.Thumb != null) list.Add(new CheckItem("Vorschaubild", "eingebettet (kleine Version für Kamera-Display und Dateimanager)"));
        if (m.Xmp.Keys.Any(k => k.Contains("HDRGainMap", StringComparison.OrdinalIgnoreCase) || k.Contains("hdrgm:", StringComparison.OrdinalIgnoreCase))
            || ContainsAscii(head, "urn:com:apple:photo:2020:aux:hdrgainmap"))
            list.Add(new CheckItem("HDR", "enthält eine HDR-Gain-Map – auf HDR-Displays heller und kontrastreicher"));
        if (ContainsAscii(head, "MPF\0")) list.Add(new CheckItem("Mehrbild-Datei (MPF)", "enthält weitere Bilder, z. B. Tiefenkarte (Porträtmodus) oder HDR-Daten"));
        if (m.Xmp.Keys.Any(k => k.StartsWith("GDepth", StringComparison.OrdinalIgnoreCase) || k.Contains("GCamera:", StringComparison.OrdinalIgnoreCase)))
            list.Add(new CheckItem("Google-Kamera-Daten", "Tiefeninformation / Bewegungsfoto vorhanden"));
        return list;
    }

    private static string Ratio(int w, int h)
    {
        double r = (double)Math.Max(w, h) / Math.Min(w, h);
        (double v, string n)[] known = { (1, "1:1"), (1.25, "5:4"), (4 / 3.0, "4:3"), (1.5, "3:2"), (16 / 9.0, "16:9"), (2, "2:1"), (21 / 9.0, "21:9") };
        var best = known.OrderBy(k => Math.Abs(k.v - r)).First();
        var name = Math.Abs(best.v - r) < 0.02 ? best.n : r.ToString("0.00", CultureInfo.InvariantCulture) + ":1";
        return w >= h ? L.F("Seitenverhältnis {0}", name) : L.F("Seitenverhältnis {0}", string.Join(":", name.Split(':').Reverse()));
    }

    /// <summary>JPEG-Qualität aus der ersten Quantisierungstabelle schätzen (Vergleich mit der IJG-Standardtabelle).</summary>
    private static int? Quality(byte[] d)
    {
        for (int i = 2; i + 4 < d.Length;)
        {
            if (d[i] != 0xFF) return null;
            byte mk = d[i + 1];
            int len = d[i + 2] << 8 | d[i + 3];
            if (mk == 0xDB && i + 4 + 65 <= d.Length && (d[i + 4] >> 4) == 0)
            {
                // Luminanz-Tabelle (Zickzack) – Mittelwert gegen Standard (Mittelwert 57,6) bei Qualität 50
                double sum = 0;
                for (int k = 0; k < 64; k++) sum += d[i + 5 + k];
                double scale = sum / 64 / 57.625 * 100;
                int q = scale <= 100 ? (int)Math.Round((200 - scale) / 2) : (int)Math.Round(5000 / scale);
                return Math.Clamp(q, 1, 100);
            }
            if (mk == 0xDA) return null;
            i += 2 + len;
        }
        return null;
    }

    private static readonly (string Needle, string Hint)[] AiNeedles =
    {
        ("trainedAlgorithmicMedia", "laut Herkunftsangabe mit KI erzeugt (IPTC „trainedAlgorithmicMedia“)"),
        ("compositeWithTrainedAlgorithmicMedia", "laut Herkunftsangabe teilweise mit KI erzeugt"),
        ("algorithmicMedia", "laut Herkunftsangabe künstlich erzeugt"),
        ("Midjourney", "Midjourney erwähnt"), ("DALL-E", "DALL-E (OpenAI) erwähnt"), ("DALL·E", "DALL-E (OpenAI) erwähnt"),
        ("OpenAI", "OpenAI erwähnt"), ("ChatGPT", "ChatGPT erwähnt"), ("Stable Diffusion", "Stable Diffusion erwähnt"),
        ("ComfyUI", "ComfyUI (Stable Diffusion) erwähnt"), ("Adobe Firefly", "Adobe Firefly erwähnt"), ("Firefly", "Adobe Firefly erwähnt"),
        ("Imagen", "Google Imagen erwähnt"), ("Gemini", "Google Gemini erwähnt"), ("Made with AI", "als „Made with AI“ gekennzeichnet"),
        ("Generative Fill", "Photoshop „Generatives Füllen“ verwendet"), ("Bing Image Creator", "Bing Image Creator erwähnt"),
        ("Leonardo.Ai", "Leonardo.Ai erwähnt"), ("Ideogram", "Ideogram erwähnt"), ("Flux", "FLUX-Modell erwähnt"),
    };

    private static List<string> AiHints(Meta m, byte[] head)
    {
        var hints = new List<string>();
        var texts = m.AllTags.Select(t => t.Description ?? "").Concat(m.Xmp.Values).ToList();
        foreach (var (needle, hint) in AiNeedles)
        {
            bool word = needle is "Flux" or "Imagen" or "Gemini" or "Firefly";
            if (texts.Any(t => word ? Regex.IsMatch(t, $@"\b{Regex.Escape(needle)}\b") : t.Contains(needle, StringComparison.OrdinalIgnoreCase)) && !hints.Contains(hint))
                hints.Add(hint);
        }
        // Stable-Diffusion-Parameter (PNG „parameters“: Steps, Sampler, CFG scale, Seed)
        if (texts.Any(t => t.Contains("Steps:") && t.Contains("Sampler:") && t.Contains("Seed:")))
            hints.Add("Stable-Diffusion-Parameter (Prompt, Seed, Sampler) eingebettet");
        if (ContainsAscii(head, "trainedAlgorithmicMedia") && hints.Count == 0)
            hints.Add("laut Herkunftsangabe mit KI erzeugt (IPTC „trainedAlgorithmicMedia“)");
        return hints;
    }

    private static readonly string[] Editors = { "Photoshop", "Lightroom", "GIMP", "Affinity", "Snapseed", "Pixelmator", "Canva", "Luminar", "Capture One", "DxO", "darktable", "RawTherapee", "Paint.NET", "Photos", "Picasa", "Fotor", "VSCO", "Facetune", "PicsArt", "Darkroom" };

    private static List<CheckItem> EditItems(Meta m, string software, DateTime? original, DateTime? modified, List<string> ai, bool c2pa, int metaCount, bool isJpeg, string camera)
    {
        var list = new List<CheckItem>();
        if (software != null)
        {
            var editor = Editors.FirstOrDefault(e => software.Contains(e, StringComparison.OrdinalIgnoreCase));
            list.Add(new CheckItem("Software", software + (editor != null ? " – " + L.T("Bildbearbeitung") : ""), editor != null ? Level.Info : Level.Ok));
        }
        if (m.X("xmp:CreatorTool") is { } tool && tool != software) list.Add(new CheckItem("Erstellt mit (XMP)", tool));
        var history = m.Xmp.Where(kv => kv.Key.Contains("xmpMM:History", StringComparison.OrdinalIgnoreCase) && kv.Key.EndsWith("stEvt:action", StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Value).ToList();
        if (history.Count > 0) list.Add(new CheckItem("Bearbeitungsverlauf", L.F("{0} Schritte ({1})", history.Count, string.Join(", ", history.Distinct().Take(6)))));
        if (m.X("crs:Version", "crs:ProcessVersion") is { } crs) list.Add(new CheckItem("Camera Raw / Lightroom", L.F("mit Adobe Camera Raw entwickelt (Version {0})", crs)));
        if (m.X("photoshop:DocumentAncestors") != null) list.Add(new CheckItem("Photoshop-Montage", "aus anderen Dokumenten zusammengesetzt"));
        if (m.X("xmpMM:DerivedFrom/stRef:documentID", "xmpMM:DerivedFrom/stRef:instanceID") != null) list.Add(new CheckItem("Abgeleitet", "aus einer anderen Datei erzeugt (z. B. Export, Zuschnitt)"));
        foreach (var h in ai) list.Add(new CheckItem("KI-Hinweis", h, Level.Warn));
        if (c2pa) list.Add(new CheckItem("Content Credentials (C2PA)", "signierter Herkunftsnachweis eingebettet – prüfbar auf contentcredentials.org/verify", ai.Count > 0 ? Level.Warn : Level.Info));
        if (m.X("Iptc4xmpExt:DigitalSourceType") is { } dst) list.Add(new CheckItem("Herkunftsart (IPTC)", dst.Split('/').Last()));
        if (metaCount > 0 && camera == null && original == null)
            list.Add(new CheckItem("Kameradaten", "fehlen – Bildschirmfoto, Grafik, KI-Bild, Download oder bereinigter Export", Level.Info));
        else if (original != null && modified == null && software == null)
            list.Add(new CheckItem("Originalzustand", "keine Hinweise auf Bearbeitung – wirkt wie direkt aus der Kamera", Level.Ok));
        return list;
    }

    private static List<CheckItem> AuthorItems(Meta m)
    {
        var list = new List<CheckItem>();
        void Add(string name, string v)
        {
            v = Clean(v);
            if (!string.IsNullOrEmpty(v) && !list.Any(i => i.Value == v)) list.Add(new CheckItem(name, v));
        }
        Add("Fotograf / Künstler", m.S(TArtist));
        Add("Fotograf / Künstler", m.X("dc:creator"));
        Add("Urheberrecht", m.S(TCopyright));
        Add("Urheberrecht", m.X("dc:rights"));
        Add("Titel", m.X("dc:title") ?? m.Iptcs(IptcDirectory.TagObjectName));
        Add("Überschrift", m.X("photoshop:Headline") ?? m.Iptcs(IptcDirectory.TagHeadline));
        Add("Beschreibung", m.S(TDescription));
        Add("Beschreibung", m.X("dc:description") ?? m.Iptcs(IptcDirectory.TagCaption));
        Add("Schlagwörter", m.Iptcs(IptcDirectory.TagKeywords) ?? string.Join(", ", m.Xmp.Where(kv => kv.Key.StartsWith("dc:subject", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value)));
        Add("Ort (IPTC)", string.Join(", ", new[] { m.Iptcs(IptcDirectory.TagSubLocation) ?? m.X("Iptc4xmpCore:Location"), m.Iptcs(IptcDirectory.TagCity) ?? m.X("photoshop:City"),
            m.Iptcs(IptcDirectory.TagCountryOrPrimaryLocationName) ?? m.X("photoshop:Country") }.Where(s => !string.IsNullOrWhiteSpace(s))));
        Add("Quelle / Agentur", m.Iptcs(IptcDirectory.TagCredit) ?? m.X("photoshop:Credit"));
        Add("Nutzungsbedingungen", m.X("xmpRights:UsageTerms"));
        if (m.X("xmp:Rating") is { } rating && int.TryParse(rating, out var stars) && stars > 0)
            Add("Bewertung", new string('★', Math.Min(stars, 5)) + new string('☆', Math.Max(0, 5 - stars)));
        if (m.X("xmp:Label") is { } label) Add("Farbmarkierung", label);
        return list;
    }

    private static bool ContainsAscii(byte[] data, string needle)
    {
        if (data == null || data.Length == 0) return false;
        return data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;
    }

    /* ------------------------------------------------------------------ Adresse (OpenStreetMap) */

    private static async Task<string> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct)
    {
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&lat={lat:0.######}&lon={lon:0.######}&zoom=18&accept-language={(L.IsGerman ? "de" : "en")}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.Clear();
            req.Headers.UserAgent.ParseAdd("Net-Stuff/1.1 (network toolbox)");   // Nominatim verlangt eine App-Kennung
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(8000);
            using var res = await NetUtil.Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("display_name", out var n) ? n.GetString() : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    /* ------------------------------------------------------------------ Metadaten entfernen */

    /// <summary>
    /// Kopie ohne Metadaten (JPEG, PNG). JPEG: EXIF, XMP, IPTC, Kommentare, Herstellerdaten und angehängte Zusatzbilder
    /// werden entfernt; das Farbprofil (ICC) bleibt, die Ausrichtung wird als einziger EXIF-Eintrag neu geschrieben,
    /// damit Hochformatfotos nicht gekippt erscheinen. PNG: Text-, Zeit- und EXIF-Blöcke werden entfernt.
    /// </summary>
    public static byte[] Strip(byte[] data)
    {
        if (data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8) return StripJpeg(data);
        if (data.Length > 8 && data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return StripPng(data);
        throw new InvalidOperationException(L.T("Metadaten entfernen geht nur bei JPEG- und PNG-Dateien."));
    }

    private static byte[] StripJpeg(byte[] d)
    {
        int orientation = 1;
        try
        {
            using var ms = new MemoryStream(d, false);
            var exif = ImageMetadataReader.ReadMetadata(ms).OfType<ExifIfd0Directory>().FirstOrDefault();
            if (exif != null && exif.TryGetInt32(TOrientation, out var o) && o is >= 2 and <= 8) orientation = o;
        }
        catch { }

        using var outp = new MemoryStream(d.Length);
        outp.Write(d, 0, 2);
        bool exifWritten = orientation == 1;
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) throw new InvalidOperationException(L.T("JPEG-Datei ist beschädigt."));
            byte mk = d[i + 1];
            if (mk == 0xFF) { i++; continue; }
            if (mk == 0x01 || mk is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            int len = d[i + 2] << 8 | d[i + 3];
            if (i + 2 + len > d.Length) throw new InvalidOperationException(L.T("JPEG-Datei ist beschädigt."));
            if (!exifWritten && mk != 0xE0)
            {
                WriteExifSegment(outp, new Ifd().Short(TOrientation, (ushort)orientation));
                exifWritten = true;
            }
            if (mk == 0xDA)
            {
                // Bilddaten bis zum Bildende (EOI) – angehängte Zusatzbilder (MPF, Tiefenkarte, eigene EXIF/GPS!) fallen weg
                int end = FindEoi(d, i + 2 + len);
                outp.Write(d, i, end - i);
                return outp.ToArray();
            }
            bool keep = mk switch
            {
                0xE0 => true,                                         // JFIF
                0xE2 => IsIcc(d, i),                                  // Farbprofil behalten, MPF/FlashPix weg
                0xEE => true,                                         // Adobe (Farbtransformation)
                >= 0xE1 and <= 0xEF => false,                         // EXIF, XMP, IPTC, Hersteller, C2PA …
                0xFE => false,                                        // Kommentar
                _ => true,
            };
            if (keep) outp.Write(d, i, 2 + len);
            i += 2 + len;
        }
        throw new InvalidOperationException(L.T("JPEG-Datei ist beschädigt."));
    }

    private static bool IsIcc(byte[] d, int i) => i + 16 < d.Length && Encoding.ASCII.GetString(d, i + 4, 12) == "ICC_PROFILE\0";

    private static int FindEoi(byte[] d, int start)
    {
        for (int j = start; j + 1 < d.Length; j++)
            if (d[j] == 0xFF && d[j + 1] == 0xD9) return j + 2;
        return d.Length;
    }

    private static readonly HashSet<string> PngKeep = new(StringComparer.Ordinal)
    { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs", "hIST", "acTL", "fcTL", "fdAT", "cICP", "mDCv", "cLLi" };

    private static byte[] StripPng(byte[] d)
    {
        using var outp = new MemoryStream(d.Length);
        outp.Write(d, 0, 8);
        int i = 8;
        while (i + 12 <= d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
            if (len < 0 || i + 12 + len > d.Length) throw new InvalidOperationException(L.T("PNG-Datei ist beschädigt."));
            var type = Encoding.ASCII.GetString(d, i + 4, 4);
            if (PngKeep.Contains(type)) outp.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") break;
        }
        return outp.ToArray();
    }

    /* ------------------------------------------------------------------ EXIF schreiben (Beispiel, Ausrichtung) */

    /// <summary>Minimaler TIFF/EXIF-Schreiber (Little Endian) mit verschachtelten IFDs (Exif, GPS).</summary>
    private sealed class Ifd
    {
        public readonly List<(ushort Tag, ushort Type, uint Count, byte[] Data)> Entries = new();
        public readonly Dictionary<ushort, Ifd> Children = new();

        public Ifd Ascii(int tag, string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); Entries.Add(((ushort)tag, 2, (uint)b.Length, b)); return this; }
        public Ifd Short(int tag, ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); Entries.Add(((ushort)tag, 3, 1, b)); return this; }
        public Ifd Long(int tag, uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); Entries.Add(((ushort)tag, 4, 1, b)); return this; }
        public Ifd Bytes(int tag, ushort type, params byte[] v) { Entries.Add(((ushort)tag, type, (uint)v.Length, v)); return this; }
        public Ifd Rational(int tag, params (uint N, uint D)[] v)
        {
            var b = new byte[8 * v.Length];
            for (int k = 0; k < v.Length; k++) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 * k), v[k].N); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 * k + 4), v[k].D); }
            Entries.Add(((ushort)tag, 5, (uint)v.Length, b));
            return this;
        }
        public Ifd SRational(int tag, int n, int dn)
        {
            var b = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(b, n);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), dn);
            Entries.Add(((ushort)tag, 10, 1, b));
            return this;
        }
        public Ifd Child(int pointerTag, Ifd child) { Children[(ushort)pointerTag] = child; return this; }
    }

    private static byte[] BuildTiff(Ifd root)
    {
        var buf = new List<byte> { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 };
        WriteIfd(buf, root);
        return buf.ToArray();
    }

    private static int WriteIfd(List<byte> buf, Ifd ifd)
    {
        var all = ifd.Entries.Select(e => (e.Tag, e.Type, e.Count, e.Data, Child: (Ifd)null))
            .Concat(ifd.Children.Select(c => (c.Key, (ushort)4, 1u, new byte[4], Child: c.Value)))
            .OrderBy(e => e.Item1).ToList();
        int start = buf.Count;
        int dataPos = start + 2 + all.Count * 12 + 4;
        var header = new byte[2 + all.Count * 12 + 4];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)all.Count);
        var data = new List<byte>();
        var childSlots = new List<(int Pos, Ifd Child)>();
        for (int k = 0; k < all.Count; k++)
        {
            var (tag, type, count, bytes, child) = all[k];
            int p = 2 + k * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(p), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(p + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 4), count);
            if (child != null) childSlots.Add((start + p + 8, child));
            else if (bytes.Length <= 4) bytes.CopyTo(header, p + 8);
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 8), (uint)(dataPos + data.Count));
                data.AddRange(bytes);
                if (data.Count % 2 == 1) data.Add(0);
            }
        }
        buf.AddRange(header);
        buf.AddRange(data);
        foreach (var (pos, child) in childSlots)
        {
            int off = WriteIfd(buf, child);
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)off);
            for (int k = 0; k < 4; k++) buf[pos + k] = b[k];
        }
        return start;
    }

    private static void WriteExifSegment(Stream s, Ifd root)
    {
        var tiff = BuildTiff(root);
        int len = 2 + 6 + tiff.Length;
        s.WriteByte(0xFF); s.WriteByte(0xE1); s.WriteByte((byte)(len >> 8)); s.WriteByte((byte)len);
        s.Write(Encoding.ASCII.GetBytes("Exif\0\0"));
        s.Write(tiff);
    }

    private static void WriteXmpSegment(Stream s, string xmp)
    {
        var body = Encoding.UTF8.GetBytes(xmp);
        var ns = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int len = 2 + ns.Length + body.Length;
        s.WriteByte(0xFF); s.WriteByte(0xE1); s.WriteByte((byte)(len >> 8)); s.WriteByte((byte)len);
        s.Write(ns);
        s.Write(body);
    }

    /// <summary>JPEG mit eingefügten EXIF- (und optional XMP-)Daten direkt nach dem JFIF-Block.</summary>
    private static byte[] InsertSegments(byte[] jpeg, Ifd exif, string xmp)
    {
        int insertAt = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) insertAt = 4 + (jpeg[4] << 8 | jpeg[5]);
        using var ms = new MemoryStream();
        ms.Write(jpeg, 0, insertAt);
        WriteExifSegment(ms, exif);
        if (xmp != null) WriteXmpSegment(ms, xmp);
        ms.Write(jpeg, insertAt, jpeg.Length - insertAt);
        return ms.ToArray();
    }

    private static (uint, uint) R(double v, uint den = 10000) => ((uint)Math.Round(v * den), den);

    /// <summary>Testbild für die Selbsttests: JPEG mit wählbarer Ausrichtung und Kamera/GPS-Daten.</summary>
    internal static byte[] TestJpeg(int orientation, bool gps) =>
        InsertSegments(Convert.FromBase64String(SampleJpeg), new Ifd().Ascii(TMake, "Canon").Ascii(TModel, "Canon EOS R6").Short(TOrientation, (ushort)orientation)
            .Child(0x8825, gps ? new Ifd().Bytes(0, 1, 2, 3, 0, 0).Ascii(1, "N").Rational(2, (48, 1), (12, 1), R(30.6, 100)).Ascii(3, "E").Rational(4, (16, 1), (22, 1), R(23.2, 100)) : new Ifd().Bytes(0, 1, 2, 3, 0, 0)), null);

    /// <summary>Beispielfoto mit typischen Metadaten (Kamera, Objektiv, Seriennummer, Fotograf, GPS am Stephansplatz Wien, Lightroom).</summary>
    public static byte[] Sample()
    {
        var gps = new Ifd()
            .Bytes(0, 1, 2, 3, 0, 0)
            .Ascii(1, "N").Rational(2, (48, 1), (12, 1), R(30.6, 100))
            .Ascii(3, "E").Rational(4, (16, 1), (22, 1), R(23.2, 100))
            .Bytes(GAltRef, 1, 0).Rational(GAlt, R(171.4, 10))
            .Rational(7, (16, 1), (42, 1), (7, 1))
            .Ascii(GImgDirRef, "T").Rational(GImgDir, R(247.5, 10))
            .Rational(GHError, R(4.7, 10))
            .Ascii(29, "2026:07:18");
        var exif = new Ifd()
            .Rational(TExposure, (1, 250)).Rational(TFNumber, (40, 10)).Short(TProgram, 3).Short(TIso, 400)
            .Bytes(0x9000, 7, (byte)'0', (byte)'2', (byte)'3', (byte)'2')
            .Ascii(TOriginal, "2026:07:18 18:42:07").Ascii(TDigitized, "2026:07:18 18:42:07").Ascii(TOffsetOriginal, "+02:00").Ascii(TOffset, "+02:00")
            .SRational(TBias, -1, 3).Short(TMetering, 5).Short(TFlash, 16).Rational(TFocal, (70, 1)).Ascii(TSubSec, "48")
            .Short(TColorSpace, 1).Long(TWidth, 240).Long(THeight, 160).Short(TExposureMode, 0).Short(TWhiteBalance, 0).Short(TFocal35, 70).Short(TScene, 0)
            .Ascii(TOwner, "Max Mustermann").Ascii(TBodySerial, "083021004711").Rational(TLensSpec, (24, 1), (105, 1), (4, 1), (4, 1))
            .Ascii(TLensMake, "Canon").Ascii(TLensModel, "RF24-105mm F4 L IS USM").Ascii(TLensSerial, "9204001234");
        var ifd0 = new Ifd()
            .Ascii(TDescription, "Stephansdom am Abend").Ascii(TMake, "Canon").Ascii(TModel, "Canon EOS R6").Short(TOrientation, 1)
            .Rational(TXRes, (300, 1)).Rational(TYRes, (300, 1)).Short(TResUnit, 2)
            .Ascii(TSoftware, "Adobe Lightroom Classic 14.0 (Windows)").Ascii(TDateTime, "2026:07:20 09:15:31")
            .Ascii(TArtist, "Max Mustermann").Ascii(TCopyright, "© 2026 Max Mustermann – alle Rechte vorbehalten")
            .Child(0x8769, exif).Child(0x8825, gps);
        const string xmp = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:photoshop=\"http://ns.adobe.com/photoshop/1.0/\""
            + " xmp:CreatorTool=\"Adobe Lightroom Classic 14.0 (Windows)\" xmp:Rating=\"4\" photoshop:City=\"Wien\" photoshop:Country=\"Österreich\">"
            + "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Stephansdom am Abend</rdf:li></rdf:Alt></dc:title>"
            + "<dc:subject><rdf:Bag><rdf:li>Wien</rdf:li><rdf:li>Architektur</rdf:li><rdf:li>Abendlicht</rdf:li></rdf:Bag></dc:subject>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return InsertSegments(Convert.FromBase64String(SampleJpeg), ifd0, xmp);
    }

    public const string SampleName = "Beispiel-Foto.jpg";

    private const string SampleJpeg = "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";
}
