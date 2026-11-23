using System.IO;
using System.Net.Http;

namespace NetStuff.Services;

/// <summary>
/// MAC-Hersteller-Datenbank. Wird beim ersten Gebrauch heruntergeladen (Wireshark "manuf", Fallback IEEE)
/// und im Benutzerprofil zwischengespeichert.
/// </summary>
public static class OuiDatabase
{
    private static readonly string CacheFile = Path.Combine(Platform.DataDir, "manuf.txt");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // Präfixlänge (Bits) -> (Präfixwert -> Hersteller)
    private static Dictionary<int, Dictionary<ulong, string>> _map;

    public static bool IsLoaded => _map != null;
    public static int Count => _map?.Values.Sum(d => d.Count) ?? 0;
    public static DateTime? CacheDate => File.Exists(CacheFile) ? File.GetLastWriteTime(CacheFile) : null;

    public static async Task EnsureLoadedAsync(bool forceDownload = false)
    {
        if (_map != null && !forceDownload) return;
        await Gate.WaitAsync();
        try
        {
            if (_map != null && !forceDownload) return;
            bool stale = !File.Exists(CacheFile) || (DateTime.Now - File.GetLastWriteTime(CacheFile)).TotalDays > 90;
            if (forceDownload || stale)
            {
                try { await DownloadAsync(); }
                catch when (File.Exists(CacheFile)) { /* alte Datei weiterverwenden */ }
            }
            if (File.Exists(CacheFile)) _map = Parse(await File.ReadAllLinesAsync(CacheFile));
            else _map = new Dictionary<int, Dictionary<ulong, string>>();
        }
        finally { Gate.Release(); }
    }

    private static async Task DownloadAsync()
    {
        string[] sources =
        {
            "https://www.wireshark.org/download/automated/data/manuf",
            "https://standards-oui.ieee.org/oui/oui.csv",
        };
        Exception last = null;
        foreach (var url in sources)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var res = await NetUtil.Http.SendAsync(req);
                res.EnsureSuccessStatusCode();
                var text = await res.Content.ReadAsStringAsync();
                if (url.EndsWith(".csv")) text = ConvertIeeeCsv(text);
                if (text.Length < 10000) throw new Exception("Datei unvollständig");
                await File.WriteAllTextAsync(CacheFile, text);
                return;
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new Exception("Download fehlgeschlagen");
    }

    /// <summary>IEEE-CSV (Registry,Assignment,Organization Name,...) ins manuf-Format umwandeln.</summary>
    private static string ConvertIeeeCsv(string csv)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var cols = SplitCsv(line);
            if (cols.Count < 3 || cols[1].Length != 6) continue;
            var a = cols[1];
            sb.Append($"{a[..2]}:{a[2..4]}:{a[4..6]}\t{cols[2].Trim()}\t{cols[2].Trim()}\n");
        }
        return sb.ToString();
    }

    private static List<string> SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool q = false;
        foreach (var c in line.TrimEnd('\r'))
        {
            if (c == '"') q = !q;
            else if (c == ',' && !q) { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        res.Add(cur.ToString());
        return res;
    }

    private static Dictionary<int, Dictionary<ulong, string>> Parse(string[] lines)
    {
        var map = new Dictionary<int, Dictionary<ulong, string>>();
        foreach (var raw in lines)
        {
            if (raw.Length < 8 || raw[0] == '#') continue;
            var cols = raw.Split('\t');
            if (cols.Length < 2) continue;
            var key = cols[0].Trim();
            int bits = 24;
            var slash = key.IndexOf('/');
            if (slash > 0)
            {
                bits = int.Parse(key[(slash + 1)..]);
                key = key[..slash];
            }
            var hex = key.Replace(":", "").Replace("-", "").Replace(".", "");
            if (hex.Length < 6) continue;
            if (!ulong.TryParse(hex.PadRight(12, '0')[..12], System.Globalization.NumberStyles.HexNumber, null, out var v)) continue;
            var prefix = v >> (48 - bits);
            var name = cols.Length >= 3 && cols[2].Trim().Length > 0 ? cols[2].Trim() : cols[1].Trim();
            if (!map.TryGetValue(bits, out var d)) map[bits] = d = new Dictionary<ulong, string>();
            d[prefix] = name;
        }
        return map;
    }

    public static string Lookup(string mac)
    {
        if (string.IsNullOrEmpty(mac)) return "";
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length < 6) return "";
        if (!ulong.TryParse(hex.PadRight(12, '0')[..12], System.Globalization.NumberStyles.HexNumber, null, out var v)) return "";

        if (_map != null)
        {
            foreach (var bits in _map.Keys.OrderByDescending(b => b))
            {
                if (_map[bits].TryGetValue(v >> (48 - bits), out var name)) return name;
            }
        }
        // Lokal administrierte Adresse (z. B. private/zufällige MAC bei Smartphones)
        if (((v >> 40) & 0x02) != 0) return "Private / zufällige MAC";
        return "";
    }

    public static bool IsRandomized(string mac)
    {
        var hex = new string((mac ?? "").Where(Uri.IsHexDigit).ToArray());
        return hex.Length >= 2 && (Convert.ToByte(hex[..2], 16) & 0x02) != 0;
    }
}
