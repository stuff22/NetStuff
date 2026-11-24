using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/* ====================================================================== ICMP */

/// <summary>Ergebnis eines einzelnen ICMP-Echos.</summary>
public record IcmpResult(IPStatus Status, IPAddress Address, double RttMs, int Ttl);

/// <summary>
/// Sendet ICMP-Echos. Standard: System.Net.NetworkInformation.Ping (Windows, macOS, Linux).
/// iOS erhält später eine eigene Umsetzung (SimplePing) über <see cref="Platform.Icmp"/>.
/// </summary>
public interface IIcmpSender
{
    Task<IcmpResult> SendAsync(IPAddress ip, int timeoutMs, byte[] payload, int ttl, bool dontFragment);
}

public sealed class SystemIcmpSender : IIcmpSender
{
    public async Task<IcmpResult> SendAsync(IPAddress ip, int timeoutMs, byte[] payload, int ttl, bool dontFragment)
    {
        var sw = Stopwatch.StartNew();
        using var ping = new Ping();
        // Bewusst ohne CancellationToken: Das Abbrechen eines laufenden ICMP-Requests kann in .NET (Windows)
        // zu einem Schreibzugriff auf bereits freigegebenen Speicher führen. Der Ping endet spätestens nach dem Timeout.
        var r = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(timeoutMs), payload, new PingOptions(ttl, dontFragment));
        double rtt = r.Status == IPStatus.Success && r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds;
        return new IcmpResult(r.Status, r.Address, rtt, r.Options?.Ttl ?? 0);
    }
}

public static class Icmp
{
    public static IIcmpSender Sender => Platform.Icmp ??= CreateDefault();

    /// <summary>
    /// macOS/iOS: eigener Datagramm-Sender (System.Net-Ping meldet dort bei TTL-Ablauf die Zieladresse statt des Routers
    /// und keine Antwort-TTL). Sonst: System.Net.NetworkInformation.Ping.
    /// </summary>
    private static IIcmpSender CreateDefault() =>
        (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) && DatagramIcmpSender.IsSupported()
            ? new DatagramIcmpSender()
            : new SystemIcmpSender();

    public static string StatusText(IPStatus s) => s switch
    {
        IPStatus.Success => "OK",
        IPStatus.TimedOut => "Zeitüberschreitung",
        IPStatus.DestinationHostUnreachable => "Host nicht erreichbar",
        IPStatus.DestinationNetworkUnreachable => "Netz nicht erreichbar",
        IPStatus.DestinationUnreachable => "Ziel nicht erreichbar",
        IPStatus.PacketTooBig => "Paket zu groß (DF)",
        IPStatus.TtlExpired => "TTL abgelaufen",
        IPStatus.BadDestination => "Ungültiges Ziel",
        _ => s.ToString(),
    };
}

/* ====================================================================== Ping */

public class PingSettings
{
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 2000;
    public int Size { get; set; } = 32;
    public bool DontFragment { get; set; }
}

/// <summary>Laufende Ping-Statistik eines Ziels (Verlust, Min/Max/Mittel, Jitter).</summary>
public class PingStats : Bindable
{
    private int _sent, _recv;
    private double? _last;
    private double _min = double.MaxValue, _max, _sum, _jitterSum;
    private int _jitterCount;
    private string _status = "";
    private string _ip = "";

    public string Host { get; init; }
    public string Ip { get => _ip; set => Set(ref _ip, value); }
    public int Sent => _sent;
    public int Received => _recv;
    public string Status { get => _status; set => Set(ref _status, value); }

    public double? LossPercent => _sent == 0 ? null : 100.0 * (_sent - _recv) / _sent;
    public double? Avg => _recv == 0 ? null : _sum / _recv;
    public double? Jitter => _jitterCount == 0 ? null : _jitterSum / _jitterCount;

    public string LossText => _sent == 0 ? "–" : $"{LossPercent:0.#} %";
    public string LastText => _last.HasValue ? $"{_last:0} ms" : "–";
    public string MinText => _recv == 0 ? "–" : $"{_min:0} ms";
    public string MaxText => _recv == 0 ? "–" : $"{_max:0} ms";
    public string AvgText => _recv == 0 ? "–" : $"{Avg:0.#} ms";
    public string JitterText => _jitterCount == 0 ? "–" : $"{Jitter:0.#} ms";

    /// <summary>Wird nach jeder Messung aufgerufen (z. B. für Diagramme).</summary>
    protected virtual void OnSample(double? rtt) { }

    public void Add(double? rtt, string status)
    {
        _sent++;
        if (rtt.HasValue)
        {
            if (_last.HasValue) { _jitterSum += Math.Abs(rtt.Value - _last.Value); _jitterCount++; }
            _recv++;
            _min = Math.Min(_min, rtt.Value);
            _max = Math.Max(_max, rtt.Value);
            _sum += rtt.Value;
        }
        _last = rtt;
        Status = status;
        OnSample(rtt);
        foreach (var p in new[] { nameof(Sent), nameof(Received), nameof(LossText), nameof(LastText), nameof(MinText), nameof(MaxText), nameof(AvgText), nameof(JitterText) })
            Raise(p);
    }
}

