using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>Wie mit fremden Domains (nicht Start-Domain) umgegangen wird.</summary>
public enum ForeignMode { Ask, AutoResources, Never }

public class SiteCopyOptions
{
    public int MaxDepth { get; set; } = 5;
    public int MaxFiles { get; set; } = 5000;          // 0 = unbegrenzt
    public long MaxFileBytes { get; set; } = 100L << 20; // 0 = unbegrenzt
    public int Parallel { get; set; } = 4;
    public int DelayMs { get; set; }
    public bool IncludeSubdomains { get; set; } = true;
    public bool StayBelowStartPath { get; set; }
    public bool RespectRobots { get; set; } = true;
    public ForeignMode Foreign { get; set; } = ForeignMode.Ask;
    public HashSet<string> ExcludeExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum HostDecision { Pending, Allowed, Denied }

/// <summary>Eine fremde Domain, die auf der Website referenziert wird.</summary>
public class ForeignHost : Bindable
{
    private int _resources, _pages;
    private HostDecision _decision;
    public string Host { get; init; }
    public int Resources { get => _resources; set { if (Set(ref _resources, value)) Raise(nameof(Summary)); } }
    public int Pages { get => _pages; set { if (Set(ref _pages, value)) { Raise(nameof(Summary)); Raise(nameof(DecisionText)); } } }
    public HostDecision Decision
    {
        get => _decision;
        set { if (Set(ref _decision, value)) { Raise(nameof(DecisionText)); Raise(nameof(IsPending)); } }
    }
    public bool IsPending => Decision == HostDecision.Pending;
    /// <summary>Ressourcen werden automatisch geladen, nur Seiten werden nachgefragt.</summary>
    public bool AutoResources { get; init; }
    public string Summary => L.F("{0} Ressourcen, {1} Seiten", Resources, Pages);
    public string DecisionText => Decision switch
    {
        HostDecision.Allowed => L.T("erlaubt"),
        HostDecision.Denied => L.T("abgelehnt"),
        _ when AutoResources && Pages == 0 => L.T("Ressourcen automatisch"),
        _ => L.T("Entscheidung offen"),
    };
}

public class CopyLogEntry
{
    public string Status { get; init; }
    public string Kind { get; init; }
    public string Size { get; init; }
    public string Url { get; init; }
    public string LocalFile { get; init; }
    public bool IsError { get; init; }
}

/// <summary>
/// Website-Kopierer (ähnlich HTTrack): lädt eine Website rekursiv herunter, bildet die Verzeichnisstruktur
/// des Webservers lokal nach (ein Ordner pro Host) und schreibt Links in HTML und CSS auf relative lokale Pfade um.
/// Phase 1 lädt alles herunter, Phase 2 schreibt die Links um – so sind alle lokalen Dateinamen bekannt.
/// </summary>
public class SiteCopier
{
    private enum LinkKind { Page, Resource, None }

    private record Item(Uri Url, int Depth, bool IsPage, bool FromForeign);

    private record SavedDoc(string LocalPath, Uri BaseUrl, Encoding Encoding, bool IsHtml);

    private readonly SiteCopyOptions _o;
    private readonly string _root;
    private readonly object _lock = new();
    private readonly Queue<Item> _queue = new();                                     // Ressourcen (Vorrang)
    private readonly Queue<Item> _pageQueue = new();                                 // Seiten
    private readonly HashSet<string> _seen = new();                                  // Schlüssel bereits eingereiht
    private readonly Dictionary<string, string> _local = new();                      // Schlüssel → lokale Datei (absolut)
    private readonly Dictionary<string, string> _localOwner = new(StringComparer.OrdinalIgnoreCase); // lokale Datei → Schlüssel
    private readonly Dictionary<string, ForeignHost> _foreign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Item>> _parked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<RobotsRules>> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentBag<SavedDoc> _docs = new();
    private readonly HashSet<string> _ownHosts = new(StringComparer.OrdinalIgnoreCase);
    private string _ownDomain;
    private string _startPathPrefix = "/";
    private TaskCompletionSource _wake = NewTcs();
    private int _active, _started, _taken;

    /// <summary>Die maximale Dateianzahl wurde erreicht, es gab noch weitere Dateien.</summary>
    public bool LimitReached { get; private set; }
    private bool _finished;
    private HttpClient _http;

    public SiteCopier(SiteCopyOptions options, string rootFolder)
    {
        _o = options;
        _root = Path.GetFullPath(rootFolder);
    }

    /* ---------------------------------------------------------------- Ereignisse (Hintergrund-Threads) */

    public event Action<CopyLogEntry> FileProcessed;
    public event Action<ForeignHost> ForeignHostFound;
    /// <summary>true = Download wartet auf Entscheidungen zu fremden Domains.</summary>
    public event Action<bool> WaitingForDecision;
    public event Action<string> Phase;

