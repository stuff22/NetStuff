using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using Lextm.SharpSnmpLib;
using NetStuff.Infrastructure;

namespace NetStuff.Services;

/// <summary>Ein Gerät, das der Router kennt (aus seiner Hostliste oder ARP-Tabelle).</summary>
public record RouterHost(string Ip, byte[] Mac, string Name, bool Active, string Source)
{
    /// <summary>Modell/Seriennummer, falls der Router sie kennt (z. B. UniFi für eigene Access Points und Switches).</summary>
    public string Model { get; init; } = "";
    public string Serial { get; init; } = "";
}

/// <summary>Welche Router-Schnittstelle abgefragt wird.</summary>
public enum RouterKind { Auto, FritzBox, UniFi, OpenWrt, MikroTik, OpnSense, SnmpOnly }

/// <summary>Ergebnis der Router-Abfrage: gefundene Geräte und ein Satz für die Statuszeile.</summary>
public class RouterHostResult
{
    public Dictionary<string, RouterHost> Hosts { get; } = new();
    public List<string> Notes { get; } = new();
    public string Summary => string.Join(" · ", Notes);
}

/// <summary>
/// MAC-Adressen über den Router statt über die eigene ARP-Tabelle (die z. B. iOS nicht herausgibt):
/// FRITZ!Box per TR-064 (X_AVM-DE_GetHostListPath, sonst GetGenericHostEntry) und beliebige Router/Switches
/// per SNMP (ipNetToMediaTable bzw. ipNetToPhysicalTable); weitere Router-APIs in RouterApis.cs.
/// </summary>
public static partial class RouterHosts
{
    private const string HostsService = "urn:dslforum-org:service:Hosts:1";

    /// <param name="tr064Port">TR-064-Port (FRITZ!Box: 49000; anderer Wert nur für den Selbsttest)</param>
    /// <param name="webBase">Basis-URL der Web-APIs (leer = https://router bzw. http://router; nur für den Selbsttest)</param>
    public static async Task<RouterHostResult> QueryAsync(IPAddress router, string user, string password, string community, CancellationToken ct,
        int tr064Port = 49000, RouterKind kind = RouterKind.Auto, string webBase = null)
    {
        var result = new RouterHostResult();
        bool Use(RouterKind k) => kind == RouterKind.Auto || kind == k;
        var tasks = new List<Task>();
        if (Use(RouterKind.FritzBox)) tasks.Add(Tr064Async(router, tr064Port, user, password, result, ct));
        if (!string.IsNullOrWhiteSpace(community)) tasks.Add(SnmpArpAsync(router, community.Trim(), result, ct));
        var web = new WebApiContext(router, user ?? "", password ?? "", webBase, kind != RouterKind.Auto, result);
        if (Use(RouterKind.UniFi)) tasks.Add(UniFiAsync(web, ct));
        if (Use(RouterKind.OpenWrt)) tasks.Add(OpenWrtAsync(web, ct));
        if (Use(RouterKind.MikroTik)) tasks.Add(MikroTikAsync(web, ct));
        if (Use(RouterKind.OpnSense)) tasks.Add(OpnSenseAsync(web, ct));
        await Task.WhenAll(tasks);
        if (kind is not RouterKind.Auto and not RouterKind.SnmpOnly && result.Notes.Count == 0)
            lock (result) result.Notes.Add(L.F("{0}: keine Antwort von {1}", kind.ToString(), router));
        return result;
    }

    /* ------------------------------------------------------------------ TR-064 (FRITZ!Box) */

    private static async Task Tr064Async(IPAddress router, int port, string user, string password, RouterHostResult result, CancellationToken ct)
    {
        var baseUrl = $"http://{router}:{port}";
        var handler = new HttpClientHandler { UseProxy = false };
        if (!string.IsNullOrEmpty(password))
            handler.Credentials = new NetworkCredential(user ?? "", password); // FRITZ!Box verlangt HTTP-Digest; der Handler antwortet auf die Aufforderung
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };

