namespace NetStuff.Ui.Infrastructure;

/// <summary>Startoptionen für automatisierte Läufe (wie die WPF-App): --page X --param Y --run --snapshot datei.png --snapdelay N --lang en --datadir ordner --report</summary>
public static class UiOptions
{
    public static string Page { get; private set; }
    public static string Param { get; private set; }
    public static bool Run { get; private set; }
    public static string Snapshot { get; private set; }
    public static int SnapDelay { get; private set; } = 3;
    public static string Lang { get; private set; }

    /// <summary>Eigener Datenordner (Tests, ohne die echten Projekte/Einstellungen zu berühren).</summary>
    public static string DataDir { get; private set; }

    /// <summary>Vor dem Screenshot das Ergebnis per „Zum Bericht“ ins aktive Projekt übernehmen.</summary>
    public static bool Report { get; private set; }

    /// <summary>Fenstergröße für Tests, z. B. „390x844“ (iPhone-Ansicht am Desktop).</summary>
    public static (int W, int H)? Size { get; private set; }

    public static void Parse(string[] args)
    {
        string Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        Page = Arg("--page");
        Param = Arg("--param");
        Run = args.Contains("--run");
        Snapshot = Arg("--snapshot");
        if (int.TryParse(Arg("--snapdelay"), out var d)) SnapDelay = d;
        Lang = Arg("--lang");
        DataDir = Arg("--datadir");
        Report = args.Contains("--report");
        var size = Arg("--size")?.Split('x');
        if (size?.Length == 2 && int.TryParse(size[0], out var w) && int.TryParse(size[1], out var h)) Size = (w, h);
    }
}