    /* ---------------------------------------------------------------- Statistik */

    public int FilesDone;
    public int PagesDone;
    public int Errors;
    public long BytesDone;
    public int QueueCount { get { lock (_lock) return _queue.Count + _pageQueue.Count; } }
    public string StartFile { get; private set; }
    public string RootIndex => Path.Combine(_root, "index.html");

    /* ---------------------------------------------------------------- Steuerung */

    /// <summary>Entscheidung des Benutzers zu einer fremden Domain.</summary>
    public void Decide(string host, bool allow)
    {
        List<Item> parked = null;
        lock (_lock)
        {
            if (!_foreign.TryGetValue(host, out var fh) || fh.Decision != HostDecision.Pending) return;
            fh.Decision = allow ? HostDecision.Allowed : HostDecision.Denied;
            if (_parked.Remove(host, out parked) && allow)
                foreach (var it in parked) EnqueueLocked(it);
        }
        Pulse();
    }

    public async Task RunAsync(Uri start, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(_root);

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = Math.Max(1, _o.Parallel),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-AT,de;q=0.9,en;q=0.8");
        _http = http;

        AddOwnHost(start.Host);
        var path = start.AbsolutePath;
        _startPathPrefix = path.EndsWith('/') ? path : path[..(path.LastIndexOf('/') + 1)];

        Phase?.Invoke(L.T("Lade Website …"));
        lock (_lock) EnqueueLocked(new Item(Strip(start), 0, true, false));

        var workers = Enumerable.Range(0, Math.Max(1, _o.Parallel)).Select(_ => Task.Run(() => WorkerAsync(ct))).ToArray();
        try { await Task.WhenAll(workers); }
        finally
        {
            // Auch nach Abbruch die bisher geladenen Dateien offline nutzbar machen
            Phase?.Invoke(L.T("Links werden umgeschrieben …"));
            await Task.Run(RewriteAll);
            WriteRootIndex(start);
        }
    }

    /* ---------------------------------------------------------------- Warteschlange */

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Pulse()
    {
        TaskCompletionSource old;
        lock (_lock) { old = _wake; _wake = NewTcs(); }
        old.TrySetResult();
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        bool reportedWaiting = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Item item = null;
            Task wait = null;
            bool waitingForUser = false;
            lock (_lock)
            {
                if (_finished) return;
                if (_o.MaxFiles > 0 && _taken >= _o.MaxFiles)
                {
                    // Limit erreicht: Rest verwerfen (Links darauf bleiben online)
                    LimitReached = LimitReached || _queue.Count + _pageQueue.Count + _parked.Count > 0;
                    _queue.Clear();
                    _pageQueue.Clear();
                    _parked.Clear();
                }
                if (_queue.Count > 0 || _pageQueue.Count > 0) { item = _queue.Count > 0 ? _queue.Dequeue() : _pageQueue.Dequeue(); _active++; _taken++; }
                else if (_active == 0 && !_parked.Any(p => _foreign[p.Key].Decision == HostDecision.Pending))
                {
                    _finished = true;
                }
                else
                {
                    waitingForUser = _active == 0;
                    wait = _wake.Task;
                }
            }
            if (item == null && wait == null) { Pulse(); return; }

            if (wait != null)
            {
                if (waitingForUser != reportedWaiting) { reportedWaiting = waitingForUser; WaitingForDecision?.Invoke(waitingForUser); }
                await wait.WaitAsync(ct);
                continue;
            }
            if (reportedWaiting) { reportedWaiting = false; WaitingForDecision?.Invoke(false); }

            try
            {
                await ProcessAsync(item, ct);
                if (_o.DelayMs > 0) await Task.Delay(_o.DelayMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref Errors);
                FileProcessed?.Invoke(new CopyLogEntry { Status = L.T("Fehler"), Kind = "", Size = "", Url = item.Url.ToString(), LocalFile = Short(ex), IsError = true });
            }
            finally
            {
                lock (_lock) _active--;
                Pulse();
            }
        }
    }

    private static string Short(Exception ex)
    {
        var e = ex;
        while (e.InnerException != null) e = e.InnerException;
        return e.Message;
    }

    /// <summary>Unter Sperre aufrufen.</summary>
    private void EnqueueLocked(Item it)
    {
        var key = Key(it.Url);
        if (_seen.Contains(key)) return;
        if (_o.MaxFiles > 0 && _seen.Count >= _o.MaxFiles * 20L) return; // Speicher begrenzen
        _seen.Add(key);
        (it.IsPage ? _pageQueue : _queue).Enqueue(it);
    }

