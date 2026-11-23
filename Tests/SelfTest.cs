using System.IO;
using System.Text;
using NetStuff.Diagnostics;

namespace NetStuff.Tests;

/// <summary>
/// Entwickler-Selbsttest (NetStuff.exe --selftest datei.txt [--copytest]): WPF-spezifische Tests + Selbsttest des Kerns.
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(string outFile)
    {
        var sb = new StringBuilder();
        void Log(string s) => sb.AppendLine(s);
        async Task T(string name, Func<Task<string>> f)
        {
            try { Log($"PASS {name}: {await f()}"); }
            catch (Exception ex) { Log($"FAIL {name}: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Link-Erkennung
        await T("Link-Erkennung", () =>
        {
            var cases = new (string Text, string Expect)[]
            {
                ("https://www.orf.at/news.", "Url:https://www.orf.at/news"), ("10.0.0.1", "Ip:10.0.0.1"), ("10.0.0.0/24", "Ip:10.0.0.0/24"),
                ("fe80::9358:de9d:497:3bb0%20", "Ip:fe80::9358:de9d:497:3bb0%20"), ("ns3.apa.at", "Host:ns3.apa.at"),
                ("max@example.com", "Mail:max@example.com"), ("C8:5E:A9:8C:E0:38", "None"), ("22:43:05", "None"),
                ("10.0.26100.10068", "None"), ("settings.json", "None"), ("1.022", "None"), ("z. B.", "None"),
            };
            var rx = typeof(Infrastructure.Linkify).GetField("Pattern", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) as System.Text.RegularExpressions.Regex;
            var fails = new List<string>();
            foreach (var (text, expect) in cases)
            {
                var m = rx!.Match(text);
                string got = "None";
                if (m.Success && m.Index == 0 && m.Length >= text.TrimEnd('.').Length)
                {
                    var k = Infrastructure.Linkify.Classify(m, out var target);
                    got = k == Infrastructure.Linkify.Kind.None ? "None" : $"{k}:{target}";
                }
                if (got != expect) fails.Add($"'{text}' → {got} (erwartet {expect})");
            }
            if (fails.Count > 0) throw new Exception(string.Join("; ", fails));
            return Task.FromResult($"{cases.Length} Fälle korrekt");
        });

        sb.Append(await CoreSelfTest.RunAsync(Environment.GetCommandLineArgs().Contains("--copytest")));
        File.WriteAllText(outFile, sb.ToString());
    }
}
