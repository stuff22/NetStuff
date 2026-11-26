using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>Ein Ordner oder eine Datei im Scan-Ergebnis. Ordnerwerte (Größe, Anzahl, Datum) sind über alle Unterordner summiert.</summary>
public sealed class DiskNode
{
    private readonly string _rootPath;
    private IReadOnlyList<DiskNode> _subFolders, _items;

    internal DiskNode(string name, DiskNode parent, bool isDir, string rootPath = null)
    {
        Name = name;
        Parent = parent;
        IsDir = isDir;
        _rootPath = rootPath;
    }

    public string Name { get; }
    public DiskNode Parent { get; }
    public bool IsDir { get; }
    /// <summary>Größe in Bytes (Ordner: Summe aller enthaltenen Dateien).</summary>
    public long Size { get; internal set; }
    /// <summary>Ordner: Anzahl Dateien im gesamten Teilbaum.</summary>
    public long Files { get; internal set; }
    /// <summary>Ordner: Anzahl Unterordner im gesamten Teilbaum.</summary>
    public long Dirs { get; internal set; }
    /// <summary>Datei: letzte Änderung. Ordner: jüngste Änderung einer enthaltenen Datei (UTC).</summary>
    public DateTime Modified { get; internal set; }
    /// <summary>Datei liegt nur in der Cloud (OneDrive/iCloud-Platzhalter) und belegt lokal keinen Platz.</summary>
    public bool CloudOnly { get; internal set; }
    /// <summary>Ordner: Bytes, die nur in der Cloud liegen.</summary>
    public long CloudBytes { get; internal set; }
    /// <summary>Ordner: Anzahl nicht lesbarer Ordner im Teilbaum (inkl. diesem).</summary>
    public int ErrorCount { get; internal set; }
    /// <summary>Fehlermeldung, wenn dieser Ordner nicht gelesen werden konnte.</summary>
    public string Error { get; internal set; }

    internal List<DiskNode> Children;

    public string FullPath => Parent == null ? _rootPath : Path.Combine(Parent.FullPath, Name);
    public string FolderPath => Parent?.FullPath ?? "";
    public string Extension => IsDir ? "" : DiskAnalyzer.ExtensionOf(Name);

    public double Percent => Parent == null || Parent.Size <= 0 ? 100 : Size * 100.0 / Parent.Size;
    public string PercentText => Percent.ToString("0.0", CultureInfo.CurrentCulture) + " %";
    public string SizeText => DiskAnalyzer.FormatSize(Size);
    public string FilesText => IsDir ? Files.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string DirsText => IsDir ? Dirs.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string ModifiedText => Modified == default ? "" : Modified.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    public string Icon => IsDir ? (Error != null ? "⚠" : "📁") : CloudOnly ? "☁" : "📄";
    public string DisplayName => Parent == null ? _rootPath : Name;
    public string TypeText => IsDir ? L.T("Ordner") : L.T(DiskAnalyzer.CategoryOf(Extension));
    /// <summary>Baumansicht: „12,3 GB  Ordnername“.</summary>
    public string TreeText => $"{SizeText}   {DisplayName}" + (Error != null ? "  ⚠" : "");

    /// <summary>Unterordner, größte zuerst (für die Baumansicht, höchstens 500).</summary>
    public IReadOnlyList<DiskNode> SubFolders => _subFolders ??=
        Children == null ? Array.Empty<DiskNode>() : Children.Where(c => c.IsDir).OrderByDescending(c => c.Size).Take(500).ToList();

    /// <summary>Inhalt (Ordner und Dateien), größte zuerst.</summary>
    public IReadOnlyList<DiskNode> Items => _items ??=
        Children == null ? Array.Empty<DiskNode>() : Children.OrderByDescending(c => c.Size).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public IEnumerable<DiskNode> AllFiles()
    {
        var stack = new Stack<DiskNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDir) { yield return n; continue; }
            if (n.Children != null) foreach (var c in n.Children) stack.Push(c);
        }
    }

    public IEnumerable<DiskNode> AllFolders()
    {
        var stack = new Stack<DiskNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!n.IsDir) continue;
            yield return n;
            if (n.Children != null) foreach (var c in n.Children) if (c.IsDir) stack.Push(c);
        }
    }

    public override string ToString() => FullPath;
}

/// <summary>Laufwerk oder Freigabe zur Auswahl.</summary>
public sealed record DiskLocation(string Path, string Label, string Kind, long Total, long Free)
{
    public double UsedPercent => Total > 0 ? (Total - Free) * 100.0 / Total : 0;
    public string Title => string.IsNullOrEmpty(Label) ? Path : $"{Label} ({Path})";
    public string Detail => Total > 0
        ? L.F("{0} – {1} frei von {2}", L.T(Kind), DiskAnalyzer.FormatSize(Free), DiskAnalyzer.FormatSize(Total))
        : L.T(Kind);
}

