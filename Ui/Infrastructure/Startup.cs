namespace NetStuff.Ui.Infrastructure;

/// <summary>Gemeinsamer Start für alle Plattformen: Datenordner, Optionen, Sprache.</summary>
public static class Startup
{
    public static void Init(string[] args)
    {
        Directory.CreateDirectory(Platform.DataDir);
        UiOptions.Parse(args ?? Array.Empty<string>());
        L.Init(UiOptions.Lang ?? AppSettings.Current.EffectiveLanguage);
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();
    }
}