public static class PingRunner
{
    /// <summary>Kommagetrennte Zielliste bereinigen (max. 12 Ziele).</summary>
    public static List<string> ParseTargets(string text) =>
        (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(NetUtil.CleanHost).Where(h => h.Length > 0).Distinct().Take(12).ToList();

    /// <summary>Pingt ein Ziel dauerhaft bis zum Abbruch und schreibt die Ergebnisse in <paramref name="t"/>.</summary>
    public static async Task RunAsync(PingStats t, PingSettings s, CancellationToken ct)
    {
        IPAddress ip;
        try
        {
            ip = await NetUtil.ResolveAsync(t.Host, false, ct);
            t.Ip = ip.ToString();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            t.Status = L.T("DNS-Fehler: ") + ex.Message;
            return;
        }

        var buffer = new byte[s.Size];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)('a' + i % 23);

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await Icmp.Sender.SendAsync(ip, s.TimeoutMs, buffer, 128, s.DontFragment);
                if (ct.IsCancellationRequested) break;
                if (r.Status == IPStatus.Success) t.Add(r.RttMs, r.Ttl > 0 ? $"OK (TTL {r.Ttl})" : "OK");
                else t.Add(null, L.T(Icmp.StatusText(r.Status)));
            }
            catch (Exception ex)
            {
                t.Add(null, ex.InnerException?.Message ?? ex.Message);
            }
            var wait = s.IntervalMs - (int)sw.ElapsedMilliseconds;
            try { await Task.Delay(Math.Max(50, wait), ct); } catch { break; }
        }
    }
}

/* ====================================================================== Path-MTU */

public record PathMtuResult(bool Reachable, int Mtu, int Payload, int Mss, bool IPv6, IPStatus FirstStatus);

public static class PathMtu
{
    /// <summary>Binäre Suche der größten unfragmentierten Paketgröße (DF-Bit). <paramref name="log"/> erhält Fortschrittszeilen.</summary>
    public static async Task<PathMtuResult> DiscoverAsync(IPAddress ip, int maxMtu, Action<string> log, CancellationToken ct = default)
    {
        bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        int overhead = v6 ? 48 : 28;
        log?.Invoke(L.F("Ziel: {0}  ({1} Byte)", ip, v6 ? "IPv6, Header 40 + ICMPv6 8" : "IPv4, Header 20 + ICMP 8"));

        async Task<IPStatus> Probe(int payload)
        {
            IPStatus last = IPStatus.Unknown;
            for (int i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await Icmp.Sender.SendAsync(ip, 2000, new byte[payload], 64, true);
                    last = r.Status;
                    if (last == IPStatus.Success || last == IPStatus.PacketTooBig) return last;
                }
                catch (PingException ex) { log?.Invoke(L.T("  Fehler: ") + (ex.InnerException?.Message ?? ex.Message)); last = IPStatus.PacketTooBig; }
            }
            return last;
        }

        var first = await Probe(32);
        if (first != IPStatus.Success)
        {
            log?.Invoke(L.F("Ziel antwortet nicht auf Ping ({0}) – Messung nicht möglich.", L.T(Icmp.StatusText(first))));
            return new PathMtuResult(false, 0, 0, 0, v6, first);
        }

        int lo = 32, hi = maxMtu - overhead;
        if (await Probe(hi) == IPStatus.Success)
        {
            lo = hi;
            log?.Invoke(L.F("  {0,5} Byte  ✓  (Maximum erreicht)", hi + overhead));
        }
        else
        {
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                var st = await Probe(mid);
                log?.Invoke($"  {mid + overhead,5} Byte  {(st == IPStatus.Success ? "✓" : "✗  " + L.T(Icmp.StatusText(st)))}");
                if (st == IPStatus.Success) lo = mid;
                else hi = mid - 1;
            }
        }
        int mtu = lo + overhead;
        return new PathMtuResult(true, mtu, lo, mtu - (v6 ? 60 : 40), v6, first);
    }
}

/* ====================================================================== Traceroute */

public class TraceHop : Bindable
{
    private string _host = "", _place = "", _isp = "";
    public int Hop { get; init; }
    public IPAddress Address { get; init; }
    public string Ip => Address?.ToString() ?? "*";
    public string Rtt { get; init; }
    public bool Reached { get; init; }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Place { get => _place; set => Set(ref _place, value); }
    public string Isp { get => _isp; set => Set(ref _isp, value); }
    public GeoInfo Geo { get; set; }
}

