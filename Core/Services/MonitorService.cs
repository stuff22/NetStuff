using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

public class MonitorTarget : Bindable
{
    private string _status = "Unbekannt", _lastValue = "", _lastCheck = "", _since = "";
    private Level _level = Level.Info;

    public string Name { get; set; }
    public string Type { get; set; } = "Ping";      // Ping | TCP | HTTP | DNS | TLS
    public string Target { get; set; }
    public int Port { get; set; }
    public int IntervalSec { get; set; } = 30;
    public int Threshold { get; set; } = 3;
    public bool Enabled { get; set; } = true;

    [JsonIgnore] public string Status { get => _status; set { Set(ref _status, value); } }
    [JsonIgnore] public Level Level { get => _level; set => Set(ref _level, value); }
    [JsonIgnore] public string LastValue { get => _lastValue; set => Set(ref _lastValue, value); }
    [JsonIgnore] public string LastCheck { get => _lastCheck; set => Set(ref _lastCheck, value); }
    [JsonIgnore] public string Since { get => _since; set => Set(ref _since, value); }
    [JsonIgnore] public int Checks { get; set; }
    [JsonIgnore] public int Successes { get; set; }
    [JsonIgnore] public int Fails { get; set; }
    [JsonIgnore] public DateTime NextRun { get; set; }
    [JsonIgnore] public bool Running { get; set; }
    [JsonIgnore] public List<double?> History { get; } = new();
    [JsonIgnore] public string Availability => Checks == 0 ? "–" : $"{100.0 * Successes / Checks:0.##} %";
    [JsonIgnore] public string Display => Type switch
    {
        "TCP" => $"{Target}:{Port}",
        "TLS" => Port is 0 or 443 ? Target : $"{Target}:{Port}",
        _ => Target,
    };

    public void Touch() { Raise(nameof(Availability)); }
}

public class MonitorEvent
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string TimeText => Time.ToString("dd.MM. HH:mm:ss");
    public string Name { get; init; }
    public string Message { get; init; }
    public Level Level { get; init; }
}

/// <summary>Überwacht Hosts/Dienste im Hintergrund und meldet Ausfälle per Systembenachrichtigung.</summary>
public static class MonitorService
{
    public static ObservableCollection<MonitorTarget> Targets { get; } = new(JsonStore.LoadList<MonitorTarget>("monitors.json"));
    public static ObservableCollection<MonitorEvent> Events { get; } = new();
    public static bool IsRunning { get; private set; }
    public static event EventHandler Updated;

    private static CancellationTokenSource _cts;
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
        { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Net-Stuff-Monitor/1.0");
        return h;
    }

    public static void Save() => JsonStore.SaveList("monitors.json", Targets);

