using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetStuff.Services;

public class SpeedSample
{
    public double Seconds { get; init; }
    public double Mbps { get; init; }
}

/// <summary>Internet-Speedtest über die öffentlichen Endpunkte von speed.cloudflare.com.</summary>
public static class CloudflareSpeed
{
    private const string Base = "https://speed.cloudflare.com/";

    private static HttpClient NewClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(NetUtil.UserAgent);
        return h;
    }

    public static async Task<Dictionary<string, string>> MetaAsync(CancellationToken ct)
    {
        // Die Metadaten liefert Cloudflare als Header jeder Messanfrage (colo, city, country, asn, cf-meta-ip)
        var res = new Dictionary<string, string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(6000);
            using var r = await NetUtil.Http.GetAsync(Base + "__down?bytes=0", cts.Token);
            foreach (var h in r.Headers) res[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
            if (res.TryGetValue("cf-meta-ip", out var ip)) res["clientIp"] = ip;
            if (res.TryGetValue("asn", out var asn)) res["asOrganization"] = "AS" + asn;
        }
        catch { }
        return res;
    }

    /// <summary>Latenz (Median) und Jitter über TCP-Verbindungsaufbau (reine Netzlaufzeit, ohne HTTP-Verarbeitung).</summary>
    public static async Task<(double Latency, double Jitter)> LatencyAsync(int count, CancellationToken ct)
    {
        var ip = (await Dns.GetHostAddressesAsync("speed.cloudflare.com", ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var t = new List<double>();
        for (int i = 0; i < count; i++)
        {
            using var c = new TcpClient();
            var sw = Stopwatch.StartNew();
            await c.ConnectAsync(ip, 443, ct);
            t.Add(sw.Elapsed.TotalMilliseconds);
            await Task.Delay(100, ct);
        }
        var sorted = t.OrderBy(x => x).ToList();
        double median = sorted[sorted.Count / 2];
        double jitter = t.Zip(t.Skip(1), (a, b) => Math.Abs(a - b)).DefaultIfEmpty(0).Average();
        return (median, jitter);
    }

    public static async Task<double> DownloadAsync(int seconds, int parallel, IProgress<SpeedSample> progress, CancellationToken ct)
    {
        using var http = NewClient();
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, parallel).Select(async _ =>
        {
            var buf = new byte[81920];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var r = await http.GetAsync(Base + "__down?bytes=50000000", HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    using var s = await r.Content.ReadAsStreamAsync(stop.Token);
                    int n;
                    while ((n = await s.ReadAsync(buf, stop.Token)) > 0) Interlocked.Add(ref total, n);
                }
                catch when (stop.IsCancellationRequested) { }
                catch { await Task.Delay(200); }
            }
        }).ToList();

        return await Measure(seconds, () => Interlocked.Read(ref total), sw, progress, stop, workers);
    }

    public static async Task<double> UploadAsync(int seconds, int parallel, IProgress<SpeedSample> progress, CancellationToken ct)
    {
        using var http = NewClient();
        long total = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sw = Stopwatch.StartNew();
        var chunk = new byte[65536];
        Random.Shared.NextBytes(chunk);

        var workers = Enumerable.Range(0, parallel).Select(async _ =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var content = new CountingContent(chunk, 8 * 1024 * 1024, n => Interlocked.Add(ref total, n), stop.Token);
                    using var r = await http.PostAsync(Base + "__up", content, stop.Token);
                }
                catch when (stop.IsCancellationRequested) { }
                catch { await Task.Delay(200); }
            }
        }).ToList();

        return await Measure(seconds, () => Interlocked.Read(ref total), sw, progress, stop, workers);
    }

    private static async Task<double> Measure(int seconds, Func<long> bytes, Stopwatch sw, IProgress<SpeedSample> progress, CancellationTokenSource stop, List<Task> workers)
    {
        long last = 0;
        double lastT = 0;
        var samples = new List<double>();
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(250, stop.Token);
                long b = bytes();
                double t = sw.Elapsed.TotalSeconds;
                double mbps = (b - last) * 8 / 1e6 / Math.Max(0.001, t - lastT);
                last = b; lastT = t;
                samples.Add(mbps);
                progress?.Report(new SpeedSample { Seconds = t, Mbps = mbps });
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(workers); } catch { }
        }
        // Anlaufphase (erste 25 %) ignorieren, Mittel der Restmessung
        var steady = samples.Skip(samples.Count / 4).ToList();
        return steady.Count > 0 ? steady.Average() : 0;
    }

    private sealed class CountingContent : HttpContent
    {
        private readonly byte[] _chunk;
        private readonly long _size;
        private readonly Action<int> _onWrite;
        private readonly CancellationToken _ct;

        public CountingContent(byte[] chunk, long size, Action<int> onWrite, CancellationToken ct)
        {
            _chunk = chunk; _size = size; _onWrite = onWrite; _ct = ct;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            long sent = 0;
            while (sent < _size && !_ct.IsCancellationRequested)
            {
                int n = (int)Math.Min(_chunk.Length, _size - sent);
                await stream.WriteAsync(_chunk.AsMemory(0, n), _ct);
                sent += n;
                _onWrite(n);
            }
        }

        protected override bool TryComputeLength(out long length) { length = _size; return true; }
    }
}

