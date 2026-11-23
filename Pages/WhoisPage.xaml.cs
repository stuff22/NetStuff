using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class WhoisPage : ToolPage
{
    public WhoisPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) { QueryBox.Text = s; Whois_Click(null, null); }
    }

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Whois_Click(sender, e);
    }

    private void Busy(bool b)
    {
        WhoisButton.IsEnabled = !b;
        RdapButton.IsEnabled = !b;
        if (b) { RawBox.Text = "Abfrage läuft …"; SummaryList.ItemsSource = null; }
    }

    private async void Whois_Click(object sender, RoutedEventArgs e)
    {
        var q = QueryBox.Text.Trim();
        if (q == "") return;
        Busy(true);
        try
        {
            if (Regex.IsMatch(q, @"^AS\d+$", RegexOptions.IgnoreCase) && string.IsNullOrWhiteSpace(ServerBox.Text))
                q = q.ToUpperInvariant();
            var hops = await WhoisClient.QueryAsync(q, ServerBox.Text, FollowBox.IsChecked == true);
            var sb = new StringBuilder();
            foreach (var h in hops)
            {
                sb.AppendLine($"──────── {h.Server} ────────");
                sb.AppendLine(h.Response.Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim());
                sb.AppendLine();
            }
            RawBox.Text = sb.ToString();
            SummaryList.ItemsSource = WhoisClient.Summarize(hops.AsEnumerable().Reverse())
                .Select(kv => new CheckItem(kv.Key, kv.Value, kv.Key == "Läuft ab" ? ExpiryLevel(kv.Value) : Level.Info)).ToList();
        }
        catch (Exception ex)
        {
            RawBox.Text = "Fehler: " + ex.Message;
        }
        finally { Busy(false); }
    }

    private static Level ExpiryLevel(string v)
    {
        var m = Regex.Match(v, @"\d{4}-\d{2}-\d{2}");
        if (m.Success && DateTime.TryParse(m.Value, out var d))
            return (d - DateTime.Now).TotalDays < 30 ? Level.Warn : Level.Ok;
        return Level.Info;
    }

    private async void Rdap_Click(object sender, RoutedEventArgs e)
    {
        var q = QueryBox.Text.Trim();
        if (q == "") return;
        Busy(true);
        try
        {
            string path;
            if (Regex.IsMatch(q, @"^AS\d+$", RegexOptions.IgnoreCase)) path = "autnum/" + q[2..];
            else if (IPAddress.TryParse(q, out _) || q.Contains('/')) path = "ip/" + q;
            else path = "domain/" + NetUtil.CleanHost(q);

            using var req = new HttpRequestMessage(HttpMethod.Get, "https://rdap.org/" + path);
            req.Headers.Accept.ParseAdd("application/rdap+json, application/json");
            using var res = await NetUtil.Http.SendAsync(req);
            var json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                RawBox.Text = $"RDAP: HTTP {(int)res.StatusCode} – für diese TLD ist evtl. kein RDAP verfügbar. Whois verwenden.\n\n{json}";
                return;
            }
            using var doc = JsonDocument.Parse(json);
            RawBox.Text = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            SummaryList.ItemsSource = SummarizeRdap(doc.RootElement);
        }
        catch (Exception ex)
        {
            RawBox.Text = "Fehler: " + ex.Message;
        }
        finally { Busy(false); }
    }

    private static List<CheckItem> SummarizeRdap(JsonElement root)
    {
        var items = new List<CheckItem>();
        string S(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var name = S(root, "ldhName") ?? S(root, "name") ?? S(root, "handle");
        if (name != null) items.Add(new CheckItem("Objekt", name));
        if (S(root, "startAddress") is { } sa) items.Add(new CheckItem("Bereich", $"{sa} – {S(root, "endAddress")}"));
        if (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Array)
            items.Add(new CheckItem("Status", string.Join(", ", st.EnumerateArray().Select(x => x.GetString()))));
        if (root.TryGetProperty("events", out var ev))
        {
            foreach (var e in ev.EnumerateArray())
            {
                var action = S(e, "eventAction");
                var date = S(e, "eventDate");
                if (action == null || date == null) continue;
                var label = action switch
                {
                    "registration" => "Erstellt",
                    "expiration" => "Läuft ab",
                    "last changed" => "Geändert",
                    "last update of RDAP database" => null,
                    _ => action,
                };
                if (label == null) continue;
                var lvl = Level.Info;
                if (action == "expiration" && DateTime.TryParse(date, out var d)) lvl = (d - DateTime.Now).TotalDays < 30 ? Level.Warn : Level.Ok;
                items.Add(new CheckItem(label, date.Length >= 10 ? date[..10] : date, lvl));
            }
        }
        if (root.TryGetProperty("entities", out var ents))
        {
            foreach (var en in ents.EnumerateArray())
            {
                var roles = en.TryGetProperty("roles", out var r) ? string.Join(", ", r.EnumerateArray().Select(x => x.GetString())) : "";
                var fn = VcardFn(en);
                if (!string.IsNullOrEmpty(fn))
                    items.Add(new CheckItem(roles switch { "registrar" => "Registrar", "registrant" => "Inhaber", "abuse" => "Abuse", _ => roles }, fn));
            }
        }
        if (root.TryGetProperty("nameservers", out var ns))
            items.Add(new CheckItem("Nameserver", string.Join(", ", ns.EnumerateArray().Select(n => S(n, "ldhName")).Where(x => x != null))));
        if (root.TryGetProperty("secureDNS", out var sd) && sd.TryGetProperty("delegationSigned", out var ds))
            items.Add(new CheckItem("DNSSEC", ds.GetBoolean() ? "signiert" : "nicht signiert", ds.GetBoolean() ? Level.Ok : Level.Info));
        if (S(root, "country") is { } c) items.Add(new CheckItem("Land", c));
        return items;
    }

    private static string VcardFn(JsonElement entity)
    {
        try
        {
            if (!entity.TryGetProperty("vcardArray", out var va) || va.GetArrayLength() < 2) return null;
            foreach (var prop in va[1].EnumerateArray())
            {
                if (prop[0].GetString() == "fn") return prop[3].GetString();
            }
        }
        catch { }
        return null;
    }

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyText(RawBox.Text);
}