    public static async void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        AddEvent("Überwachung", L.T("Überwachung gestartet"), Level.Info);
        foreach (var t in Targets) t.NextRun = DateTime.Now;
        while (!ct.IsCancellationRequested)
        {
            foreach (var t in Targets.Where(t => t.Enabled && !t.Running && t.NextRun <= DateTime.Now).ToList())
                _ = CheckAsync(t, ct);
            try { await Task.Delay(1000, ct); } catch { break; }
        }
        IsRunning = false;
        AddEvent("Überwachung", L.T("Überwachung gestoppt"), Level.Info);
        Updated?.Invoke(null, EventArgs.Empty);
    }

    public static void Stop() => _cts?.Cancel();

    private static async Task CheckAsync(MonitorTarget t, CancellationToken ct)
    {
        t.Running = true;
        t.NextRun = DateTime.Now.AddSeconds(Math.Max(5, t.IntervalSec));
        (bool ok, double? ms, string value, bool warn) r;
        try { r = await RunCheck(t, ct); }
        catch (Exception ex) { r = (false, null, NetUtil.ErrorText(ex), false); }
        if (ct.IsCancellationRequested) { t.Running = false; return; }

        t.Checks++;
        t.LastCheck = DateTime.Now.ToString("HH:mm:ss");
        t.LastValue = L.T(r.value);
        t.History.Add(r.ok ? r.ms : null);
        if (t.History.Count > 500) t.History.RemoveRange(0, 100);

        if (r.ok)
        {
            t.Successes++;
            var wasDown = t.Level == Level.Bad;
            t.Fails = 0;
            var newLevel = r.warn ? Level.Warn : Level.Ok;
            if (t.Level != newLevel)
            {
                t.Since = DateTime.Now.ToString("dd.MM. HH:mm");
                if (wasDown)
                {
                    AddEvent(t.Name, L.F("wieder erreichbar ({0})", t.LastValue), Level.Ok);
                    Notifier.Show(L.F("✓ {0} wieder erreichbar", t.Name), t.Display + " · " + t.LastValue);
                }
                else if (newLevel == Level.Warn)
                {
                    AddEvent(t.Name, L.F("Warnung: {0}", t.LastValue), Level.Warn);
                    Notifier.Show(L.F("⚠ {0}", t.Name), t.LastValue);
                }
            }
            t.Level = newLevel;
            t.Status = L.T(r.warn ? "Warnung" : "Online");
        }
        else
        {
            t.Fails++;
            if (t.Fails >= Math.Max(1, t.Threshold) && t.Level != Level.Bad)
            {
                t.Level = Level.Bad;
                t.Status = L.T("Offline");
                t.Since = DateTime.Now.ToString("dd.MM. HH:mm");
                AddEvent(t.Name, L.F("AUSFALL: {0}", t.LastValue), Level.Bad);
                Notifier.Show(L.F("✗ {0} ist nicht erreichbar", t.Name), t.Display + " · " + t.LastValue);
            }
            else if (t.Level != Level.Bad)
            {
                t.Status = L.F("Fehler {0}/{1}", t.Fails, t.Threshold);
                t.Level = Level.Warn;
            }
        }
        t.Touch();
        t.Running = false;
        Updated?.Invoke(null, EventArgs.Empty);
    }

    private static async Task<(bool, double?, string, bool)> RunCheck(MonitorTarget t, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        switch (t.Type)
        {
            case "TCP":
            {
                var ip = await NetUtil.ResolveAsync(t.Target, false, ct);
                bool ok = await NetUtil.TcpConnectAsync(ip, t.Port, 4000, ct);
                return ok ? (true, sw.Elapsed.TotalMilliseconds, $"Port {t.Port} offen · {sw.ElapsedMilliseconds} ms", false)
                          : (false, null, $"Port {t.Port} nicht erreichbar", false);
            }
            case "HTTP":
            {
                var url = t.Target.Contains("://") ? t.Target : "https://" + t.Target;
                using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                int code = (int)res.StatusCode;
                var v = $"HTTP {code} · {sw.ElapsedMilliseconds} ms";
                return code < 400 ? (true, sw.Elapsed.TotalMilliseconds, v, false) : (false, null, v, false);
            }
            case "DNS":
            {
                var addrs = await Dns.GetHostAddressesAsync(t.Target, ct);
                return addrs.Length > 0 ? (true, sw.Elapsed.TotalMilliseconds, string.Join(", ", addrs.Take(3).Select(a => a.ToString())), false)
                                        : (false, null, "keine Adresse", false);
            }
            case "TLS":
            {
                int port = t.Port > 0 ? t.Port : 443;
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(10000);
                await tcp.ConnectAsync(t.Target, port, cts.Token);
                X509Certificate2 cert = null;
                using var ssl = new SslStream(tcp.GetStream(), false, (_, c, _, _) => { if (c != null) cert = new X509Certificate2(c); return true; });
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = t.Target }, cts.Token);
                if (cert == null) return (false, null, "kein Zertifikat", false);
                var days = (cert.NotAfter - DateTime.Now).TotalDays;
                var v = $"Zertifikat gültig bis {cert.NotAfter:dd.MM.yyyy} ({days:0} Tage)";
                if (days < 0) return (false, null, $"Zertifikat abgelaufen seit {cert.NotAfter:dd.MM.yyyy}", false);
                return (true, sw.Elapsed.TotalMilliseconds, v, days < 14);
            }
            default:
            {
                var ip = await NetUtil.ResolveAsync(t.Target, false, ct);
                var rtt = await NetUtil.PingAsync(ip, 2000, ct);
                return rtt.HasValue ? (true, (double)rtt.Value, $"{rtt} ms", false) : (false, null, "keine Antwort", false);
            }
        }
    }

    private static void AddEvent(string name, string message, Level level)
    {
        Events.Insert(0, new MonitorEvent { Name = name, Message = message, Level = level });
        while (Events.Count > 500) Events.RemoveAt(Events.Count - 1);
    }
}
