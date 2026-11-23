using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetStuff.Infrastructure;

/// <summary>
/// Mehrsprachigkeit. Quelltexte sind Deutsch; für andere Sprachen gibt es eine Übersetzungstabelle
/// (Assets/lang/xx.json, Deutsch → Zielsprache). Platzhalter {0}, {1} … erlauben variable Texte.
/// Die automatische Übersetzung der Oberfläche übernimmt die jeweilige App (z. B. LUi in der WPF-App).
/// </summary>
public static class L
{
    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Pattern, string Target)> _templates = new();

    public static string Language { get; private set; } = "de";
    public static bool IsGerman => Language == "de";

    /// <summary>Lädt die Übersetzungstabelle. Rückgabe: true, wenn eine Fremdsprache aktiv ist.</summary>
    public static bool Init(string language)
    {
        Language = language;
        _exact = new();
        _templates = new();
        if (IsGerman) return false;
        try
        {
            using var s = typeof(L).Assembly.GetManifestResourceStream($"NetStuff.Assets.lang.{language}.json");
            if (s == null) { Language = "de"; return false; }
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
            foreach (var (k, v) in dict)
            {
                if (string.IsNullOrEmpty(v)) continue;
                if (Regex.IsMatch(k, @"\{\d+\}"))
                {
                    var pattern = "^" + Regex.Replace(Regex.Escape(k), @"\\\{(\d+)\}", m => $"(?<p{m.Groups[1].Value}>.+?)") + "$";
                    _templates.Add((new Regex(pattern, RegexOptions.Singleline | RegexOptions.Compiled), v));
                }
                else _exact[k] = v;
            }
            // längere Vorlagen zuerst (spezifischer)
            _templates = _templates.OrderByDescending(t => t.Pattern.ToString().Length).ToList();
            return true;
        }
        catch { Language = "de"; return false; }
    }

    /// <summary>Übersetzt einen Text (exakt oder über Vorlagen).</summary>
    public static string T(string text)
    {
        if (IsGerman || string.IsNullOrEmpty(text)) return text;
        if (_exact.TryGetValue(text, out var t)) return t;
        var trimmed = text.Trim();
        if (trimmed != text && _exact.TryGetValue(trimmed, out t)) return text.Replace(trimmed, t);
        foreach (var (pattern, target) in _templates)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            return Regex.Replace(target, @"\{(\d+)\}", x =>
            {
                var g = m.Groups["p" + x.Groups[1].Value];
                return g.Success ? T(g.Value) : x.Value;
            });
        }
        return text;
    }

    /// <summary>Format mit Übersetzung der Vorlage.</summary>
    public static string F(string format, params object[] args) => string.Format(T(format), args);
}
