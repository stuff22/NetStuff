using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using DnsClient;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public class ResolverRow : Bindable
{
    private string _answer = "", _verdict = "", _ttl = "";
    private Level _level = Level.Info;
    private double? _avg, _min, _max;

    public bool Enabled { get; set; } = true;
    public string Name { get; init; }
    public string Address { get; init; }
    public bool IsDoh => Address.StartsWith("https://");
    public bool IsSystem => Address == "system";

    public string Answer { get => _answer; set => Set(ref _answer, value); }
    public string Verdict { get => _verdict; set => Set(ref _verdict, value); }
    public string Ttl { get => _ttl; set => Set(ref _ttl, value); }
    public Level Level { get => _level; set => Set(ref _level, value); }
    public string Normalized { get; set; } = "";

    public void SetTimes(List<double> t)
    {
        _avg = t.Count > 0 ? t.Average() : null;
        _min = t.Count > 0 ? t.Min() : null;
        _max = t.Count > 0 ? t.Max() : null;
        Raise(nameof(AvgText)); Raise(nameof(MinText)); Raise(nameof(MaxText)); Raise(nameof(AvgSort));
    }

    public string AvgText => _avg.HasValue ? $"{_avg:0.#}" : "–";
    public string MinText => _min.HasValue ? $"{_min:0}" : "–";
    public string MaxText => _max.HasValue ? $"{_max:0}" : "–";
    public double AvgSort => _avg ?? double.MaxValue;
}

public partial class ResolverPage : ToolPage
{
    private readonly ObservableCollection<ResolverRow> _rows = new();

    public ResolverPage()
    {
        InitializeComponent();
        var gw = NetUtil.ActiveIPv4().Select(x => x.Gateway).FirstOrDefault(g => g != null);
        _rows.Add(new ResolverRow { Name = "Windows (System)", Address = "system" });
        if (gw != null) _rows.Add(new ResolverRow { Name = "Router / Gateway", Address = gw.ToString() });
        foreach (var (n, a) in new[]
                 {
                     ("Cloudflare", "1.1.1.1"), ("Cloudflare 2", "1.0.0.1"), ("Google", "8.8.8.8"), ("Google 2", "8.8.4.4"),
                     ("Quad9", "9.9.9.9"), ("OpenDNS", "208.67.222.222"), ("AdGuard", "94.140.14.14"), ("DNS4EU", "86.54.11.100"),
                     ("Control D", "76.76.2.0"),
                     ("Cloudflare DoH", "https://cloudflare-dns.com/dns-query"), ("Google DoH", "https://dns.google/resolve"),
                 })
            _rows.Add(new ResolverRow { Name = n, Address = a });
        ResultGrid.ItemsSource = _rows;
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) NameBox.Text = s;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var a = CustomBox.Text.Trim();
        if (a == "") return;
        if (!a.StartsWith("https://") && !IPAddress.TryParse(a, out _)) { InfoText.Text = "Bitte IP-Adresse oder https-URL (DoH, JSON-API) angeben."; return; }
        _rows.Insert(0, new ResolverRow { Name = "Eigener", Address = a });
        CustomBox.Text = "";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var name = NetUtil.CleanHost(NameBox.Text);
        if (name == "") return;
        var typeName = (TypeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "A";
        var type = Enum.Parse<QueryType>(typeName);
        int rounds = (int)(RoundsBox.Value ?? 3);
        StartButton.IsEnabled = false;
        InfoText.Text = "Abfragen laufen …";

        var active = _rows.Where(r => r.Enabled).ToList();
        foreach (var r in _rows) { r.Answer = ""; r.Verdict = ""; r.Ttl = ""; r.Level = Level.Info; r.SetTimes(new()); r.Normalized = ""; }

        await Task.WhenAll(active.Select(r => QueryResolver(r, name, type, rounds)));

        // Mehrheitsantwort ermitteln
        var majority = active.Where(r => r.Normalized != "" && r.Level != Level.Bad)
            .GroupBy(r => r.Normalized).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        foreach (var r in active.Where(r => r.Level != Level.Bad))
        {
            if (r.Normalized == "") { r.Verdict = "leer"; r.Level = Level.Warn; }
            else if (r.Normalized == majority) { r.Verdict = "übereinstimmend"; r.Level = Level.Ok; }
            else { r.Verdict = "abweichend"; r.Level = Level.Warn; }
        }
        int diff = active.Count(r => r.Verdict == "abweichend");
        var fastest = active.Where(r => r.AvgSort < double.MaxValue).OrderBy(r => r.AvgSort).FirstOrDefault();
        InfoText.Text = $"{active.Count} Resolver abgefragt · {diff} abweichende Antworten"
                        + (fastest != null ? $" · schnellster: {fastest.Name} ({fastest.AvgText} ms)" : "")
                        + (diff > 0 ? " · Abweichungen sind bei CDN/GeoDNS (z. B. A-Records großer Seiten) normal." : "");
        StartButton.IsEnabled = true;
    }

