using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// MAC-Adressen aus der ARP-Tabelle von macOS („arp -an“). Für noch unbekannte Adressen wird ein UDP-Paket
/// geschickt, damit das System die Adresse auflöst; danach wird die Tabelle kurz beobachtet.
/// </summary>
public sealed partial class MacArpResolver : IArpResolver
{
    [GeneratedRegex(@"\((\d+\.\d+\.\d+\.\d+)\)\s+at\s+([0-9a-fA-F]{1,2}(?:[:-][0-9a-fA-F]{1,2}){5})(?![0-9a-fA-F:])", RegexOptions.IgnoreCase)]
    private static partial Regex Entry();

    private readonly object _lock = new();
    private Dictionary<string, byte[]> _table = new();
    private DateTime _read = DateTime.MinValue;

    public MacArpResolver()
    {
        // Der Netzwerkscanner fragt viele Adressen parallel (blockierend) ab
        ThreadPool.GetMinThreads(out var worker, out var io);
        if (worker < 64) ThreadPool.SetMinThreads(64, io);
    }

    /// <summary>Letzter Fehler beim Lesen der Tabelle (für „--diag“).</summary>
    public static string LastError { get; private set; } = "";

    public IReadOnlyDictionary<string, byte[]> Table()
    {
        lock (_lock)
        {
            _table = ReadTable();
            _read = DateTime.UtcNow;
            return _table;
        }
    }

    public byte[] Resolve(IPAddress ip)
    {
        var key = ip.ToString();
        var mac = Lookup(key, 500);
        if (mac != null) return mac;

        Nudge(ip);
        var until = DateTime.UtcNow.AddMilliseconds(1000);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(150);
            mac = Lookup(key, 120);
            if (mac != null) return mac;
        }
        return null;
    }

    private byte[] Lookup(string ip, int maxAgeMs)
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _read).TotalMilliseconds > maxAgeMs)
            {
                _table = ReadTable();
                _read = DateTime.UtcNow;
            }
            return _table.TryGetValue(ip, out var m) ? m : null;
        }
    }

    private static void Nudge(IPAddress ip)
    {
        try
        {
            using var u = new UdpClient(AddressFamily.InterNetwork);
            u.Send(new byte[] { 0 }, 1, new IPEndPoint(ip, 9));
        }
        catch { }
    }

    internal static Dictionary<string, byte[]> Parse(string output)
    {
        var t = new Dictionary<string, byte[]>();
        foreach (Match m in Entry().Matches(output))
        {
            var bytes = m.Groups[2].Value.Split(':', '-').Select(p => Convert.ToByte(p, 16)).ToArray();
            if (bytes.All(b => b == 0xFF) || bytes.All(b => b == 0)) continue;
            t[m.Groups[1].Value] = bytes;
        }
        return t;
    }

    private static Dictionary<string, byte[]> ReadTable()
    {
        try
        {
            var exe = File.Exists("/usr/sbin/arp") ? "/usr/sbin/arp" : "arp";
            var psi = new ProcessStartInfo(exe, "-an") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var t = Parse(output);
            LastError = t.Count == 0 ? $"keine Einträge erkannt (Exit {p.ExitCode}) {errTask.Result}".Trim() : "";
            return t;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return new Dictionary<string, byte[]>();
        }
    }
}
