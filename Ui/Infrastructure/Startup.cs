namespace NetStuff.Ui.Infrastructure;

/// <summary>Gemeinsamer Start für alle Plattformen: Datenordner, Optionen, Sprache.</summary>
public static class Startup
{
    public static void Init(string[] args)
    {
        UiOptions.Parse(args ?? Array.Empty<string>());
        if (UiOptions.DataDir != null) Platform.DataDir = Path.GetFullPath(UiOptions.DataDir);
        Directory.CreateDirectory(Platform.DataDir);
        L.Init(UiOptions.Lang ?? AppSettings.Current.EffectiveLanguage);
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();
    }
}