public class IperfResult
{
    public string Direction { get; set; }
    public double ClientMbps { get; set; }
    public double ServerMbps { get; set; }
    public long Bytes { get; set; }
    public int Retransmits { get; set; } = -1;
    public double Mbps => ServerMbps > 0 ? ServerMbps : ClientMbps;
}

/// <summary>
/// iPerf3-Client (TCP) – spricht das iperf3-Protokoll direkt, keine iperf3.exe nötig.
/// </summary>
public static class IperfClient
{
    private const byte TestStart = 1, TestRunning = 2, TestEnd = 4, ParamExchange = 9, CreateStreams = 10,
        ServerTerminate = 11, ExchangeResults = 13, DisplayResults = 14, IperfDone = 16, AccessDenied = 0xFF, ServerError = 0xFE;

    public static async Task<IperfResult> RunAsync(string host, int port, int seconds, int streams, bool reverse,
        IProgress<SpeedSample> progress, CancellationToken ct)
    {
        var cookie = MakeCookie();
        using var ctrl = new TcpClient { NoDelay = true };
        using (var cc = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cc.CancelAfter(8000);
            await ctrl.ConnectAsync(host, port, cc.Token);
        }
        await ctrl.GetStream().WriteAsync(cookie, ct);

        var started = new StrongBox<bool>(false);
        try
        {
            return await RunCoreAsync(host, port, seconds, streams, reverse, progress, ct, cookie, ctrl, started);
        }
        catch (Exception ex) when ((ex is IOException or SocketException) && !started.Value)
        {
            // Server schließt die Verbindung vor Testbeginn → belegt / nicht bereit
            throw new IperfBusyException();
        }
    }