public sealed record DiskTypeRow(string Extension, string Category, long Size, long Count, double Percent)
{
    public string ExtensionText => Extension == "" ? L.T("(ohne Endung)") : "." + Extension;
    public string CategoryText => L.T(Category);
    public string SizeText => DiskAnalyzer.FormatSize(Size);
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public string PercentText => Percent.ToString("0.0", CultureInfo.CurrentCulture) + " %";
}

public sealed class DuplicateGroup
{
    public int Number { get; init; }
    public long Size { get; init; }
    public List<DiskNode> Files { get; init; }
    public long Wasted => Size * (Files.Count - 1);
    public string SizeText => DiskAnalyzer.FormatSize(Size);
    public string WastedText => DiskAnalyzer.FormatSize(Wasted);
    public string CountText => Files.Count.ToString(CultureInfo.CurrentCulture);
    public string Name => Files[0].Name;
    public string PathsText => string.Join("\n", Files.Select(f => f.FullPath));
    public string FirstPath => Files[0].FullPath;
}

/// <summary>Eine Gruppe möglicher Platzverschwendung (Papierkorb, Caches, Installer …).</summary>
public sealed class WasteHint
{
    public string Title { get; init; }
    public string Explanation { get; init; }
    public long Size { get; set; }
    public int Count { get; set; }
    public List<DiskNode> Entries { get; } = new();
}

/// <summary>Auswertung eines Scans: größte Dateien, Dateitypen, alte Dateien, Aufräum-Kandidaten.</summary>
public sealed class DiskReport
{
    public DiskNode Root { get; init; }
    public List<DiskNode> LargestFiles { get; init; }
    public List<DiskTypeRow> Types { get; init; }
    public List<WasteHint> Hints { get; init; }
    public int EmptyFolders { get; init; }
    public long EmptyFiles { get; init; }
    public long CloudBytes { get; init; }
    public long CloudFiles { get; init; }
}

public sealed class OldFilesResult
{
    public int Years { get; init; }
    public DateTime Before { get; init; }
    public long Size { get; init; }
    public long Count { get; init; }
    public List<DiskNode> Largest { get; init; }
    public double Percent { get; init; }
}

