namespace NetStuff.Ui.Infrastructure;

/// <summary>Startoptionen für automatisierte Läufe (wie die WPF-App): --page X --param Y --run --snapshot datei.png --snapdelay N --lang en</summary>
public static class UiOptions
{
    public static string Page { get; private set; }
    public static string Param { get; private set; }
    public static bool Run { get; private set; }
    public static string Snapshot { get; private set; }
    public static int SnapDelay { get; private set; } = 3;
    public static string Lang { get; private set; }

    public static void Parse(string[] args)
    {
        string Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        Page = Arg("--page");
        Param = Arg("--param");
        Run = args.Contains("--run");
        Snapshot = Arg("--snapshot");
        if (int.TryParse(Arg("--snapdelay"), out var d)) SnapDelay = d;
        Lang = Arg("--lang");
    }
}
