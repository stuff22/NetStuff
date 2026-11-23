using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NetStuff.Services;

/// <summary>NetBIOS-Namensabfrage (Node Status, UDP 137).</summary>
public static class NetBiosClient
{
    public static async Task<(string Name, string Group)> QueryAsync(IPAddress ip, int timeoutMs = 1000)
    {
        try
        {
            using var udp = new UdpClient();
            var req = new byte[50];
            var rnd = (ushort)Random.Shared.Next(1, 65535);
            req[0] = (byte)(rnd >> 8); req[1] = (byte)rnd;
            req[5] = 1; // QDCOUNT
            req[12] = 0x20;
            // "*" + 15x 0x00 in First-Level-Encoding
            req[13] = (byte)'C'; req[14] = (byte)'K';
            for (int i = 15; i < 45; i++) req[i] = (byte)'A';
            req[45] = 0;
            req[46] = 0x00; req[47] = 0x21; // NBSTAT
            req[48] = 0x00; req[49] = 0x01; // IN
            await udp.SendAsync(req, req.Length, new IPEndPoint(ip, 137));

            var recv = udp.ReceiveAsync();
            if (await Task.WhenAny(recv, Task.Delay(timeoutMs)) != recv) return ("", "");
            var data = recv.Result.Buffer;
            if (data.Length < 57) return ("", "");
            int count = data[56];
            string name = "", group = "";
            for (int i = 0; i < count; i++)
            {
                int off = 57 + i * 18;
                if (off + 18 > data.Length) break;
                var n = Encoding.ASCII.GetString(data, off, 15).Trim();
                byte suffix = data[off + 15];
                bool isGroup = (data[off + 16] & 0x80) != 0;
                if (suffix == 0x00 && !isGroup && name == "") name = n;
                else if (suffix == 0x00 && isGroup && group == "") group = n;
            }
            return (name, group);
        }
        catch { return ("", ""); }
    }
}

/// <summary>Ergebnis einer SSDP/UPnP-Erkennung.</summary>
public class SsdpDevice
{
    public string Server { get; set; } = "";
    public string Location { get; set; } = "";
    public string FriendlyName { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string DeviceType { get; set; } = "";

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (FriendlyName != "") parts.Add(FriendlyName);
            var mm = $"{Manufacturer} {Model}".Trim();
            if (mm != "" && !FriendlyName.Contains(Model)) parts.Add(mm);
            if (parts.Count == 0 && Server != "") parts.Add(Server);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>SSDP M-SEARCH (UPnP) – findet Router, TVs, Drucker, Mediaplayer usw.</summary>
public static class SsdpDiscovery
{
    public static async Task<Dictionary<string, SsdpDevice>> DiscoverAsync(int durationMs = 3000, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SsdpDevice>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var msg = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n");
            var ep = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
            await udp.SendAsync(msg, msg.Length, ep);
            await Task.Delay(100, ct);
            await udp.SendAsync(msg, msg.Length, ep);

            var until = DateTime.UtcNow.AddMilliseconds(durationMs);
            while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                var recv = udp.ReceiveAsync();
                var remaining = until - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || await Task.WhenAny(recv, Task.Delay(remaining, ct)) != recv) break;
                var r = recv.Result;
                var ip = r.RemoteEndPoint.Address.ToString();
                var text = Encoding.UTF8.GetString(r.Buffer);
                if (!result.TryGetValue(ip, out var dev)) result[ip] = dev = new SsdpDevice();
                var server = Header(text, "SERVER");
                if (server != "" && dev.Server == "") dev.Server = server;
                var loc = Header(text, "LOCATION");
                if (loc != "" && dev.Location == "") dev.Location = loc;
            }
        }
        catch { }

        // Gerätebeschreibungen (XML) laden
        var tasks = result.Values.Where(d => d.Location.StartsWith("http")).Select(async d =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(2500);
                var xml = await NetUtil.Http.GetStringAsync(d.Location, cts.Token);
                var doc = XDocument.Parse(xml);
                var device = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "device");
                if (device == null) return;
                string V(string n) => device.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value?.Trim() ?? "";
                d.FriendlyName = V("friendlyName");
                d.Manufacturer = V("manufacturer");
                d.Model = $"{V("modelName")} {V("modelNumber")}".Trim();
                d.DeviceType = V("deviceType");
            }
            catch { }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private static string Header(string text, string name)
    {
        var m = Regex.Match(text, "^" + name + @":\s*(.*)$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }
}
