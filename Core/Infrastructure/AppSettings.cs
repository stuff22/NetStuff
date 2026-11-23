using System.IO;
using System.Text.Json;

namespace NetStuff.Infrastructure;

/// <summary>Benutzereinstellungen (JSON im Datenordner, siehe Platform.DataDir).</summary>
public class AppSettings
{
    private static readonly string FilePath = Path.Combine(Platform.DataDir, "settings.json");
    private static AppSettings _current;

    public string Language { get; set; } = "system";   // system | de | en
    public string Theme { get; set; } = "system";      // system | light | dark
    public string CompanyName { get; set; } = "";
    public string Technician { get; set; } = "";
    public string CompanyContact { get; set; } = "";
    public string LogoFile { get; set; } = "";
    public bool Notifications { get; set; } = true;
    public string ActiveProjectId { get; set; } = "";
    public string IperfServer { get; set; } = "";

    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    /// <summary>Effektive Sprache ("de" oder "en").</summary>
    public string EffectiveLanguage =>
        Language is "de" or "en" ? Language
        : System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
}

/// <summary>Einfacher JSON-Speicher für Listen.</summary>
public static class JsonStore
{
    public static List<T> LoadList<T>(string file)
    {
        try
        {
            var path = Path.Combine(Platform.DataDir, file);
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void SaveList<T>(string file, IEnumerable<T> items)
    {
        try { File.WriteAllText(Path.Combine(Platform.DataDir, file), JsonSerializer.Serialize(items.ToList(), new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
}