        // Gerätebeschreibung: Gibt es TR-064 und den Hosts-Dienst?
        string controlUrl;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            var desc = XDocument.Parse(await http.GetStringAsync(baseUrl + "/tr64desc.xml", cts.Token));
            var svc = desc.Descendants().FirstOrDefault(e => e.Name.LocalName == "service" && Child(e, "serviceType") == HostsService);
            if (svc == null) return;
            controlUrl = Child(svc, "controlURL");
            if (string.IsNullOrEmpty(controlUrl)) controlUrl = "/upnp/control/hosts";
        }
        catch { return; } // kein TR-064 (kein AVM-Router oder in der FRITZ!Box abgeschaltet)

        try
        {
            // Variante 1: Eine Datei mit der gesamten Hostliste (FRITZ!OS ab 7.10)
            var path = await SoapAsync(http, baseUrl + controlUrl, "X_AVM-DE_GetHostListPath", null, ct);
            if (path.TryGetValue("NewX_AVM-DE_HostListPath", out var p) && p != "")
            {
                var xml = XDocument.Parse(await http.GetStringAsync(baseUrl + p, ct));
                foreach (var item in xml.Descendants().Where(e => e.Name.LocalName == "Item"))
                    Add(result, Child(item, "IPAddress"), Child(item, "MACAddress"), Child(item, "HostName"), Child(item, "Active") == "1", "FRITZ!Box");
            }
        }
        catch (Tr064Exception ex) when (ex.Unauthorized) { lock (result) result.Notes.Add(AuthNote(password)); return; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        if (Count(result, "FRITZ!Box") == 0)
        {
            // Variante 2: Einträge einzeln abfragen (ältere FRITZ!OS-Versionen)
            try
            {
                var n = await SoapAsync(http, baseUrl + controlUrl, "GetHostNumberOfEntries", null, ct);
                int count = int.TryParse(n.GetValueOrDefault("NewHostNumberOfEntries"), out var c) ? Math.Min(c, 1024) : 0;
                for (int i = 0; i < count; i++)
                {
                    var e = await SoapAsync(http, baseUrl + controlUrl, "GetGenericHostEntry", new() { ["NewIndex"] = i.ToString() }, ct);
                    Add(result, e.GetValueOrDefault("NewIPAddress"), e.GetValueOrDefault("NewMACAddress"), e.GetValueOrDefault("NewHostName"),
                        e.GetValueOrDefault("NewActive") == "1", "FRITZ!Box");
                }
            }
            catch (Tr064Exception ex) when (ex.Unauthorized) { lock (result) result.Notes.Add(AuthNote(password)); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lock (result) result.Notes.Add(L.T("FRITZ!Box-Hostliste nicht lesbar: ") + NetUtil.ErrorText(ex)); return; }
        }
        lock (result) result.Notes.Add(L.F("FRITZ!Box (TR-064): {0} Einträge", Count(result, "FRITZ!Box")));
    }

    private static string AuthNote(string password) => string.IsNullOrEmpty(password)
        ? L.T("FRITZ!Box gefunden – für die Geräteliste Benutzer und Kennwort eintragen")
        : L.T("FRITZ!Box: Anmeldung abgelehnt (Benutzer/Kennwort prüfen, „Zugriff für Anwendungen zulassen“ aktiv?)");

    private class Tr064Exception(string message, bool unauthorized) : Exception(message)
    {
        public bool Unauthorized { get; } = unauthorized;
    }

    private static async Task<Dictionary<string, string>> SoapAsync(HttpClient http, string url, string action, Dictionary<string, string> args, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
          .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
          .Append($"<u:{action} xmlns:u=\"{HostsService}\">");
        foreach (var (k, v) in args ?? new()) sb.Append($"<{k}>{System.Security.SecurityElement.Escape(v)}</{k}>");
        sb.Append($"</u:{action}></s:Body></s:Envelope>");

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml") };
        req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{HostsService}#{action}\"");
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new Tr064Exception("401", true);
        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch { throw new Tr064Exception($"HTTP {(int)resp.StatusCode}", false); }
        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
        // UPnP-Fehler 606 = Aktion nicht erlaubt (Anmeldung fehlt); 401 = unbekannte Aktion (ältere FRITZ!OS-Version)
        if (fault != null) throw new Tr064Exception("UPnP-Fehler " + fault, fault == "606");
        var r = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response");
        return r?.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value.Trim()) ?? new();
    }

    private static string Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim() ?? "";

    /* ------------------------------------------------------------------ SNMP (ARP-Tabelle des Routers) */

    private static async Task SnmpArpAsync(IPAddress router, string community, RouterHostResult result, CancellationToken ct)
    {
        var snmp = new SnmpService(router, 161, community, v1: false);
        try
        {
            // ipNetToMediaPhysAddress: OID-Ende = ifIndex.a.b.c.d
            foreach (var v in await snmp.WalkAsync("1.3.6.1.2.1.4.22.1.2", 4096))
                AddSnmp(result, v, 4);
            ct.ThrowIfCancellationRequested();
            if (Count(result, "SNMP") == 0)
            {
                // ipNetToPhysicalPhysAddress (neuere Geräte): ifIndex.addrType.len.a.b.c.d (addrType 1 = IPv4)
                foreach (var v in await snmp.WalkAsync("1.3.6.1.2.1.4.35.1.4", 4096))
                {
                    var parts = v.Id.ToString().Split('.');
                    if (parts.Length >= 7 && parts[^6] == "1" && parts[^5] == "4") AddSnmp(result, v, 4);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            lock (result) result.Notes.Add(L.T("SNMP am Router: keine Antwort") + (ex is Lextm.SharpSnmpLib.Messaging.TimeoutException ? "" : " (" + NetUtil.ErrorText(ex) + ")"));
            return;
        }
        lock (result) result.Notes.Add(L.F("SNMP-ARP-Tabelle: {0} Einträge", Count(result, "SNMP")));
    }

    internal static void AddSnmp(RouterHostResult result, Variable v, int tail)
    {
        if (v.Data is not OctetString os) return;
        var parts = v.Id.ToString().Split('.');
        if (parts.Length < tail) return;
        var ip = string.Join(".", parts[^tail..]);
        Add(result, ip, os.GetRaw(), "", true, "SNMP");
    }

    /* ------------------------------------------------------------------ gemeinsam */

    private static void Add(RouterHostResult result, string ip, string mac, string name, bool active, string source,
        string model = "", string serial = "") =>
        Add(result, ip, ParseMac(mac), name, active, source, model, serial);

    private static void Add(RouterHostResult result, string ip, byte[] mac, string name, bool active, string source,
        string model = "", string serial = "")
    {
        if (mac == null || mac.Length != 6 || mac.All(b => b == 0)) return;
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return;
        lock (result)
        {
            var key = addr.ToString();
            // Bei mehreren Einträgen pro IP (FRITZ!Box: alte Geräte) gewinnt der aktive
            if (result.Hosts.TryGetValue(key, out var old) && old.Active && !active) return;
            result.Hosts[key] = new RouterHost(key, mac, name ?? "", active, source) { Model = model ?? "", Serial = serial ?? "" };
        }
    }

    public static byte[] ParseMac(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var hex = new string(s.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }
}