/// <summary>
/// Speicherplatz-Analyse wie TreeSize: liest einen Ordner, ein Laufwerk oder eine Netzwerkfreigabe (UNC-Pfad, verbundenes
/// Laufwerk, unter macOS /Volumes/…) parallel ein und zeigt, wo der Platz bleibt. Symbolische Links und Abzweigungen
/// (Junctions) werden nicht verfolgt, damit nichts doppelt gezählt wird. Cloud-Platzhalter werden nicht heruntergeladen.
/// </summary>
public sealed class DiskAnalyzer
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private SemaphoreSlim _gate;
    private long _bytes, _files, _dirs;
    private int _errors;
    private HashSet<string> _skip;

    public long BytesFound => Interlocked.Read(ref _bytes);
    public long FilesFound => Interlocked.Read(ref _files);
    public long DirsFound => Interlocked.Read(ref _dirs);
    public int Errors => Volatile.Read(ref _errors);
    /// <summary>Zuletzt gelesener Ordner (für die Fortschrittsanzeige).</summary>
    public string CurrentPath { get; private set; }
    public bool Cancelled { get; private set; }
    public TimeSpan Elapsed { get; private set; }
    public bool IsNetwork { get; private set; }

    /// <summary>Gleichzeitig gelesene Ordner (0 = automatisch: 8 lokal, 16 im Netzwerk).</summary>
    public int Parallel { get; set; }

    public static bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith(@"\\") || path.StartsWith("//")) return true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(Path.GetFullPath(path));
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            var mount = MountOf(path);
            return mount != null && IsNetworkFormat(mount.DriveFormat);
        }
        catch { return false; }
    }

    private static bool IsNetworkFormat(string format) =>
        format != null && (format.Contains("smb", StringComparison.OrdinalIgnoreCase) || format.Contains("afp", StringComparison.OrdinalIgnoreCase)
            || format.Contains("nfs", StringComparison.OrdinalIgnoreCase) || format.Contains("webdav", StringComparison.OrdinalIgnoreCase)
            || format.Contains("cifs", StringComparison.OrdinalIgnoreCase));

    private static DriveInfo MountOf(string path)
    {
        var full = Path.GetFullPath(path);
        DriveInfo best = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            var name = d.Name.TrimEnd('/');
            if (name == "" ? full.StartsWith('/') : full == name || full.StartsWith(name + "/"))
                if (best == null || d.Name.Length > best.Name.Length) best = d;
        }
        return best;
    }

    /// <summary>Ordner einlesen. Bei Abbruch wird das bis dahin gelesene Teilergebnis zurückgegeben (<see cref="Cancelled"/>).</summary>
    public async Task<DiskNode> ScanAsync(string path, CancellationToken ct)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path == "") throw new InvalidOperationException(L.T("Bitte einen Ordner, ein Laufwerk oder eine Freigabe angeben."));
        if (path.Length == 2 && path[1] == ':') path += Path.DirectorySeparatorChar;   // „D:“ → „D:\“
        path = Path.GetFullPath(path);
        if (path.Length > 3 && (path.EndsWith('\\') || path.EndsWith('/'))) path = path.TrimEnd('\\', '/');
        bool exists = await Task.Run(() => Directory.Exists(path), ct);
        if (!exists) throw new InvalidOperationException(L.F("„{0}“ wurde nicht gefunden oder ist nicht erreichbar.", path));

        IsNetwork = IsNetworkPath(path);
        int parallel = Parallel > 0 ? Parallel : IsNetwork ? 16 : 8;
        _gate = new SemaphoreSlim(parallel);
        _skip = SkipFolders(path);
        _bytes = _files = _dirs = 0;
        _errors = 0;
        Cancelled = false;

        var sw = Stopwatch.StartNew();
        var root = new DiskNode(Path.GetFileName(path), null, true, path);
        await Task.Run(() => ScanDirAsync(root, path, ct));
        Elapsed = sw.Elapsed;
        return root;
    }

    /// <summary>macOS/Linux: beim Lesen von „/“ keine anderen Laufwerke und Systemspiegel mitzählen.</summary>
    private static HashSet<string> SkipFolders(string root)
    {
        var skip = new HashSet<string>(StringComparer.Ordinal);
        if (OperatingSystem.IsWindows()) return skip;
        foreach (var p in new[] { "/Volumes", "/System/Volumes", "/dev", "/proc", "/sys", "/run", "/mnt", "/media", "/net", "/home/.snapshots" })
            if (p != root && p.StartsWith(root == "/" ? "/" : root + "/")) skip.Add(p);
        return skip;
    }

    private readonly record struct Entry(string Name, bool IsDir, long Length, DateTime Modified, FileAttributes Attributes);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
        BufferSize = 64 * 1024,   // weniger Anfragen über SMB
    };

    private async Task ScanDirAsync(DiskNode node, string path, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) { Cancelled = true; return; }
        var subs = new List<(DiskNode Node, string Path)>();
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Cancelled = true; return; }
        try
        {
            CurrentPath = path;
            Read(node, path, subs, ct);
        }
        finally { _gate.Release(); }

        if (ct.IsCancellationRequested) { Cancelled = true; subs.Clear(); }
        if (subs.Count == 1) await ScanDirAsync(subs[0].Node, subs[0].Path, ct).ConfigureAwait(false);
        else if (subs.Count > 1) await Task.WhenAll(subs.Select(s => Task.Run(() => ScanDirAsync(s.Node, s.Path, ct)))).ConfigureAwait(false);

        // Summen von unten nach oben
        if (node.Children == null) return;
        foreach (var c in node.Children)
        {
            if (!c.IsDir) continue;
            node.Size += c.Size;
            node.Files += c.Files;
            node.Dirs += c.Dirs + 1;
            node.CloudBytes += c.CloudBytes;
            node.ErrorCount += c.ErrorCount;
            if (c.Modified > node.Modified) node.Modified = c.Modified;
        }
    }

    private void Read(DiskNode node, string path, List<(DiskNode, string)> subs, CancellationToken ct)
    {
        node.Children = new List<DiskNode>();
        long bytes = 0, files = 0, dirs = 0;
        try
        {
            var en = new FileSystemEnumerable<Entry>(path,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcDateTime, e.Attributes), Options);
            int n = 0;
            foreach (var e in en)
            {
                // Große Ordner auf Netzlaufwerken brauchen lange – Abbruch auch mittendrin
                if ((++n & 255) == 0 && ct.IsCancellationRequested) { Cancelled = true; break; }
                if (e.IsDir)
                {
                    var sub = Path.Combine(path, e.Name);
                    if (_skip.Contains(sub)) continue;
                    // Symbolische Links / Junctions nicht verfolgen (Schleifen, Doppelzählung). OneDrive-Ordner sind ebenfalls
                    // Analysepunkte, aber keine Links – darum das Ziel prüfen statt nur das Attribut.
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && IsLink(sub)) continue;
                    var d = new DiskNode(e.Name, node, true);
                    node.Children.Add(d);
                    subs.Add((d, sub));
                    dirs++;
                }
                else
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && !OperatingSystem.IsWindows()) continue;   // Unix-Symlink auf Datei
                    var f = new DiskNode(e.Name, node, false) { Size = e.Length, Modified = e.Modified };
                    if ((e.Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0)
                    {
                        f.CloudOnly = true;
                        node.CloudBytes += e.Length;
                    }
                    node.Children.Add(f);
                    node.Size += e.Length;
                    node.Files++;
                    if (e.Modified > node.Modified) node.Modified = e.Modified;
                    bytes += e.Length;
                    files++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            node.Error = ex is UnauthorizedAccessException ? L.T("Zugriff verweigert") : ex.Message;
            node.ErrorCount++;
            Interlocked.Increment(ref _errors);
        }
        node.Children.TrimExcess();
        Interlocked.Add(ref _bytes, bytes);
        Interlocked.Add(ref _files, files);
        Interlocked.Add(ref _dirs, dirs);
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget != null; }
        catch { return true; }
    }

    /* ------------------------------------------------------------------ Auswertung */

    public static DiskReport Analyze(DiskNode root)
    {
        var largest = new PriorityQueue<DiskNode, long>();
        var types = new Dictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
        var hints = WasteRules.Select(r => new WasteHint { Title = r.Title, Explanation = r.Explanation }).ToList();
        int emptyFolders = 0;
        long emptyFiles = 0, cloudBytes = 0, cloudFiles = 0;

        var stack = new Stack<(DiskNode Node, bool InWaste)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (n, inWaste) = stack.Pop();
            if (n.IsDir)
            {
                if (n.Children is { Count: 0 } && n.Error == null && n.Parent != null) emptyFolders++;
                bool matched = inWaste;
                if (!inWaste && n.Parent != null)
                    for (int i = 0; i < WasteRules.Length; i++)
                        if (n.Size > 0 && WasteRules[i].Folder?.Invoke(n) == true)
                        {
                            Add(hints[i], n);
                            matched = true;
                            break;
                        }
                if (n.Children != null) foreach (var c in n.Children) stack.Push((c, matched));
                continue;
            }

            var ext = ExtensionOf(n.Name);
            types[ext] = types.TryGetValue(ext, out var t) ? (t.Size + n.Size, t.Count + 1) : (n.Size, 1);
            if (n.Size == 0) emptyFiles++;
            if (n.CloudOnly) { cloudBytes += n.Size; cloudFiles++; }
            if (largest.Count < 500) largest.Enqueue(n, n.Size);
            else if (n.Size > largest.Peek().Size) largest.EnqueueDequeue(n, n.Size);
            if (!inWaste)
                for (int i = 0; i < WasteRules.Length; i++)
                    if (WasteRules[i].File?.Invoke(n) == true) { Add(hints[i], n); break; }
        }

        var largestList = new List<DiskNode>(largest.Count);
        while (largest.Count > 0) largestList.Add(largest.Dequeue());
        largestList.Reverse();

        long total = Math.Max(1, root.Size);
        foreach (var h in hints) h.Entries.Sort((a, b) => b.Size.CompareTo(a.Size));
        return new DiskReport
        {
            Root = root,
            LargestFiles = largestList,
            Types = types.Select(kv => new DiskTypeRow(kv.Key, CategoryOf(kv.Key), kv.Value.Size, kv.Value.Count, kv.Value.Size * 100.0 / total))
                .OrderByDescending(r => r.Size).ToList(),
            Hints = hints.Where(h => h.Count > 0).OrderByDescending(h => h.Size).ToList(),
            EmptyFolders = emptyFolders,
            EmptyFiles = emptyFiles,
            CloudBytes = cloudBytes,
            CloudFiles = cloudFiles,
        };

        static void Add(WasteHint h, DiskNode n)
        {
            h.Size += n.Size;
            h.Count++;
            h.Entries.Add(n);
        }
    }

    /// <summary>Dateien, die seit <paramref name="years"/> Jahren nicht geändert wurden.</summary>
    public static OldFilesResult OldFiles(DiskNode root, int years, int max = 1000)
    {
        var before = DateTime.UtcNow.AddYears(-years);
        var top = new PriorityQueue<DiskNode, long>();
        long size = 0, count = 0;
        foreach (var f in root.AllFiles())
        {
            if (f.Modified == default || f.Modified >= before) continue;
            size += f.Size;
            count++;
            if (top.Count < max) top.Enqueue(f, f.Size);
            else if (f.Size > top.Peek().Size) top.EnqueueDequeue(f, f.Size);
        }
        var list = new List<DiskNode>(top.Count);
        while (top.Count > 0) list.Add(top.Dequeue());
        list.Reverse();
        return new OldFilesResult { Years = years, Before = before, Size = size, Count = count, Largest = list, Percent = root.Size > 0 ? size * 100.0 / root.Size : 0 };
    }

    /* ------------------------------------------------------------------ Duplikate */

    /// <summary>
    /// Findet inhaltsgleiche Dateien ab <paramref name="minSize"/> Bytes: gleiche Größe → Prüfsumme über Anfang und Ende →
    /// SHA-256 über die ganze Datei. Cloud-Platzhalter werden übersprungen (sonst würden sie heruntergeladen).
    /// </summary>
    public static async Task<List<DuplicateGroup>> FindDuplicatesAsync(DiskNode root, long minSize, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        var bySize = root.AllFiles().Where(f => f.Size >= Math.Max(1, minSize) && !f.CloudOnly)
            .GroupBy(f => f.Size).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        long total = bySize.Sum(g => g.Sum(f => Math.Min(f.Size, 2 * PartBytes)));
        long full = 0;
        long done = 0;
        void Report(long n) => progress?.Report((Interlocked.Add(ref done, n), total + Interlocked.Read(ref full)));

        var po = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };
        // 1. Anfang + Ende
        var partial = new ConcurrentBag<(List<DiskNode> Group, Dictionary<DiskNode, string> Hash)>();
        await System.Threading.Tasks.Parallel.ForEachAsync(bySize, po, async (group, token) =>
        {
            var hashes = new Dictionary<DiskNode, string>();
            foreach (var f in group)
            {
                var h = await PartialHashAsync(f.FullPath, f.Size, token);
                Report(Math.Min(f.Size, 2 * PartBytes));
                if (h != null) hashes[f] = h;
            }
            partial.Add((group, hashes));
        });

        // 2. Ganze Datei (nur wenn Anfang und Ende übereinstimmen und die Datei größer als die geprüften Teile ist)
        var candidates = new List<List<DiskNode>>();
        var finished = new List<List<DiskNode>>();
        foreach (var (group, hashes) in partial)
            foreach (var same in hashes.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).Select(g => g.Select(kv => kv.Key).ToList()))
                (same[0].Size <= 2 * PartBytes ? finished : candidates).Add(same);
        Interlocked.Add(ref full, candidates.Sum(g => g.Sum(f => f.Size)));

        var verified = new ConcurrentBag<List<DiskNode>>(finished);
        await System.Threading.Tasks.Parallel.ForEachAsync(candidates, po, async (group, token) =>
        {
            var hashes = new List<(DiskNode, string)>();
            foreach (var f in group)
            {
                var h = await FullHashAsync(f.FullPath, token, Report);
                if (h != null) hashes.Add((f, h));
            }
            foreach (var g in hashes.GroupBy(x => x.Item2).Where(g => g.Count() > 1))
                verified.Add(g.Select(x => x.Item1).ToList());
        });

        int nr = 0;
        return verified.Select(g => g.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList())
            .OrderByDescending(g => g[0].Size * (g.Count - 1))
            .Select(g => new DuplicateGroup { Number = ++nr, Size = g[0].Size, Files = g })
            .ToList();
    }

    private const int PartBytes = 64 * 1024;

    private static async Task<string> PartialHashAsync(string path, long size, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[PartBytes];
            int n = await ReadFullAsync(fs, buf, ct);
            hash.AppendData(buf, 0, n);
            if (size > 2 * PartBytes)
            {
                fs.Seek(-PartBytes, SeekOrigin.End);
                n = await ReadFullAsync(fs, buf, ct);
                hash.AppendData(buf, 0, n);
            }
            else if (size > PartBytes)
            {
                n = await ReadFullAsync(fs, buf, ct);
                hash.AppendData(buf, 0, n);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }   // gesperrt, gelöscht, kein Zugriff
    }

    private static async Task<string> FullHashAsync(string path, CancellationToken ct, Action<long> report)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1024 * 1024];
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0)
            {
                hash.AppendData(buf, 0, n);
                report(n);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static async Task<int> ReadFullAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0, n;
        while (total < buf.Length && (n = await s.ReadAsync(buf.AsMemory(total), ct)) > 0) total += n;
        return total;
    }

    /* ------------------------------------------------------------------ Laufwerke */

    /// <summary>Laufwerke, Netzlaufwerke und häufige Ordner zur Schnellauswahl.</summary>
    public static List<DiskLocation> Locations()
    {
        var list = new List<DiskLocation>();
        foreach (var d in SafeDrives())
        {
            try
            {
                string kind = d.DriveType switch
                {
                    DriveType.Network => "Netzlaufwerk",
                    DriveType.Removable => "Wechseldatenträger",
                    DriveType.Fixed => "Lokales Laufwerk",
                    _ => null,
                };
                if (OperatingSystem.IsWindows())
                {
                    if (kind == null || !d.IsReady) continue;
                    var label = d.VolumeLabel;
                    if (d.DriveType == DriveType.Network) label = NetworkShareOf(d.Name) ?? label;
                    list.Add(new DiskLocation(d.Name, label, kind, d.TotalSize, d.AvailableFreeSpace));
                }
                else
                {
                    var name = d.Name;
                    if (!d.IsReady) continue;
                    bool net = IsNetworkFormat(d.DriveFormat);
                    if (name == "/") list.Add(new DiskLocation("/", OperatingSystem.IsMacOS() ? "Macintosh HD" : L.T("System"), "Lokales Laufwerk", d.TotalSize, d.AvailableFreeSpace));
                    else if (name.StartsWith("/Volumes/") || name.StartsWith("/media/") || name.StartsWith("/mnt/") || net)
                    {
                        if (IsLink(name)) continue;
                        list.Add(new DiskLocation(name, Path.GetFileName(name.TrimEnd('/')), net ? "Netzlaufwerk" : "Laufwerk", d.TotalSize, d.AvailableFreeSpace));
                    }
                }
            }
            catch { }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home)) list.Add(new DiskLocation(home, L.T("Benutzerordner"), "Ordner", 0, 0));
        var downloads = Path.Combine(home, "Downloads");
        if (Directory.Exists(downloads)) list.Add(new DiskLocation(downloads, "Downloads", "Ordner", 0, 0));
        return list;
    }

    private static DriveInfo[] SafeDrives()
    {
        try { return DriveInfo.GetDrives(); } catch { return Array.Empty<DriveInfo>(); }
    }

    /// <summary>Speicherplatz des Laufwerks bzw. der Freigabe, auf der der Ordner liegt (auch UNC).</summary>
    public static (long Total, long Free)? VolumeSpace(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = path.EndsWith('\\') ? path : path + "\\";
                if (GetDiskFreeSpaceEx(dir, out var free, out var total, out _)) return ((long)total, (long)free);
                return null;
            }
            var m = MountOf(path);
            return m != null && m.IsReady ? (m.TotalSize, m.AvailableFreeSpace) : null;
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

    /// <summary>Windows: \\server\freigabe hinter einem verbundenen Laufwerksbuchstaben.</summary>
    public static string NetworkShareOf(string drive)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var sb = new StringBuilder(512);
            int len = sb.Capacity;
            return WNetGetConnection(drive.TrimEnd('\\'), sb, ref len) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>Datei/Ordner im Explorer bzw. Finder markiert anzeigen.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(path);
                Process.Start(psi);
            }
            else NetUtil.OpenUrl(Directory.Exists(path) ? path : Path.GetDirectoryName(path));
        }
        catch { }
    }

    /* ------------------------------------------------------------------ Texte, CSV */

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes:N0} B" : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " " + u[i];
    }

    public static string ExtensionOf(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > 12) return "";
        return name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>CSV aller Ordner (Semikolon, für Excel).</summary>
    public static string FoldersCsv(DiskNode root)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", L.T("Pfad"), L.T("Größe (Bytes)"), L.T("Größe"), L.T("Dateien"), L.T("Ordner"), L.T("Zuletzt geändert"), L.T("Fehler")));
        foreach (var d in root.AllFolders().OrderByDescending(d => d.Size))
            sb.AppendLine(string.Join(";", Csv(d.FullPath), d.Size, d.SizeText, d.Files, d.Dirs,
                d.Modified == default ? "" : d.Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), Csv(d.Error ?? "")));
        return sb.ToString();
    }

    private static string Csv(string s) => s.IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /* ------------------------------------------------------------------ Ergebnisabschnitte */

    /// <summary>Zusammenfassung, größte Ordner, Aufräum-Kandidaten und Dateitypen als Prüfabschnitte (für Anzeige, Kopieren, Bericht).</summary>
    public static List<CheckSection> Sections(DiskAnalyzer scan, DiskReport r, OldFilesResult old)
    {
        var root = r.Root;
        var list = new List<CheckSection>();
        var overview = new List<CheckItem>
        {
            new("Ordner", root.FullPath),
            new("Belegt", $"{FormatSize(root.Size)} ({root.Size:N0} Bytes)"),
            new("Dateien", root.Files.ToString("N0", CultureInfo.CurrentCulture)),
            new("Unterordner", root.Dirs.ToString("N0", CultureInfo.CurrentCulture)),
        };
        var space = VolumeSpace(root.FullPath);
        if (space is { Total: > 0 } s)
        {
            double usedPct = (s.Total - s.Free) * 100.0 / s.Total;
            overview.Add(new CheckItem(scan.IsNetwork ? "Freigabe / Netzlaufwerk" : "Laufwerk",
                L.F("{0} frei von {1} ({2:0} % belegt)", FormatSize(s.Free), FormatSize(s.Total), usedPct),
                usedPct >= 95 ? Level.Bad : usedPct >= 85 ? Level.Warn : Level.Ok));
            overview.Add(new CheckItem("Anteil dieses Ordners", L.F("{0:0.0} % des Laufwerks", root.Size * 100.0 / s.Total)));
        }
        if (r.CloudFiles > 0)
            overview.Add(new CheckItem("Nur in der Cloud", L.F("{0} in {1:N0} Dateien – belegen lokal keinen Platz", FormatSize(r.CloudBytes), r.CloudFiles)));
        if (root.ErrorCount > 0)
            overview.Add(new CheckItem("Nicht lesbar", L.F("{0:N0} Ordner ohne Zugriffsrecht – deren Inhalt fehlt in der Summe", root.ErrorCount)
                + (OperatingSystem.IsWindows() ? L.T(" (Net-Stuff als Administrator starten)") : ""), Level.Warn));
        if (scan.Cancelled) overview.Add(new CheckItem("Status", "Abgebrochen – Werte sind unvollständig", Level.Warn));
        overview.Add(new CheckItem("Dauer", L.F("{0:0.0#} s ({1:N0} Dateien/s)", scan.Elapsed.TotalSeconds, root.Files / Math.Max(0.1, scan.Elapsed.TotalSeconds))));
        list.Add(new CheckSection("Übersicht", overview));

        var top = root.SubFolders.Take(12).Select(d => new CheckItem(d.Name,
            $"{d.SizeText} · {d.PercentText} · " + L.F("{0:N0} Dateien", d.Files) + (d.Error != null ? " · " + d.Error : ""),
            d.Percent >= 40 ? Level.Warn : Level.Info)).ToList();
        long looseFiles = root.Children?.Where(c => !c.IsDir).Sum(c => c.Size) ?? 0;
        if (looseFiles > 0) top.Add(new CheckItem("(Dateien direkt in diesem Ordner)", FormatSize(looseFiles)));
        if (top.Count > 0) list.Add(new CheckSection("Größte Ordner", top));

        // Aufräum-Kandidaten
        var waste = new List<CheckItem>();
        foreach (var h in r.Hints)
            waste.Add(new CheckItem(h.Title, L.F("{0} in {1:N0} Einträgen", FormatSize(h.Size), h.Count),
                h.Size >= 1L << 30 || h.Size >= root.Size / 20 ? Level.Warn : Level.Info));
        if (old != null && old.Count > 0)
            waste.Add(new CheckItem(old.Years == 1 ? L.T("Seit über 1 Jahr unverändert") : L.F("Seit über {0} Jahren unverändert", old.Years),
                L.F("{0} in {1:N0} Dateien ({2:0} %)", FormatSize(old.Size), old.Count, old.Percent), old.Percent >= 30 ? Level.Warn : Level.Info));
        if (r.EmptyFolders > 0) waste.Add(new CheckItem("Leere Ordner", r.EmptyFolders.ToString("N0", CultureInfo.CurrentCulture)));
        if (r.EmptyFiles > 0) waste.Add(new CheckItem("Leere Dateien (0 Bytes)", r.EmptyFiles.ToString("N0", CultureInfo.CurrentCulture)));
        list.Add(waste.Count > 0
            ? new CheckSection("Mögliche Platzverschwendung", waste, "Vorschläge – vor dem Löschen prüfen. Doppelte Dateien findet der Reiter „Duplikate“.")
            : new CheckSection("Mögliche Platzverschwendung", new[] { new CheckItem("", "Keine typischen Aufräum-Kandidaten gefunden.", Level.Ok) }));

        foreach (var h in r.Hints.Where(h => h.Size > 0).Take(12))
            list.Add(new CheckSection(h.Title, h.Entries.Take(8).Select(e => new CheckItem(e.SizeText, e.FullPath)), h.Explanation));

        var types = r.Types.Take(10).Select(t => new CheckItem(t.ExtensionText,
            $"{t.SizeText} · {t.PercentText} · " + L.F("{0:N0} Dateien", t.Count) + " · " + t.CategoryText)).ToList();
        if (types.Count > 0) list.Add(new CheckSection("Dateitypen mit dem meisten Platz", types));
        return list;
    }

    /* ------------------------------------------------------------------ Regeln */

    private sealed record WasteRule(string Title, string Explanation, Func<DiskNode, bool> Folder, Func<DiskNode, bool> File);

    private static HashSet<string> Set(params string[] s) => new(s, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TrashDirs = Set("$RECYCLE.BIN", ".Trash", ".Trashes", "RECYCLER", "#recycle", "@Recycle", ".recycle");
    private static readonly HashSet<string> TempDirs = Set("Temp", "Tmp", "$TEMP");
    private static readonly HashSet<string> CacheDirs = Set("Cache", "Caches", "cache2", "Code Cache", "GPUCache", ".cache", "INetCache", "CachedData",
        "ShaderCache", "DXCache", "D3DSCache", "__pycache__", "CrashpadMetrics", "Service Worker", "WebCache", "@eaDir");
    private static readonly HashSet<string> UpgradeDirs = Set("Windows.old", "$WINDOWS.~BT", "$Windows.~WS", "$GetCurrent", "$SysReset", "ESD");
    private static readonly HashSet<string> DevDirs = Set("node_modules", ".gradle", "DerivedData", ".npm", ".nuget", "bower_components", ".venv", "venv", "target");
    private static readonly HashSet<string> DumpDirs = Set("CrashDumps", "Minidump", "DiagnosticReports", "WER", "LiveKernelReports");
    private static readonly HashSet<string> InstallerExt = Set("iso", "dmg", "msi", "msix", "msixbundle", "appx", "pkg", "mpkg");
    private static readonly HashSet<string> BackupExt = Set("bak", "old", "backup", "orig", "wbk", "bkf", "sav");
    private static readonly HashSet<string> DumpExt = Set("dmp", "mdmp", "hdmp", "etl");
    private static readonly HashSet<string> ArchiveExt = Set("zip", "rar", "7z", "tar", "gz", "tgz");
    private static readonly HashSet<string> VmExt = Set("vhd", "vhdx", "vmdk", "vdi", "qcow2", "ova", "avhdx", "vmem", "vmsn");
    private static readonly HashSet<string> SystemFiles = Set("hiberfil.sys", "pagefile.sys", "swapfile.sys", "sleepimage", "MEMORY.DMP");

    private static bool InDownloads(DiskNode n)
    {
        for (var p = n.Parent; p != null; p = p.Parent)
            if (p.Name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Download", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Archiv liegt neben einem gleichnamigen Ordner → wurde schon entpackt.</summary>
    private static bool ExtractedArchive(DiskNode f)
    {
        if (!ArchiveExt.Contains(f.Extension) || f.Size < 1024 * 1024 || f.Parent?.Children == null) return false;
        var baseName = f.Name[..f.Name.LastIndexOf('.')];
        if (baseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) baseName = baseName[..^4];
        return f.Parent.Children.Any(c => c.IsDir && c.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly WasteRule[] WasteRules =
    {
        new("Papierkorb", "Gelöschte Dateien, die noch Platz belegen – den Papierkorb leeren (auf NAS-Freigaben: Netzwerk-Papierkorb im NAS leeren).",
            d => TrashDirs.Contains(d.Name), null),
        new("Reste von Windows-Upgrades", "Sicherung der vorherigen Windows-Version und Update-Dateien – über Einstellungen › System › Speicher › Temporäre Dateien entfernen.",
            d => UpgradeDirs.Contains(d.Name) && d.Parent?.Parent == null, null),
        new("Temporäre Dateien", "Zwischendateien von Programmen und Installationen. Meist gefahrlos löschbar, wenn die Programme geschlossen sind.",
            d => TempDirs.Contains(d.Name),
            f => f.Extension is "tmp" or "temp" || f.Name.StartsWith("~$")),
        new("Caches (Zwischenspeicher)", "Browser- und Programm-Caches werden bei Bedarf neu erstellt. Löschen bringt Platz, die Programme sind danach kurz langsamer.",
            d => CacheDirs.Contains(d.Name), null),
        new("Installationsdateien & Abbilder", "Installer (ISO, DMG, MSI, PKG) und Programme in „Downloads“ werden nach der Installation meist nicht mehr gebraucht.",
            null, f => f.Size >= 1024 * 1024 && (InstallerExt.Contains(f.Extension) || (f.Extension == "exe" && InDownloads(f)))),
        new("Bereits entpackte Archive", "Neben dem Archiv liegt ein gleichnamiger Ordner – der Inhalt ist vermutlich doppelt vorhanden.",
            null, ExtractedArchive),
        new("Sicherungskopien", "Alte Sicherungen einzelner Dateien (.bak, .old …) – prüfen, ob sie noch gebraucht werden.",
            null, f => BackupExt.Contains(f.Extension)),
        new("Absturzabbilder & große Protokolle", "Speicherabbilder nach Programmabstürzen und Protokolldateien über 10 MB – nur für die Fehlersuche nötig.",
            d => DumpDirs.Contains(d.Name),
            f => DumpExt.Contains(f.Extension) || (f.Extension == "log" && f.Size >= 10L * 1024 * 1024)),
        new("Software-Abhängigkeiten", "node_modules, Paket-Caches und Build-Ordner von Entwicklungsprojekten lassen sich jederzeit neu herunterladen bzw. erzeugen.",
            d => DevDirs.Contains(d.Name), null),
        new("Virtuelle Festplatten", "Festplatten virtueller Maschinen – oft vergessene Test-VMs oder alte Sicherungen.",
            null, f => VmExt.Contains(f.Extension) && f.Size >= 100L * 1024 * 1024),
        new("Outlook-Datendateien", ".ost-Dateien sind nur ein Zwischenspeicher des Postfachs und werden neu erstellt; .pst-Archive prüfen.",
            null, f => f.Extension is "ost" or "pst"),
        new("Auslagerung & Ruhezustand", "Vom System verwaltet. Den Ruhezustand (hiberfil.sys) kann man mit „powercfg /h off“ abschalten, wenn er nicht genutzt wird.",
            null, f => SystemFiles.Contains(f.Name) && f.Parent?.Parent == null),
    };

    /* ------------------------------------------------------------------ Kategorien */

    private static readonly Dictionary<string, string> Categories = BuildCategories();

    private static Dictionary<string, string> BuildCategories()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) d[e] = cat; }
        Add("Videos", "mp4 mkv avi mov wmv m4v mpg mpeg webm flv ts mts m2ts vob 3gp");
        Add("Bilder", "jpg jpeg png gif bmp tif tiff heic heif webp raw cr2 cr3 nef arw dng psd svg ico");
        Add("Musik & Audio", "mp3 wav flac aac m4a ogg wma aiff alac opus");
        Add("Dokumente", "pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf csv md pages numbers key one vsdx");
        Add("Archive", "zip rar 7z tar gz tgz bz2 xz cab");
        Add("Installer & Abbilder", "iso dmg msi msix msixbundle appx pkg mpkg img exe");
        Add("Virtuelle Maschinen", "vhd vhdx vmdk vdi qcow2 ova avhdx vmem vmsn");
        Add("Sicherungen", "bak old backup orig wbk bkf tib vbk vib sav");
        Add("Programme & Bibliotheken", "dll so dylib sys app jar msp");
        Add("E-Mail", "pst ost eml msg mbox emlx");
        Add("Datenbanken", "db sqlite mdf ldf ndf accdb mdb sql");
        Add("Temporär & Protokolle", "tmp temp log etl dmp mdmp hdmp cache");
        Add("Quellcode", "cs js ts py java c cpp h php html css json xml yml yaml");
        return d;
    }

    public static string CategoryOf(string ext) => Categories.TryGetValue(ext ?? "", out var c) ? c : "Sonstige";
}