    /// <summary>Gefundenen Link prüfen und ggf. einreihen (oder für eine Rückfrage zurückstellen).</summary>
    private void Consider(Uri url, LinkKind kind, Item parent)
    {
        if (kind == LinkKind.None || url.Scheme is not ("http" or "https")) return;
        url = Strip(url);
        var ext = Path.GetExtension(url.AbsolutePath).TrimStart('.');
        if (ext != "" && _o.ExcludeExtensions.Contains(ext)) return;

        bool isPage = kind == LinkKind.Page;
        bool own = IsOwn(url.Host);
        if (isPage)
        {
            if (parent.FromForeign) return;                    // fremde Seiten nicht weiterverfolgen
            if (parent.Depth + 1 > _o.MaxDepth) return;
            if (own && _o.StayBelowStartPath && !url.AbsolutePath.StartsWith(_startPathPrefix, StringComparison.OrdinalIgnoreCase)) return;
        }
        var item = new Item(url, isPage ? parent.Depth + 1 : parent.Depth, isPage, !own || parent.FromForeign);

        ForeignHost found = null;
        lock (_lock)
        {
            if (_seen.Contains(Key(url))) return;
            if (own) { EnqueueLocked(item); }
            else
            {
                if (!_foreign.TryGetValue(url.Host, out var fh))
                {
                    fh = new ForeignHost { Host = url.Host, AutoResources = _o.Foreign == ForeignMode.AutoResources };
                    _foreign[url.Host] = fh;
                    found = fh;
                    if (_o.Foreign == ForeignMode.Never) fh.Decision = HostDecision.Denied;
                }
                // pro Host & URL nur einmal zählen
                var parked = _parked.TryGetValue(url.Host, out var pl) ? pl : null;
                if (fh.Decision == HostDecision.Pending && _o.Foreign == ForeignMode.AutoResources && !isPage)
                {
                    EnqueueLocked(item); // Ressourcen automatisch, Seiten weiter nachfragen
                    if (_seen.Contains(Key(url))) fh.Resources++;
                }
                else if (fh.Decision == HostDecision.Allowed)
                {
                    EnqueueLocked(item);
                    if (isPage) fh.Pages++; else fh.Resources++;
                }
                else if (fh.Decision == HostDecision.Pending)
                {
                    parked ??= _parked[url.Host] = new List<Item>();
                    if (!parked.Any(p => Key(p.Url) == Key(url)))
                    {
                        parked.Add(item);
                        if (isPage) fh.Pages++; else fh.Resources++;
                    }
                }
            }
        }
        if (found != null) ForeignHostFound?.Invoke(found);
        Pulse();
    }

    private void AddOwnHost(string host)
    {
        lock (_lock)
        {
            _ownHosts.Add(host);
            var bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            _ownHosts.Add(bare);
            _ownHosts.Add("www." + bare);
            _ownDomain ??= bare;
        }
    }

    private bool IsOwn(string host)
    {
        lock (_lock)
        {
            if (_ownHosts.Contains(host)) return true;
            return _o.IncludeSubdomains && _ownDomain != null && host.EndsWith("." + _ownDomain, StringComparison.OrdinalIgnoreCase);
        }
    }

    /* ---------------------------------------------------------------- Download */

    private async Task ProcessAsync(Item item, CancellationToken ct)
    {
        if (_o.RespectRobots && item.IsPage && !item.FromForeign && Interlocked.CompareExchange(ref _started, 0, 0) > 0)
        {
            var rules = await _robots.GetOrAdd(item.Url.Scheme + "://" + item.Url.Authority, _ => LoadRobotsAsync(item.Url, ct));
            if (!rules.Allows(item.Url.PathAndQuery))
            {
                FileProcessed?.Invoke(new CopyLogEntry { Status = L.T("robots.txt"), Kind = L.T("Seite"), Size = "", Url = item.Url.ToString(), LocalFile = L.T("durch robots.txt gesperrt") });
                return;
            }
        }
        bool isStart = Interlocked.Increment(ref _started) == 1;

        using var resp = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        var final = resp.RequestMessage?.RequestUri ?? item.Url;

        if (isStart)
        {
            // Weiterleitung der Start-URL (z. B. http → https, example.at → www.example.at) übernehmen
            AddOwnHost(final.Host);
            var p = final.AbsolutePath;
            _startPathPrefix = p.EndsWith('/') ? p : p[..(p.LastIndexOf('/') + 1)];
        }
        else if (!item.FromForeign && !IsOwn(final.Host))
        {
            FileProcessed?.Invoke(new CopyLogEntry { Status = L.T("übersprungen"), Kind = "", Size = "", Url = item.Url.ToString(), LocalFile = L.F("Weiterleitung auf fremde Domain {0}", final.Host) });
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref Errors);
            FileProcessed?.Invoke(new CopyLogEntry { Status = $"HTTP {(int)resp.StatusCode}", Kind = "", Size = "", Url = item.Url.ToString(), LocalFile = resp.ReasonPhrase, IsError = true });
            return;
        }