    private static async Task<IperfResult> RunCoreAsync(string host, int port, int seconds, int streams, bool reverse,
        IProgress<SpeedSample> progress, CancellationToken ct, byte[] cookie, TcpClient ctrl, StrongBox<bool> started)
    {
        var cs = ctrl.GetStream();
        var data = new List<TcpClient>();
        long total = 0;
        bool measuring = false;
        var transfers = new List<Task>();
        using var stopData = new CancellationTokenSource();
        var result = new IperfResult { Direction = reverse ? "Download" : "Upload" };
        double elapsed = seconds;

        try
        {
            while (true)
            {
                byte state = await ReadByte(cs, ct, 30000);
                switch (state)
                {
                    case ParamExchange:
                        var param = new Dictionary<string, object>
                        {
                            ["tcp"] = true, ["omit"] = 0, ["time"] = seconds, ["num"] = 0, ["blockcount"] = 0,
                            ["parallel"] = streams, ["len"] = 131072, ["pacing_timer"] = 1000, ["client_version"] = "3.17.1",
                        };
                        if (reverse) param["reverse"] = true;
                        await WriteJson(cs, param, ct);
                        break;

                    case CreateStreams:
                        for (int i = 0; i < streams; i++)
                        {
                            var d = new TcpClient();
                            await d.ConnectAsync(host, port, ct);
                            await d.GetStream().WriteAsync(cookie, ct);
                            data.Add(d);
                        }
                        break;

                    case TestStart:
                        break;

                    case TestRunning:
                        started.Value = true;
                        measuring = true;
                        foreach (var d in data)
                        {
                            var s = d.GetStream();
                            transfers.Add(reverse
                                ? Task.Run(async () =>
                                {
                                    var buf = new byte[131072];
                                    try
                                    {
                                        int n;
                                        while ((n = await s.ReadAsync(buf, stopData.Token)) > 0)
                                            if (measuring) Interlocked.Add(ref total, n);
                                    }
                                    catch { }
                                })
                                : Task.Run(async () =>
                                {
                                    var buf = new byte[131072];
                                    Random.Shared.NextBytes(buf);
                                    try
                                    {
                                        while (measuring && !stopData.IsCancellationRequested)
                                        {
                                            await s.WriteAsync(buf, stopData.Token);
                                            Interlocked.Add(ref total, buf.Length);
                                        }
                                    }
                                    catch { }
                                }));
                        }

                        var sw = Stopwatch.StartNew();
                        long last = 0; double lastT = 0;
                        while (sw.Elapsed.TotalSeconds < seconds && !ct.IsCancellationRequested)
                        {
                            try { await Task.Delay(500, ct); } catch { break; }
                            long b = Interlocked.Read(ref total);
                            double t = sw.Elapsed.TotalSeconds;
                            progress?.Report(new SpeedSample { Seconds = t, Mbps = (b - last) * 8 / 1e6 / Math.Max(0.001, t - lastT) });
                            last = b; lastT = t;
                        }
                        elapsed = sw.Elapsed.TotalSeconds;
                        measuring = false;
                        result.Bytes = Interlocked.Read(ref total);
                        result.ClientMbps = result.Bytes * 8 / 1e6 / Math.Max(0.1, elapsed);
                        if (!reverse) stopData.Cancel();
                        await cs.WriteAsync(new[] { TestEnd }, CancellationToken.None);
                        break;

                    case ExchangeResults:
                        var mine = new Dictionary<string, object>
                        {
                            ["cpu_util_total"] = 0, ["cpu_util_user"] = 0, ["cpu_util_system"] = 0, ["sender_has_retransmits"] = 0,
                            ["streams"] = data.Select((_, i) => new Dictionary<string, object>
                            {
                                ["id"] = i == 0 ? 1 : i + 2, ["bytes"] = result.Bytes / Math.Max(1, data.Count), ["retransmits"] = 0,
                                ["jitter"] = 0, ["errors"] = 0, ["omitted_errors"] = 0, ["packets"] = 0, ["omitted_packets"] = 0,
                                ["start_time"] = 0, ["end_time"] = elapsed,
                            }).ToList(),
                        };
                        await WriteJson(cs, mine, ct);
                        var serverJson = await ReadJson(cs, ct);
                        ParseServerResults(serverJson, result, elapsed);
                        break;

                    case DisplayResults:
                        await cs.WriteAsync(new[] { IperfDone }, ct);
                        return result;

                    case AccessDenied:
                        throw new IperfBusyException();

                    case ServerError:
                        throw new Exception("iPerf3-Server meldet einen Fehler.");

                    case ServerTerminate:
                        throw new Exception("iPerf3-Server hat den Test abgebrochen.");
                }
            }
        }
        finally
        {
            stopData.Cancel();
            foreach (var d in data) d.Dispose();
            try { await Task.WhenAll(transfers); } catch { }
        }
    }

    private static void ParseServerResults(string json, IperfResult result, double elapsed)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            long bytes = 0; double end = 0; int retr = 0; bool hasRetr = false;
            if (doc.RootElement.TryGetProperty("sender_has_retransmits", out var shr) && shr.ValueKind == JsonValueKind.Number && shr.GetInt32() == 1) hasRetr = true;
            foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
            {
                bytes += s.GetProperty("bytes").GetInt64();
                if (s.TryGetProperty("end_time", out var et)) end = Math.Max(end, et.GetDouble());
                if (s.TryGetProperty("retransmits", out var rt) && rt.GetInt32() > 0) retr += rt.GetInt32();
            }
            if (end <= 0) end = elapsed;
            result.ServerMbps = bytes * 8 / 1e6 / end;
            if (hasRetr) result.Retransmits = retr;
        }
        catch { }
    }

    private static byte[] MakeCookie()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz234567";
        var b = new byte[37];
        for (int i = 0; i < 36; i++) b[i] = (byte)chars[Random.Shared.Next(chars.Length)];
        return b;
    }

    private static async Task<byte> ReadByte(NetworkStream s, CancellationToken ct, int timeoutMs)
    {
        var buf = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        int n = await s.ReadAsync(buf, cts.Token);
        if (n == 0) throw new Exception("iPerf3-Server hat die Verbindung geschlossen.");
        return buf[0];
    }

    private static async Task WriteJson(NetworkStream s, object obj, CancellationToken ct)
    {
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj));
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, json.Length);
        await s.WriteAsync(len, ct);
        await s.WriteAsync(json, ct);
    }

    private static async Task<string> ReadJson(NetworkStream s, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct);
        int n = BinaryPrimitives.ReadInt32BigEndian(len);
        if (n <= 0 || n > 10_000_000) throw new Exception("Ungültige Antwort vom iPerf3-Server.");
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct);
        return Encoding.UTF8.GetString(buf);
    }
}

public class IperfBusyException : Exception
{
    public IperfBusyException() : base("iPerf3-Server ist gerade belegt.") { }
}