    private static async Task QueryResolver(ResolverRow row, string name, QueryType type, int rounds)
    {
        var times = new List<double>();
        try
        {
            List<(string Value, int Ttl)> answers = null;
            for (int i = 0; i < rounds; i++)
            {
                var sw = Stopwatch.StartNew();
                answers = row.IsDoh ? await DohAsync(row.Address, name, type) : await ClassicAsync(row, name, type);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            row.SetTimes(times);
            var values = answers.Select(a => a.Value).ToList();
            row.Answer = values.Count == 0 ? "(keine Einträge)" : string.Join("\n", values);
            row.Ttl = answers.Count > 0 ? answers.Min(a => a.Ttl).ToString() : "";
            row.Normalized = string.Join("|", values.Select(Normalize).OrderBy(v => v));
        }
        catch (Exception ex)
        {
            row.SetTimes(times);
            row.Answer = ex is TaskCanceledException or OperationCanceledException || ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                ? "Zeitüberschreitung – Resolver nicht erreichbar"
                : ex.Message;
            row.Verdict = "Fehler";
            row.Level = Level.Bad;
        }
    }

    private static string Normalize(string v) => v.Replace("\"", "").Trim().TrimEnd('.').ToLowerInvariant();

    private static async Task<List<(string, int)>> ClassicAsync(ResolverRow row, string name, QueryType type)
    {
        var client = row.IsSystem ? DnsHelper.Create(timeoutMs: 3000, dnssec: false) : DnsHelper.Create(IPAddress.Parse(row.Address), timeoutMs: 3000, dnssec: false);
        var r = await client.QueryAsync(name, type);
        if (r.HasError && r.Header.ResponseCode != DnsHeaderResponseCode.NoError)
        {
            if (r.Header.ResponseCode == DnsHeaderResponseCode.NotExistentDomain) return new();
            throw new Exception(r.ErrorMessage);
        }
        return r.Answers.Where(a => a.RecordType.ToString() == type.ToString())
            .Select(a => (DnsHelper.ValueOf(a), a.TimeToLive)).ToList();
    }

    private static async Task<List<(string, int)>> DohAsync(string url, string name, QueryType type)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}?name={Uri.EscapeDataString(name)}&type={type}");
        req.Headers.Accept.ParseAdd("application/dns-json");
        using var cts = new CancellationTokenSource(5000);
        using var res = await NetUtil.Http.SendAsync(req, cts.Token);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
        var list = new List<(string, int)>();
        int typeNum = (int)type;
        if (doc.RootElement.TryGetProperty("Answer", out var ans))
        {
            foreach (var a in ans.EnumerateArray())
            {
                if (a.GetProperty("type").GetInt32() != typeNum) continue;
                list.Add((a.GetProperty("data").GetString(), a.TryGetProperty("TTL", out var ttl) ? ttl.GetInt32() : 0));
            }
        }
        return list;
    }
}
