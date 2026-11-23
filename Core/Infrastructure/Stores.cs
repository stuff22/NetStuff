using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetStuff.Infrastructure;

public class HistoryEntry
{
    public string Tool { get; set; }
    public string ToolTitle { get; set; }
    public string Target { get; set; }
    public DateTime Time { get; set; }
    [JsonIgnore] public string TimeText => Time.ToString("dd.MM.yyyy HH:mm");
}

public class Favorite
{
    public string Name { get; set; }
    public string Tool { get; set; }
    public string ToolTitle { get; set; }
    public string Target { get; set; }
}

/// <summary>Verlauf (letzte 300 Ausführungen) und Favoriten.</summary>
public static class HistoryStore
{
    public static ObservableCollection<HistoryEntry> History { get; } = new(JsonStore.LoadList<HistoryEntry>("history.json"));
    public static ObservableCollection<Favorite> Favorites { get; } = new(JsonStore.LoadList<Favorite>("favorites.json"));

    public static void Add(string tool, string toolTitle, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) target = "";
        var last = History.FirstOrDefault();
        if (last != null && last.Tool == tool && last.Target == target && (DateTime.Now - last.Time).TotalSeconds < 30) return;
        History.Insert(0, new HistoryEntry { Tool = tool, ToolTitle = toolTitle, Target = target.Trim(), Time = DateTime.Now });
        while (History.Count > 300) History.RemoveAt(History.Count - 1);
        JsonStore.SaveList("history.json", History);
    }

    public static IEnumerable<string> RecentTargets(string tool) =>
        History.Where(h => h.Tool == tool && h.Target != "").Select(h => h.Target).Distinct().Take(15);

    public static void ClearHistory()
    {
        History.Clear();
        JsonStore.SaveList("history.json", History);
    }

    public static void AddFavorite(Favorite f)
    {
        var existing = Favorites.FirstOrDefault(x => x.Tool == f.Tool && x.Target == f.Target);
        if (existing != null) Favorites.Remove(existing);
        Favorites.Insert(0, f);
        SaveFavorites();
    }

    public static void SaveFavorites() => JsonStore.SaveList("favorites.json", Favorites);
}

/* ------------------------------------------------------------------ Projekte & Berichte */

public class ReportItem
{
    public string Name { get; set; }
    public string Value { get; set; }
    public Level Level { get; set; }
}

public class ReportBlock
{
    public string Kind { get; set; }          // section | table | image | text | headline
    public string Title { get; set; }
    public List<ReportItem> Items { get; set; }
    public List<string> Headers { get; set; }
    public List<List<string>> Rows { get; set; }
    public string ImageBase64 { get; set; }
    public string Text { get; set; }
}

public class ReportEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tool { get; set; }
    public string Title { get; set; }
    public string Target { get; set; }
    public string Comment { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public List<ReportBlock> Blocks { get; set; } = new();
    [JsonIgnore] public string Summary => $"{Time:dd.MM.yyyy HH:mm} · {Title}" + (string.IsNullOrEmpty(Target) ? "" : $" – {Target}");
}

public class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Customer { get; set; } = "";
    public string Site { get; set; } = "";
    public string Address { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<ReportEntry> Entries { get; set; } = new();

    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Site) ? Customer : $"{Customer} – {Site}";
    public override string ToString() => Display;
}

/// <summary>Kundenprojekte (je Projekt eine JSON-Datei).</summary>
public static class ProjectStore
{
    private static readonly string Dir = Path.Combine(Platform.DataDir, "projects");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ObservableCollection<Project> Projects { get; } = new(LoadAll());

    public static event EventHandler ActiveChanged;

    public static Project Active
    {
        get => Projects.FirstOrDefault(p => p.Id == AppSettings.Current.ActiveProjectId);
        set
        {
            AppSettings.Current.ActiveProjectId = value?.Id ?? "";
            AppSettings.Current.Save();
            ActiveChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static IEnumerable<Project> LoadAll()
    {
        Directory.CreateDirectory(Dir);
        var list = new List<Project>();
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
        {
            try { list.Add(JsonSerializer.Deserialize<Project>(File.ReadAllText(f))); } catch { }
        }
        return list.Where(p => p != null).OrderByDescending(p => p.Created);
    }

    public static void Save(Project p)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, p.Id + ".json"), JsonSerializer.Serialize(p, Options));
    }

    public static Project Create(string customer)
    {
        var p = new Project { Customer = customer };
        Projects.Insert(0, p);
        Save(p);
        return p;
    }

    public static void Delete(Project p)
    {
        Projects.Remove(p);
        try { File.Delete(Path.Combine(Dir, p.Id + ".json")); } catch { }
        if (AppSettings.Current.ActiveProjectId == p.Id) Active = null;
    }
}