        var len = resp.Content.Headers.ContentLength;
        if (_o.MaxFileBytes > 0 && len > _o.MaxFileBytes)
        {
            FileProcessed?.Invoke(new CopyLogEntry { Status = L.T("übersprungen"), Kind = "", Size = FormatSize(len.Value), Url = item.Url.ToString(), LocalFile = L.T("größer als die maximale Dateigröße") });
            return;
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        bool isHtml = mediaType is "text/html" or "application/xhtml+xml";
        bool isCss = mediaType == "text/css" || (mediaType is "" or "text/plain" && final.AbsolutePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase));

        // Zielpfad festlegen (auch für die ursprüngliche URL bei Weiterleitungen)
        string localPath;
        lock (_lock)
        {
            var finalKey = Key(final);
            if (_local.TryGetValue(finalKey, out var existing))
            {
                // Ziel wurde bereits (über eine andere URL/Weiterleitung) geladen
                _local[Key(item.Url)] = existing;
                return;
            }
            localPath = AssignLocalPathLocked(final, mediaType, isHtml);
            _local[finalKey] = localPath;
            _local[Key(item.Url)] = localPath;
            _seen.Add(finalKey);
            if (isStart) StartFile = localPath;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        long size;
        byte[] textBytes = null;
        try
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            if (isHtml || isCss)
            {
                using var ms = new MemoryStream();
                await CopyLimitedAsync(src, ms, ct);
                textBytes = ms.ToArray();
                await File.WriteAllBytesAsync(localPath, textBytes, ct);
                size = textBytes.Length;
            }
            else
            {
                await using var fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                size = await CopyLimitedAsync(src, fs, ct);
            }
        }
        catch
        {
            // Unvollständige Datei verwerfen, Link bleibt online
            try { File.Delete(localPath); } catch { }
            lock (_lock)
            {
                foreach (var k in _local.Where(kv => kv.Value == localPath).Select(kv => kv.Key).ToList()) _local.Remove(k);
                if (StartFile == localPath) StartFile = null;
            }
            throw;
        }

        Interlocked.Increment(ref FilesDone);
        Interlocked.Add(ref BytesDone, size);
        if (isHtml) Interlocked.Increment(ref PagesDone);
        FileProcessed?.Invoke(new CopyLogEntry
        {
            Status = "OK",
            Kind = isHtml ? L.T("Seite") : KindOf(mediaType, localPath),
            Size = FormatSize(size),
            Url = item.Url.ToString(),
            LocalFile = Path.GetRelativePath(_root, localPath),
        });

        if (textBytes == null) return;
        var enc = DetectEncoding(textBytes, resp.Content.Headers.ContentType?.CharSet, isHtml);
        var text = enc.GetString(textBytes);
        var baseUrl = isHtml ? HtmlBase(text, final) : final;
        _docs.Add(new SavedDoc(localPath, baseUrl, enc, isHtml));

