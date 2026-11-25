using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>Eine gelesene Sitemap-Datei (Index-Dateien verweisen nur auf weitere Sitemaps).</summary>
public sealed record SitemapFile(string Url, int Count, bool IsIndex, string Error = null);

/// <summary>Ein Link aus der Sitemap. <see cref="Display"/> ist die lesbare (dekodierte) Form.</summary>
public sealed record SitemapLink(string Url, string Display, string LastMod, string File);

public sealed class SitemapResult
{
    /// <summary>Wo die Sitemap gefunden wurde (robots.txt oder Standardpfad).</summary>
    public string Source { get; init; } = "";
    public List<SitemapFile> Files { get; } = new();
    /// <summary>Alle Links, ohne Duplikate, alphabetisch sortiert.</summary>
    public List<SitemapLink> Links { get; set; } = new();
    /// <summary>Nicht alle Dateien/Links gelesen (Grenzen erreicht).</summary>
    public bool Truncated { get; set; }
    public bool Found => Files.Any(f => f.Error == null);

    /// <summary>Zusammenfassung: jede Sitemap-Datei mit Anzahl der Links.</summary>
    public CheckSection Summary()
    {
        var items = new List<CheckItem>();
        if (!Found)
        {
            items.Add(new CheckItem("Sitemap", L.F("Keine Sitemap gefunden ({0}).", Source), Level.Info));
            return new CheckSection("Sitemap", items);
        }
        items.Add(new CheckItem("Sitemap", L.F("{0} Links in {1} Datei(en) (gefunden über {2}){3}",
            Links.Count.ToString("N0"), Files.Count(f => f.Error == null && !f.IsIndex), Source,
            Truncated ? " – " + L.T("nicht vollständig gelesen (Grenze erreicht)") : "")));
        foreach (var f in Files)
            items.Add(f.Error != null
                ? new CheckItem(ShortName(f.Url), f.Error, Level.Warn)
                : new CheckItem(ShortName(f.Url), f.IsIndex ? L.F("Index mit {0} Sitemaps", f.Count) : f.Count == 1 ? L.T("1 Link") : L.F("{0} Links", f.Count.ToString("N0")), Level.Info));
        return new CheckSection(L.F("Sitemap ({0} Links)", Links.Count.ToString("N0")), items,
            "Die Sitemap zeigt, welche Seiten, Produkte und Kategorien die Website öffentlich für Suchmaschinen auflistet.");
    }

    /// <summary>Text für die Zwischenablage: alle Links, eine Zeile je Link.</summary>
    public string LinksText() =>
        L.F("Links aus der Sitemap ({0})", Links.Count) + "\n" + string.Concat(Links.Select(l => "  " + l.Url + "\n"));

    private static string ShortName(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.PathAndQuery : url;
}

/// <summary>
/// Liest die Sitemap einer Website: Fundstellen aus robots.txt, sonst die üblichen Pfade (/sitemap.xml,
/// /sitemap_index.xml, /wp-sitemap.xml). Sitemap-Indizes werden aufgelöst, .xml.gz wird entpackt.
/// Nur lesende GET-Anfragen, mit Obergrenzen für Dateien und Links.
/// </summary>
public static class SitemapReader
{
    public const int MaxFiles = 60;
    public const int MaxLinks = 100_000;

    private static readonly string[] DefaultPaths = { "/sitemap.xml", "/sitemap_index.xml", "/wp-sitemap.xml", "/sitemap.xml.gz" };

    public static async Task<SitemapResult> ReadAsync(string siteUrl, CancellationToken ct = default)
    {
        var origin = new Uri(siteUrl).GetLeftPart(UriPartial.Authority);
        using var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            MaxAutomaticRedirections = 5,
        })
        { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);

        // 1. robots.txt
        var start = new List<string>();
        var robots = await GetBytesAsync(http, origin + "/robots.txt", ct);
        if (robots.Bytes != null)
        {
            var text = Encoding.UTF8.GetString(robots.Bytes);
            start.AddRange(Regex.Matches(text, @"^\s*Sitemap:\s*(\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)
                .Select(m => m.Groups[1].Value.Trim())
                .Select(u => u.StartsWith("/") ? origin + u : u)
                .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10));
        }

        var result = new SitemapResult { Source = start.Count > 0 ? "robots.txt" : L.T("Standardpfade") };
        var links = new Dictionary<string, SitemapLink>(StringComparer.Ordinal);