public static class Traceroute
{
    /// <summary>
    /// Traceroute per ICMP mit steigender TTL (3 Proben je Hop). Neue Hops werden in <paramref name="hops"/> eingefügt
    /// (auf dem aufrufenden Thread, damit eine Oberfläche direkt binden kann); Reverse-DNS läuft parallel.
    /// </summary>
    public static async Task RunAsync(IPAddress ip, int maxHops, int timeoutMs, IList<TraceHop> hops, Action<TraceHop> added, CancellationToken ct)
    {
        var nameTasks = new List<Task>();
        var payload = new byte[32];
        for (int ttl = 1; ttl <= maxHops && !ct.IsCancellationRequested; ttl++)
        {
            IPAddress hopIp = null;
            bool reached = false;
            var rtts = new List<string>();
            for (int probe = 0; probe < 3 && !ct.IsCancellationRequested; probe++)
            {
                try
                {
                    var r = await Icmp.Sender.SendAsync(ip, timeoutMs, payload, ttl, true);
                    if (r.Status is IPStatus.TtlExpired or IPStatus.Success or IPStatus.TimeExceeded)
                    {
                        hopIp ??= r.Address;
                        rtts.Add(r.RttMs < 1 ? "<1" : r.RttMs.ToString("0"));
                        if (r.Status == IPStatus.Success) reached = true;
                    }
                    else if (r.Status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable)
                    {
                        hopIp ??= r.Address;
                        rtts.Add("!H");
                    }
                    else rtts.Add("*");
                }
                catch { rtts.Add("*"); }
            }
            ct.ThrowIfCancellationRequested();

            var row = new TraceHop
            {
                Hop = ttl,
                Address = hopIp,
                Rtt = string.Join(" / ", rtts) + (rtts.Any(r => r != "*" && r != "!H") ? " ms" : ""),
                Reached = reached,
            };
            hops.Add(row);
            added?.Invoke(row);
            if (hopIp != null) nameTasks.Add(ResolveNameAsync(row));
            if (reached) break;
            if (hops.Count >= 5 && hops.Skip(hops.Count - 5).All(h => h.Address == null))
            {
                for (int i = 0; i < 4; i++) hops.RemoveAt(hops.Count - 1);
                hops[^1].Host = L.T("… keine Antworten mehr – Ziel oder Router filtern ICMP");
                break;
            }
        }
        await Task.WhenAll(nameTasks);
    }

    private static async Task ResolveNameAsync(TraceHop row) => row.Host = await NetUtil.ReverseDnsAsync(row.Address, 2500);

    /// <summary>Standort und Provider der Hops ergänzen (Geo-IP).</summary>
    public static async Task AddGeoAsync(IEnumerable<TraceHop> hops, CancellationToken ct)
    {
        var list = hops.Where(h => h.Address != null).ToList();
        var geo = await GeoIpService.LookupAsync(list.Select(h => h.Address), ct);
        foreach (var h in list)
        {
            if (NetUtil.IsPrivate(h.Address)) { h.Place = L.T("privates Netz"); continue; }
            if (geo.TryGetValue(h.Ip, out var g) && g.Ok)
            {
                h.Geo = g;
                h.Place = g.Location;
                h.Isp = string.Join(" · ", new[] { g.Isp, g.As?.Split(' ').FirstOrDefault() }.Where(s => !string.IsNullOrEmpty(s)));
            }
        }
    }

    /// <summary>Punkte für die Karte (Assets/map.html, Funktion setPoints). Gleiche Orte hintereinander werden zusammengefasst.</summary>
    public static List<object> MapPoints(GeoInfo self, IList<TraceHop> hops)
    {
        var points = new List<object>();
        if (self?.Ok == true && hops.Any(h => h.Geo != null))
            points.Add(new { hop = 0, ip = self.Ip, host = "", lat = self.Lat, lon = self.Lon, place = self.Location, isp = self.Isp, rtt = "", kind = "start" });

        var withGeo = hops.Where(h => h.Geo != null).ToList();
        for (int i = 0; i < withGeo.Count; i++)
        {
            var h = withGeo[i];
            if (i > 0 && withGeo[i - 1].Geo.Lat == h.Geo.Lat && withGeo[i - 1].Geo.Lon == h.Geo.Lon && i < withGeo.Count - 1) continue;
            points.Add(new
            {
                hop = h.Hop, ip = h.Ip, host = h.Host, lat = h.Geo.Lat, lon = h.Geo.Lon, place = h.Place, isp = h.Isp, rtt = h.Rtt,
                kind = i == withGeo.Count - 1 && h.Reached ? "end" : "hop",
            });
        }
        return points;
    }

    public static string ToText(IEnumerable<TraceHop> hops) =>
        string.Join("\n", hops.Select(h => $"{h.Hop,3}  {h.Ip,-40} {h.Rtt,-22} {h.Host}  {h.Place}  {h.Isp}"));
}