        var thisItem = item with { Url = final };
        if (isHtml) RewriteHtml(text, baseUrl, (u, k) => { if (u != null) Consider(u, k, thisItem); return null; });
        else RewriteCss(text, baseUrl, u => { Consider(u, LinkKind.Resource, thisItem); return null; });
    }

    private async Task<long> CopyLimitedAsync(Stream src, Stream dst, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            total += n;
            if (_o.MaxFileBytes > 0 && total > _o.MaxFileBytes) throw new Exception(L.T("größer als die maximale Dateigröße"));
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
        }
        return total;
    }

    private static string KindOf(string mediaType, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (mediaType == "text/css" || ext == ".css") return "CSS";
        if (mediaType.Contains("javascript") || ext is ".js" or ".mjs") return "JavaScript";
        if (mediaType.StartsWith("image/")) return L.T("Bild");
        if (mediaType.StartsWith("font/") || ext is ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot") return L.T("Schrift");
        if (mediaType.StartsWith("video/") || mediaType.StartsWith("audio/")) return L.T("Medien");
        if (mediaType == "application/pdf") return "PDF";
        return L.T("Datei");
    }

    public static string FormatSize(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";

    /* ---------------------------------------------------------------- URL → lokale Datei */

    /// <summary>Schlüssel für Duplikaterkennung: Host + Pfad + Query (ohne Schema und Fragment).</summary>
    public static string Key(Uri u) => (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port).ToLowerInvariant() + u.PathAndQuery;

    private static Uri Strip(Uri u) => u.Fragment == "" ? u : new UriBuilder(u) { Fragment = "" }.Uri;

    private static readonly HashSet<string> HtmlExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml", ".shtml" };
    private static readonly HashSet<string> ScriptExt = new(StringComparer.OrdinalIgnoreCase) { ".php", ".asp", ".aspx", ".jsp", ".cgi", ".pl", ".cfm", ".ashx", ".axd", ".do" };
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly Dictionary<string, string> MediaExt = new()
    {
        ["text/css"] = ".css", ["text/javascript"] = ".js", ["application/javascript"] = ".js", ["application/x-javascript"] = ".js",
        ["application/json"] = ".json", ["application/xml"] = ".xml", ["text/xml"] = ".xml", ["text/plain"] = ".txt",
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/gif"] = ".gif", ["image/webp"] = ".webp", ["image/avif"] = ".avif",
        ["image/svg+xml"] = ".svg", ["image/x-icon"] = ".ico", ["image/vnd.microsoft.icon"] = ".ico", ["image/bmp"] = ".bmp",
        ["font/woff"] = ".woff", ["font/woff2"] = ".woff2", ["font/ttf"] = ".ttf", ["font/otf"] = ".otf",
        ["application/font-woff"] = ".woff", ["application/font-woff2"] = ".woff2", ["application/vnd.ms-fontobject"] = ".eot",
        ["application/pdf"] = ".pdf", ["application/zip"] = ".zip", ["video/mp4"] = ".mp4", ["video/webm"] = ".webm",
        ["audio/mpeg"] = ".mp3", ["application/manifest+json"] = ".webmanifest", ["application/rss+xml"] = ".xml",
    };

    /// <summary>Lokalen Pfad für eine URL bestimmen (Ordnerstruktur wie am Webserver, ein Ordner pro Host).</summary>
    public static string MapPath(Uri u, string mediaType, bool isHtml)
    {
        var host = SafeSegment(u.IsDefaultPort ? u.Host.ToLowerInvariant() : $"{u.Host.ToLowerInvariant()}_{u.Port}");
        var segs = u.AbsolutePath.Split('/', StringSplitOptions.None).Skip(1).Select(s => Uri.UnescapeDataString(s)).ToList();
        string file = segs.Count > 0 ? segs[^1] : "";
        var dirs = segs.Take(Math.Max(0, segs.Count - 1)).Where(s => s != "" && s != "." && s != "..").Select(SafeSegment).ToList();

        string stem, ext;
        if (file == "") { stem = "index"; ext = isHtml ? ".html" : MediaExt.GetValueOrDefault(mediaType ?? "", ""); }
        else
        {
            ext = Path.GetExtension(file);
            stem = ext == "" ? file : file[..^ext.Length];
            if (ext.Length > 10 || !ext.Skip(1).All(char.IsAsciiLetterOrDigit) || !ext.Any(char.IsAsciiLetter)) { stem = file; ext = ""; }
            if (isHtml)
            {
                if (!HtmlExt.Contains(ext)) ext = ".html";
            }
            else if (ext == "" || ScriptExt.Contains(ext))
            {
                // z. B. style.php (text/css) → style.css, damit der Browser offline den richtigen Typ erkennt
                ext = MediaExt.GetValueOrDefault(mediaType ?? "", ext);
            }
        }
        if (u.Query.Length > 1) stem += "_" + Hash(u.Query);
        var name = SafeSegment(stem) + ext;
        return Path.Combine(new[] { host }.Concat(dirs).Append(name).ToArray());
    }

    private string AssignLocalPathLocked(Uri u, string mediaType, bool isHtml)
    {
        var rel = MapPath(u, mediaType, isHtml);
        var full = Path.Combine(_root, rel);
        var key = Key(u);
        if (_localOwner.TryGetValue(full, out var owner) && owner != key)
        {
            var ext = Path.GetExtension(full);
            full = full[..^ext.Length] + "_" + Hash(key) + ext;
        }
        _localOwner[full] = key;
        return full;
    }

    private static string SafeSegment(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c < 32 || "<>:\"|?*\\/".Contains(c) ? '_' : c);
        var r = sb.ToString().TrimEnd('.', ' ');
        if (r == "") r = "_";
        if (ReservedNames.Contains(r.Split('.')[0])) r = "_" + r;
        if (r.Length > 100) r = r[..90] + "_" + Hash(r);
        return r;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    /* ---------------------------------------------------------------- HTML / CSS verarbeiten */

    private static readonly Regex TagRx = new(@"<(?<name>[a-zA-Z][a-zA-Z0-9:-]*)(?<attrs>(?:\s+[^\s=>/]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>",
        RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"(?<pre>\s+)(?<name>[^\s=>/]+)(?:(?<eq>\s*=\s*)(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+)))?",
        RegexOptions.Compiled);
    private static readonly Regex StyleBlockRx = new(@"(<style\b[^>]*>)(.*?)(</style\s*>)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex SkipBlockRx = new(@"<!--.*?-->|<script\b[^>]*>.*?</script\s*>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex CssUrlRx = new(@"url\(\s*(?<q>['""]?)(?<u>[^'"")]*?)\k<q>\s*\)|@import\s+(?<q2>['""])(?<u2>[^'""]+)\k<q2>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex BaseRx = new(@"<base\b[^>]*\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> UrlAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "data-src", "data-lazy-src", "data-original", "data-bg", "data-background", "poster", "background", "data", "action",
    };
    private static readonly HashSet<string> SrcsetAttrs = new(StringComparer.OrdinalIgnoreCase) { "srcset", "data-srcset", "data-lazy-srcset" };

    private static Uri HtmlBase(string html, Uri docUrl)
    {
        var m = BaseRx.Match(html);
        if (!m.Success) return docUrl;
        var v = WebUtility.HtmlDecode(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
        return Uri.TryCreate(docUrl, v, out var b) ? b : docUrl;
    }

    private static LinkKind Classify(string tag, string attr, string rel)
    {
        tag = tag.ToLowerInvariant();
        attr = attr.ToLowerInvariant();
        switch (tag)
        {
            case "a" or "area":
                return attr == "href" ? LinkKind.Page : LinkKind.None;
            case "iframe" or "frame":
                return attr == "src" ? LinkKind.Page : LinkKind.Resource;
            case "form":
                return LinkKind.None; // Formularziele nicht laden, aber umschreiben falls vorhanden
            case "link":
                rel = rel.ToLowerInvariant();
                if (rel.Contains("stylesheet") || rel.Contains("icon") || rel.Contains("preload") || rel.Contains("manifest") || rel.Contains("image_src"))
                    return LinkKind.Resource;
                return LinkKind.None;
            case "base" or "meta":
                return LinkKind.None;
            default:
                return LinkKind.Resource;
        }
    }

    /// <summary>
    /// Durchläuft alle URLs eines HTML-Dokuments. <paramref name="map"/> liefert die neue URL (oder null = unverändert).
    /// Wird in Phase 1 zum Sammeln und in Phase 2 zum Umschreiben verwendet.
    /// </summary>
    private static string RewriteHtml(string html, Uri baseUrl, Func<Uri, LinkKind, string> map)
    {
        // Kommentare und Skripte schützen (Inhalt bleibt unverändert, aber <script src> wird behandelt)
        var protectedBlocks = new List<string>();
        html = SkipBlockRx.Replace(html, m =>
        {
            var text = m.Value;
            if (text.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
            {
                int end = text.IndexOf('>');
                var open = RewriteTags(text[..(end + 1)], baseUrl, map);
                text = open + text[(end + 1)..];
            }
            protectedBlocks.Add(text);
            return $"\u0001{protectedBlocks.Count - 1}\u0002";
        });

        html = StyleBlockRx.Replace(html, m => m.Groups[1].Value + RewriteCss(m.Groups[2].Value, baseUrl, u => map(u, LinkKind.Resource)) + m.Groups[3].Value);
        html = RewriteTags(html, baseUrl, map);
        return Regex.Replace(html, "\u0001(\\d+)\u0002", m => protectedBlocks[int.Parse(m.Groups[1].Value)]);
    }

    private static string RewriteTags(string html, Uri baseUrl, Func<Uri, LinkKind, string> map)
    {
        return TagRx.Replace(html, tm =>
        {
            var tag = tm.Groups["name"].Value;
            var attrs = tm.Groups["attrs"].Value;
            if (attrs.Length == 0) return tm.Value;
            string rel = Regex.Match(attrs, @"\brel\s*=\s*[""']?([^""'>]*)", RegexOptions.IgnoreCase).Groups[1].Value;
            bool isBase = tag.Equals("base", StringComparison.OrdinalIgnoreCase);
            bool isMeta = tag.Equals("meta", StringComparison.OrdinalIgnoreCase);
            bool changed = false;

            var newAttrs = AttrRx.Replace(attrs, am =>
            {
                var name = am.Groups["name"].Value;
                if (!am.Groups["v"].Success) return am.Value;
                var raw = am.Groups["v"].Value;
                string result = null;

                if (isBase && name.Equals("href", StringComparison.OrdinalIgnoreCase))
                {
                    // <base href> würde die lokalen relativen Links verbiegen → beim Umschreiben entfernen
                    if (map(null, LinkKind.None) == "") { changed = true; return ""; }
                    return am.Value;
                }
                if (name.Equals("integrity", StringComparison.OrdinalIgnoreCase) || name.Equals("crossorigin", StringComparison.OrdinalIgnoreCase))
                {
                    if (map(null, LinkKind.None) == "") { changed = true; return ""; }
                    return am.Value;
                }
                if (UrlAttrs.Contains(name))
                {
                    result = MapOne(raw, baseUrl, Classify(tag, name, rel), map);
                }
                else if (SrcsetAttrs.Contains(name))
                {
                    result = MapSrcset(raw, baseUrl, map);
                }
                else if (name.Equals("style", StringComparison.OrdinalIgnoreCase) && raw.Contains("url(", StringComparison.OrdinalIgnoreCase))
                {
                    var css = WebUtility.HtmlDecode(raw);
                    var r = RewriteCss(css, baseUrl, u => map(u, LinkKind.Resource));
                    result = r == css ? null : WebUtility.HtmlEncode(r);
                }
                else if (isMeta && name.Equals("content", StringComparison.OrdinalIgnoreCase)
                         && Regex.IsMatch(attrs, @"http-equiv\s*=\s*[""']?refresh", RegexOptions.IgnoreCase))
                {
                    var m = Regex.Match(raw, @"^(\s*\d+\s*;\s*url\s*=\s*['""]?)([^'""]+)(.*)$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        var r = MapOne(m.Groups[2].Value, baseUrl, LinkKind.Page, map);
                        if (r != null) result = m.Groups[1].Value + r + m.Groups[3].Value;
                    }
                }
                if (result == null) return am.Value;
                changed = true;
                return $"{am.Groups["pre"].Value}{name}{am.Groups["eq"].Value}\"{result}\"";
            });
            if (!changed) return tm.Value;
            return tm.Value.Replace(attrs, newAttrs);
        });
    }

    /// <summary>Einzelne URL (HTML-kodiert) auflösen und abbilden. null = unverändert lassen.</summary>
    private static string MapOne(string rawEncoded, Uri baseUrl, LinkKind kind, Func<Uri, LinkKind, string> map)
    {
        var raw = WebUtility.HtmlDecode(rawEncoded).Trim();
        if (raw == "" || raw.StartsWith('#') || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || raw.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || raw.Contains("{{") || raw.Contains("${"))
            return null;
        if (!Uri.TryCreate(baseUrl, raw, out var abs) || abs.Scheme is not ("http" or "https")) return null;
        var r = map(abs, kind);
        return r == null ? null : WebUtility.HtmlEncode(r);
    }

    private static string MapSrcset(string rawEncoded, Uri baseUrl, Func<Uri, LinkKind, string> map)
    {
        var raw = WebUtility.HtmlDecode(rawEncoded);
        bool changed = false;
        var parts = raw.Split(',').Select(p =>
        {
            var t = p.Trim();
            if (t == "") return p;
            var sp = t.IndexOfAny(new[] { ' ', '\t', '\n' });
            var url = sp < 0 ? t : t[..sp];
            var desc = sp < 0 ? "" : t[sp..];
            if (!Uri.TryCreate(baseUrl, url, out var abs) || abs.Scheme is not ("http" or "https")) return p;
            var r = map(abs, LinkKind.Resource);
            if (r == null) return p;
            changed = true;
            return r + desc;
        }).ToList();
        return changed ? WebUtility.HtmlEncode(string.Join(", ", parts)) : null;
    }

    private static string RewriteCss(string css, Uri baseUrl, Func<Uri, string> map)
    {
        return CssUrlRx.Replace(css, m =>
        {
            bool import = m.Groups["u2"].Success;
            var raw = (import ? m.Groups["u2"].Value : m.Groups["u"].Value).Trim();
            if (raw == "" || raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || raw.StartsWith('#')) return m.Value;
            if (!Uri.TryCreate(baseUrl, raw, out var abs) || abs.Scheme is not ("http" or "https")) return m.Value;
            var r = map(abs);
            if (r == null) return m.Value;
            return import ? $"@import \"{r}\"" : $"url(\"{r}\")";
        });
    }

    /* ---------------------------------------------------------------- Phase 2: Links umschreiben */

    private void RewriteAll()
    {
        foreach (var doc in _docs)
        {
            try
            {
                var bytes = File.ReadAllBytes(doc.LocalPath);
                var text = doc.Encoding.GetString(bytes);
                var dir = Path.GetDirectoryName(doc.LocalPath)!;
                string Map(Uri u)
                {
                    if (u == null) return "";                     // Signal: Attribut entfernen (base/integrity)
                    string local;
                    lock (_lock) _local.TryGetValue(Key(u), out local);
                    if (local == null) return u.Scheme is "http" or "https" && u.IsAbsoluteUri ? u.AbsoluteUri : null; // online belassen (absolut)
                    var rel = Path.GetRelativePath(dir, local).Replace('\\', '/');
                    rel = string.Join("/", rel.Split('/').Select(s => s == ".." ? s : Uri.EscapeDataString(s)));
                    return rel + u.Fragment;
                }
                var result = doc.IsHtml ? RewriteHtml(text, doc.BaseUrl, (u, _) => Map(u)) : RewriteCss(text, doc.BaseUrl, Map);
                var enc = doc.Encoding is UTF8Encoding ? new UTF8Encoding(false) : doc.Encoding;
                File.WriteAllText(doc.LocalPath, result, enc);
            }
            catch { /* Datei bleibt im Originalzustand */ }
        }
    }

    private void WriteRootIndex(Uri start)
    {
        if (StartFile == null) return;
        try
        {
            var rel = Path.GetRelativePath(_root, StartFile).Replace('\\', '/');
            rel = string.Join("/", rel.Split('/').Select(Uri.EscapeDataString));
            var title = WebUtility.HtmlEncode(start.Host);
            File.WriteAllText(RootIndex,
                $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0; url={rel}\">" +
                $"<title>{title} – Offline-Kopie</title></head>\n<body style=\"font-family:Segoe UI,sans-serif\"><p>Offline-Kopie von " +
                $"<b>{WebUtility.HtmlEncode(start.AbsoluteUri)}</b> ({DateTime.Now:dd.MM.yyyy HH:mm}, Net-Stuff)</p><p><a href=\"{rel}\">Startseite öffnen</a></p></body></html>\n",
                new UTF8Encoding(false));
        }
        catch { }
    }

    /* ---------------------------------------------------------------- Zeichensatz */

    private static Encoding DetectEncoding(byte[] bytes, string headerCharset, bool isHtml)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return new UTF8Encoding(false);
        string cs = headerCharset?.Trim('"', '\'', ' ');
        if (string.IsNullOrEmpty(cs) && isHtml)
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase);
            if (m.Success) cs = m.Groups[1].Value;
        }
        if (!string.IsNullOrEmpty(cs))
        {
            try { var e = Encoding.GetEncoding(cs); return e is UTF8Encoding ? new UTF8Encoding(false) : e; } catch { }
        }
        return new UTF8Encoding(false);
    }

    /* ---------------------------------------------------------------- robots.txt */

    private async Task<RobotsRules> LoadRobotsAsync(Uri any, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(10000);
            var text = await _http.GetStringAsync(new Uri(any, "/robots.txt"), cts.Token);
            return RobotsRules.Parse(text);
        }
        catch { return RobotsRules.Parse(""); }
    }

    /// <summary>Test-Hilfe: HTML umschreiben mit fester Zuordnung (für den Selbsttest).</summary>
    public static string TestRewriteHtml(string html, Uri baseUrl, Func<Uri, string> map, List<string> found = null)
        => RewriteHtml(html, HtmlBase(html, baseUrl), (u, k) => { if (u != null && found != null && k != LinkKind.None) found.Add(k + " " + u); return map(u); });
}