        // 2. ohne robots.txt-Eintrag: übliche Pfade der Reihe nach, bis einer eine Sitemap liefert
        if (start.Count == 0)
        {
            foreach (var p in DefaultPaths)
            {
                var text = await GetSitemapTextAsync(http, origin + p, ct);
                if (text.Text == null) continue;
                result = new SitemapResult { Source = p };
                var queue = new Queue<string>();
                Process(result, links, origin + p, text.Text, queue);
                await DrainAsync(http, result, links, ct, queue);   // Dateien aus einem Index nachladen
                break;
            }
        }
        else
        {
            await DrainAsync(http, result, links, ct, new Queue<string>(start));
        }

        result.Links = links.Values
            .OrderBy(l => l.Display, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(l => l.Url, StringComparer.Ordinal)
            .ToList();
        return result;
    }

    private static async Task DrainAsync(HttpClient http, SitemapResult result, Dictionary<string, SitemapLink> links,
        CancellationToken ct, Queue<string> queue)
    {
        var seen = new HashSet<string>(result.Files.Select(f => f.Url), StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            if (result.Files.Count >= MaxFiles || links.Count >= MaxLinks) { result.Truncated = true; break; }
            var u = queue.Dequeue();
            if (!seen.Add(u)) continue;
            var t = await GetSitemapTextAsync(http, u, ct);
            if (t.Text == null)
            {
                result.Files.Add(new SitemapFile(u, 0, false, t.Error));
                continue;
            }
            Process(result, links, u, t.Text, queue);
        }
    }

    private static bool IsIndex(string text) => text.Contains("<sitemapindex", StringComparison.OrdinalIgnoreCase);

    private static void Process(SitemapResult result, Dictionary<string, SitemapLink> links, string fileUrl, string text, Queue<string> queue)
    {
        var locs = Locs(text);
        if (IsIndex(text))
        {
            result.Files.Add(new SitemapFile(fileUrl, locs.Count, true));
            foreach (var l in locs) queue.Enqueue(l.Loc);
            return;
        }
        foreach (var (loc, lastMod) in locs)
        {
            if (links.Count >= MaxLinks) { result.Truncated = true; break; }
            links.TryAdd(loc, new SitemapLink(loc, Readable(loc), lastMod, fileUrl));
        }
        result.Files.Add(new SitemapFile(fileUrl, locs.Count, false));
    }

    /// <summary>Alle &lt;loc&gt; (mit &lt;lastmod&gt;, falls vorhanden) einer Sitemap.</summary>
    internal static List<(string Loc, string LastMod)> Locs(string text)
    {
        var list = new List<(string, string)>();
        foreach (Match m in Regex.Matches(text, @"<(?:url|sitemap)\b[^>]*>(.*?)</(?:url|sitemap)>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var body = m.Groups[1].Value;
            var loc = Regex.Match(body, @"<loc>\s*(?:<!\[CDATA\[)?\s*([^<\s\]]+)", RegexOptions.IgnoreCase);
            if (!loc.Success) continue;
            var mod = Regex.Match(body, @"<lastmod>\s*([^<\s]+)", RegexOptions.IgnoreCase);
            var lastMod = mod.Success ? mod.Groups[1].Value : "";
            if (lastMod.Length >= 10 && DateTime.TryParse(lastMod, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
                lastMod = d.ToString("yyyy-MM-dd");
            list.Add((WebUtility.HtmlDecode(loc.Groups[1].Value), lastMod));
        }
        return list;
    }

    /// <summary>Lesbare Form eines Links: %-Kodierung (z. B. Umlaute) aufgelöst.</summary>
    private static string Readable(string url)
    {
        try { return Uri.UnescapeDataString(url); }
        catch { return url; }
    }

    private static async Task<(string Text, string Error)> GetSitemapTextAsync(HttpClient http, string url, CancellationToken ct)
    {
        var r = await GetBytesAsync(http, url, ct);
        if (r.Bytes == null) return (null, r.Error);
        var bytes = r.Bytes;
        try
        {
            if (bytes.Length > 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                await gz.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
        }
        catch { return (null, L.T("Datei lässt sich nicht entpacken")); }
        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains("<urlset", StringComparison.OrdinalIgnoreCase) || IsIndex(text)
            ? (text, null)
            : (null, L.T("keine gültige Sitemap (kein XML)"));
    }

    private static async Task<(byte[] Bytes, string Error)> GetBytesAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(15000);
            using var res = await http.GetAsync(url, cts.Token);
            if (!res.IsSuccessStatusCode) return (null, $"HTTP {(int)res.StatusCode}");
            var bytes = await res.Content.ReadAsByteArrayAsync(cts.Token);
            return bytes.Length > 50_000_000 ? (null, L.T("Datei zu groß")) : (bytes, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return (null, NetUtil.ErrorText(ex));
        }
    }
}
