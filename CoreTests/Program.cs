using System.Runtime.InteropServices;
using NetStuff.Diagnostics;
using NetStuff.Infrastructure;

// Aufruf: dotnet run --project CoreTests [-- --live] [--lang en]
var lang = Array.IndexOf(args, "--lang") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "de";
L.Init(lang);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Net-Stuff Core-Selbsttest · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}");
Console.WriteLine();

// Echter Scan ohne ARP-Tabelle (wie auf iOS): dotnet run --project CoreTests -- --scan 192.168.1.0/24 [--community public]
if (Array.IndexOf(args, "--scan") is var si and >= 0 && si + 1 < args.Length)
{
    string Arg(string n) => Array.IndexOf(args, n) is var k and >= 0 && k + 1 < args.Length ? args[k + 1] : "";
    var s = new NetStuff.Services.NetScanSettings { SnmpCommunity = Arg("--community") };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var hosts = await NetStuff.Services.NetworkScanner.ScanAsync(NetStuff.Services.NetUtil.ParseRange(args[si + 1], 4096), s, null, null, null, default);
    foreach (var h in hosts)
        Console.WriteLine($"{h.Ip,-15} {h.Mac,-17} {(h.Mac == "" ? "" : h.MacSource == "" ? "ARP" : h.MacSource),-18} {h.Hostname,-34} {h.Model,-26} {h.Serial,-20} {h.Info}");
    Console.WriteLine($"{hosts.Count} Geräte, {hosts.Count(h => h.Mac != "")} mit MAC, {sw.Elapsed.TotalSeconds:0.0} s · Router: {s.RouterSummary}");
    return 0;
}

var result = await CoreSelfTest.RunAsync(args.Contains("--live"), Console.WriteLine);
var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
int pass = lines.Count(l => l.StartsWith("PASS")), fail = lines.Count(l => l.StartsWith("FAIL"));
Console.WriteLine();
Console.WriteLine($"{pass} bestanden, {fail} fehlgeschlagen");
return fail == 0 ? 0 : 1;
