using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NetStuff.Services;

public class GeoInfo
{
    [JsonPropertyName("query")] public string Ip { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; }
    [JsonPropertyName("country")] public string Country { get; set; }
    [JsonPropertyName("countryCode")] public string CountryCode { get; set; }
    [JsonPropertyName("city")] public string City { get; set; }
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("isp")] public string Isp { get; set; }
    [JsonPropertyName("org")] public string Org { get; set; }
    [JsonPropertyName("as")] public string As { get; set; }

    public bool Ok => Status == "success";

    public string Location => Ok ? string.Join(", ", new[] { City, Country }.Where(s => !string.IsNullOrEmpty(s))) : "";
}

/// <summary>Geolokalisierung über ip-api.com (kostenlos für nicht-kommerzielle Nutzung, max. 15 Batch-Anfragen/Minute).</summary>
public static class GeoIpService
{
    private const string Fields = "status,country,countryCode,city,lat,lon,isp,org,as,query";
    private static readonly Dictionary<string, GeoInfo> Cache = new();

    public static async Task<Dictionary<string, GeoInfo>> LookupAsync(IEnumerable<IPAddress> ips, CancellationToken ct = default)
    {
        var result = new Dictionary<string, GeoInfo>();
        var todo = new List<string>();
        foreach (var ip in ips.Where(i => !NetUtil.IsPrivate(i)).Select(i => i.ToString()).Distinct())
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(ip, out var g)) { result[ip] = g; continue; }
            }
            todo.Add(ip);
        }

        foreach (var chunk in todo.Chunk(100))
        {
            var res = await NetUtil.Http.PostAsJsonAsync($"http://ip-api.com/batch?fields={Fields}&lang=de", chunk, ct);
            res.EnsureSuccessStatusCode();
            var list = await res.Content.ReadFromJsonAsync<List<GeoInfo>>(cancellationToken: ct) ?? new();
            foreach (var g in list.Where(g => g?.Ip != null))
            {
                result[g.Ip] = g;
                lock (Cache) Cache[g.Ip] = g;
            }
        }
        return result;
    }

    /// <summary>Infos zur eigenen öffentlichen IP (leere Abfrage = Absender-IP).</summary>
    public static async Task<GeoInfo> SelfAsync(CancellationToken ct = default)
    {
        return await NetUtil.Http.GetFromJsonAsync<GeoInfo>($"http://ip-api.com/json/?fields={Fields}&lang=de", ct);
    }
}
