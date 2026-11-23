using System.Runtime.InteropServices;
using NetStuff.Diagnostics;
using NetStuff.Infrastructure;

// Aufruf: dotnet run --project CoreTests [-- --live] [--lang en]
var lang = Array.IndexOf(args, "--lang") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : "de";
L.Init(lang);
Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Net-Stuff Core-Selbsttest · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · .NET {Environment.Version}");
Console.WriteLine();

var result = await CoreSelfTest.RunAsync(args.Contains("--live"), Console.WriteLine);
var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
int pass = lines.Count(l => l.StartsWith("PASS")), fail = lines.Count(l => l.StartsWith("FAIL"));
Console.WriteLine();
Console.WriteLine($"{pass} bestanden, {fail} fehlgeschlagen");
return fail == 0 ? 0 : 1;