/// <summary>Sehr einfache robots.txt-Auswertung (User-agent: *, Disallow/Allow mit * und $).</summary>
public class RobotsRules
{
    private readonly List<(bool Allow, Regex Rx, int Len)> _rules = new();

    public static RobotsRules Parse(string text)
    {
        var r = new RobotsRules();
        bool applies = false, lastWasAgent = false;
        foreach (var line0 in text.Split('\n'))
        {
            var line = line0.Split('#')[0].Trim();
            if (line == "") continue;
            int c = line.IndexOf(':');
            if (c < 0) continue;
            var key = line[..c].Trim().ToLowerInvariant();
            var val = line[(c + 1)..].Trim();
            if (key == "user-agent")
            {
                if (!lastWasAgent) applies = false;
                if (val == "*" || val.Contains("net-stuff", StringComparison.OrdinalIgnoreCase)) applies = true;
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (!applies || val == "") continue;
            if (key is "disallow" or "allow")
            {
                var pattern = "^" + Regex.Escape(val).Replace(@"\*", ".*");
                if (pattern.EndsWith(@"\$")) pattern = pattern[..^2] + "$";
                r._rules.Add((key == "allow", new Regex(pattern), val.Length));
            }
        }
        return r;
    }

    public bool Allows(string pathAndQuery)
    {
        var best = _rules.Where(x => x.Rx.IsMatch(pathAndQuery)).OrderByDescending(x => x.Len).ThenByDescending(x => x.Allow).FirstOrDefault();
        return best.Rx == null || best.Allow;
    }
}
