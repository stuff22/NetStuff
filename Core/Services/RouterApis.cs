using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>
/// Geräte- und MAC-Listen über die Web-APIs gängiger Router:
/// UniFi OS (lokales Konto oder API-Schlüssel), OpenWrt (ubus/JSON-RPC), MikroTik RouterOS 7 (REST), OPNsense (API-Schlüssel).
/// Im Modus „Automatisch“ meldet sich nur, wer die Schnittstelle tatsächlich erkennt.
/// </summary>
public static partial class RouterHosts
{
    internal sealed class WebApiContext(IPAddress router, string user, string password, string webBase, bool explicitKind, RouterHostResult result)
    {
        public IPAddress Router { get; } = router;
        public string User { get; } = user;
        public string Password { get; } = password;
        public bool Explicit { get; } = explicitKind;
        public RouterHostResult Result { get; } = result;
        public bool HasCredentials => Password != "";

        public string Https(string path) => (webBase ?? $"https://{Router}") + path;
        public string Http(string path) => (webBase ?? $"http://{Router}") + path;

        /// <summary>Eigener Client je Abfrage: Router haben fast immer selbst signierte Zertifikate; Cookies für die Anmeldung.</summary>
        public HttpClient Client(int timeoutSec = 6)
        {
            var h = new HttpClientHandler
            {
                UseProxy = false,
                CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            return new HttpClient(h) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        }

        public void Note(string s) { lock (Result) Result.Notes.Add(s); }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.ToString(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "" }
            : "";

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name = null)
    {
        if (name != null) e = e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
        return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Resp)> SendAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        var resp = await http.SendAsync(req, ct);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct), resp);
    }

    private static JsonDocument TryJson(string body)
    {
        try { return JsonDocument.Parse(body); } catch { return null; }
    }

    /// <summary>Einträge einer Quelle (mehrere Abfragen laufen gleichzeitig).</summary>
    private static int Count(RouterHostResult r, string source) { lock (r) return r.Hosts.Values.Count(h => h.Source == source); }

    /* ------------------------------------------------------------------ UniFi OS */

    private static async Task UniFiAsync(WebApiContext w, CancellationToken ct)
    {
        try
        {
            using var http = w.Client();
            // Erkennen: Die Startseite von UniFi OS heißt „UniFi OS“ (UDM, UCG, UX, Cloud Key Gen2+)
            var home = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/")), ct);
            if (!home.Body.Contains("UniFi", StringComparison.OrdinalIgnoreCase)) return;
            if (!w.HasCredentials) { w.Note(L.T("UniFi OS erkannt – lokalen Benutzer oder API-Schlüssel eintragen")); return; }

                        if (w.User == "")
            {
                // API-Schlüssel (UniFi Network 9+: Einstellungen → Control Plane → Integrationen)
                http.DefaultRequestHeaders.Add("X-API-KEY", w.Password);
                var sites = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/proxy/network/integration/v1/sites")), ct);
                if (sites.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { w.Note(L.T("UniFi: API-Schlüssel abgelehnt")); return; }
                using var sdoc = TryJson(sites.Body);
                foreach (var site in Arr(sdoc?.RootElement ?? default, "data"))
                {
                    var id = Str(site, "id");
                    var clients = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https($"/proxy/network/integration/v1/sites/{id}/clients?limit=1000")), ct);
                    using (var cdoc = TryJson(clients.Body))
                        foreach (var c in Arr(cdoc?.RootElement ?? default, "data"))
                            Add(w.Result, Str(c, "ipAddress"), Str(c, "macAddress"), Str(c, "name"), true, "UniFi");
                    var devices = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https($"/proxy/network/integration/v1/sites/{id}/devices?limit=500")), ct);
                    using (var ddoc = TryJson(devices.Body))
                        foreach (var d in Arr(ddoc?.RootElement ?? default, "data"))
                            Add(w.Result, Str(d, "ipAddress"), Str(d, "macAddress"), Str(d, "name"), true, "UniFi", Str(d, "model"));
                }
            }
            else
            {
                // Lokales Konto (kein Ubiquiti-Konto mit Zwei-Faktor-Anmeldung)
                var login = new HttpRequestMessage(HttpMethod.Post, w.Https("/api/auth/login"))
                {
                    Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                        { ["username"] = w.User, ["password"] = w.Password, ["remember"] = false }), Encoding.UTF8, "application/json"),
                };
                var lr = await SendAsync(http, login, ct);
                if (lr.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
                {
                    w.Note(L.T("UniFi: Anmeldung abgelehnt (lokales Konto ohne Zwei-Faktor nötig)"));
                    return;
                }
                if (lr.Resp.Headers.TryGetValues("X-Csrf-Token", out var csrf)) http.DefaultRequestHeaders.Add("X-Csrf-Token", csrf.First());

                var siteNames = new List<string>();
                var self = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/proxy/network/api/self/sites")), ct);
                using (var sdoc = TryJson(self.Body))
                    siteNames.AddRange(Arr(sdoc?.RootElement ?? default, "data").Select(s => Str(s, "name")).Where(n => n != "").Take(20));
                if (siteNames.Count == 0) siteNames.Add("default");

                foreach (var site in siteNames)
                {
                    var sta = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https($"/proxy/network/api/s/{site}/stat/sta")), ct);
                    using (var doc = TryJson(sta.Body))
                        foreach (var c in Arr(doc?.RootElement ?? default, "data"))
                        {
                            var name = Str(c, "name") is { Length: > 0 } n ? n : Str(c, "hostname");
                            Add(w.Result, Str(c, "ip"), Str(c, "mac"), name, true, "UniFi");
                        }
                    // Eigene Geräte (Access Points, Switches, Gateway) mit Modell und Seriennummer
                    var dev = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https($"/proxy/network/api/s/{site}/stat/device")), ct);
                    using (var doc = TryJson(dev.Body))
                        foreach (var d in Arr(doc?.RootElement ?? default, "data"))
                            Add(w.Result, Str(d, "ip"), Str(d, "mac"), Str(d, "name"), true, "UniFi", Str(d, "model"), Str(d, "serial"));
                }
            }
            w.Note(L.F("UniFi: {0} Einträge", Count(w.Result, "UniFi")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (w.Explicit) w.Note("UniFi: " + NetUtil.ErrorText(ex)); }
    }

    /* ------------------------------------------------------------------ OpenWrt (ubus über HTTP) */

    private static async Task OpenWrtAsync(WebApiContext w, CancellationToken ct)
    {
        try
        {
            using var http = w.Client();
            string url = null;
            string session = null;
            int id = 1;

            async Task<JsonElement?> Call(string sid, string obj, string method, object args)
            {
                var payload = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0", ["id"] = id++, ["method"] = "call",
                    ["params"] = new object[] { sid, obj, method, args ?? new Dictionary<string, object>() },
                });
                var r = await SendAsync(http, new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }, ct);
                using var doc = TryJson(r.Body);
                if (doc == null || !doc.RootElement.TryGetProperty("jsonrpc", out _)) return null;
                if (!doc.RootElement.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Array) return default(JsonElement);
                var arr = res.EnumerateArray().ToList();
                // [0, {...}] = OK; [6] = keine Berechtigung
                return arr.Count == 2 && arr[0].GetInt32() == 0 ? arr[1].Clone() : default(JsonElement);
            }

            // Anmelden (Standardbenutzer root); erst http, dann https
            foreach (var u in new[] { w.Http("/ubus"), w.Https("/ubus") }.Distinct())
            {
                url = u;
                JsonElement? login;
                try
                {
                    login = await Call("00000000000000000000000000000000", "session", "login",
                        new Dictionary<string, object> { ["username"] = w.User == "" ? "root" : w.User, ["password"] = w.Password });
                }
                catch (HttpRequestException) { continue; }
                if (login == null) continue; // kein ubus
                session = login.Value.ValueKind == JsonValueKind.Object ? Str(login.Value, "ubus_rpc_session") : "";
                break;
            }
            if (session == null) return;
            if (session == "")
            {
                w.Note(w.HasCredentials ? L.T("OpenWrt: Anmeldung abgelehnt") : L.T("OpenWrt erkannt – Benutzer (root) und Kennwort eintragen"));
                return;
            }

                        var hints = await Call(session, "luci-rpc", "getHostHints", null);
            if (hints is { ValueKind: JsonValueKind.Object } h)
                foreach (var p in h.EnumerateObject())
                {
                    var name = Str(p.Value, "name");
                    foreach (var ip in Arr(p.Value, "ipaddrs")) Add(w.Result, ip.GetString(), p.Name, name, true, "OpenWrt");
                }
            var leases = await Call(session, "luci-rpc", "getDHCPLeases", new Dictionary<string, object> { ["family"] = 4 });
            if (leases is { ValueKind: JsonValueKind.Object } l)
                foreach (var e in Arr(l, "dhcp_leases"))
                    Add(w.Result, Str(e, "ipaddr"), Str(e, "macaddr"), Str(e, "hostname"), true, "OpenWrt");
            if (Count(w.Result, "OpenWrt") == 0)
            {
                // Ohne LuCI: ARP-Tabelle direkt lesen (braucht Lesezugriff auf /proc/net/arp)
                var arp = await Call(session, "file", "read", new Dictionary<string, object> { ["path"] = "/proc/net/arp" });
                if (arp is { ValueKind: JsonValueKind.Object } a)
                    foreach (var line in Str(a, "data").Split('\n').Skip(1))
                    {
                        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (f.Length >= 4) Add(w.Result, f[0], f[3], "", true, "OpenWrt");
                    }
            }
            w.Note(L.F("OpenWrt (ubus): {0} Einträge", Count(w.Result, "OpenWrt")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (w.Explicit) w.Note("OpenWrt: " + NetUtil.ErrorText(ex)); }
    }

    /* ------------------------------------------------------------------ MikroTik RouterOS 7 (REST) */

    private static async Task MikroTikAsync(WebApiContext w, CancellationToken ct)
    {
        try
        {
            using var http = w.Client();
            var user = w.User == "" ? "admin" : w.User;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{w.Password}")));

            string baseUrl = null;
            foreach (var b in new[] { w.Https(""), w.Http("") }.Distinct())
            {
                try
                {
                    var r = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, b + "/rest/system/identity"), ct);
                    // RouterOS antwortet auch ohne gültige Anmeldung mit JSON {"error":401,"message":"Unauthorized"}
                    if (r.Status == HttpStatusCode.Unauthorized && r.Body.Contains("\"error\"") ||
                        r.Status == HttpStatusCode.OK && r.Body.Contains("\"name\""))
                    {
                        baseUrl = b;
                        if (r.Status == HttpStatusCode.Unauthorized)
                        {
                            w.Note(w.HasCredentials ? L.T("MikroTik: Anmeldung abgelehnt") : L.T("MikroTik erkannt – Benutzer und Kennwort eintragen (REST-API, RouterOS 7)"));
                            return;
                        }
                        break;
                    }
                }
                catch (HttpRequestException) { }
            }
            if (baseUrl == null) return;

                        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var leases = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, baseUrl + "/rest/ip/dhcp-server/lease"), ct);
            using (var doc = TryJson(leases.Body))
                foreach (var e in Arr(doc?.RootElement ?? default))
                {
                    var mac = Str(e, "mac-address");
                    if (mac != "") names[mac] = Str(e, "host-name") is { Length: > 0 } hn ? hn : Str(e, "comment");
                    Add(w.Result, Str(e, "address"), mac, names.GetValueOrDefault(mac, ""), Str(e, "status") == "bound", "MikroTik");
                }
            var arp = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, baseUrl + "/rest/ip/arp"), ct);
            using (var doc = TryJson(arp.Body))
                foreach (var e in Arr(doc?.RootElement ?? default))
                {
                    var mac = Str(e, "mac-address");
                    Add(w.Result, Str(e, "address"), mac, names.GetValueOrDefault(mac, ""), Str(e, "complete") != "false", "MikroTik");
                }
            w.Note(L.F("MikroTik (REST): {0} Einträge", Count(w.Result, "MikroTik")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (w.Explicit) w.Note("MikroTik: " + NetUtil.ErrorText(ex)); }
    }

    /* ------------------------------------------------------------------ OPNsense (API-Schlüssel + Secret) */

    private static async Task OpnSenseAsync(WebApiContext w, CancellationToken ct)
    {
        try
        {
            using var http = w.Client();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{w.User}:{w.Password}")));
            var probe = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/api/core/firmware/status")), ct);
            bool isOpn = probe.Body.Contains("Authentication Failed", StringComparison.OrdinalIgnoreCase) && probe.Body.TrimStart().StartsWith("{")
                         || probe.Status == HttpStatusCode.OK && probe.Body.Contains("product_name");
            if (!isOpn) return;
            if (probe.Status != HttpStatusCode.OK)
            {
                w.Note(w.HasCredentials ? L.T("OPNsense: API-Schlüssel abgelehnt") : L.T("OPNsense erkannt – API-Schlüssel (Benutzer) und Secret (Kennwort) eintragen"));
                return;
            }

                        var arp = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/api/diagnostics/interface/get_arp")), ct);
            if (arp.Status != HttpStatusCode.OK)
                arp = await SendAsync(http, new HttpRequestMessage(HttpMethod.Get, w.Https("/api/diagnostics/interface/getArp")), ct);
            using (var doc = TryJson(arp.Body))
            {
                var root = doc?.RootElement ?? default;
                var rows = root.ValueKind == JsonValueKind.Array ? Arr(root) : Arr(root, "rows");
                foreach (var e in rows) Add(w.Result, Str(e, "ip"), Str(e, "mac"), Str(e, "hostname"), true, "OPNsense");
            }
            w.Note(L.F("OPNsense: {0} Einträge", Count(w.Result, "OPNsense")));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { if (w.Explicit) w.Note("OPNsense: " + NetUtil.ErrorText(ex)); }
    }
}
