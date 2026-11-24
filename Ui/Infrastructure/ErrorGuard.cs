using Avalonia.Threading;

namespace NetStuff.Ui.Infrastructure;

/// <summary>
/// Fängt unbehandelte Fehler aus der Oberfläche ab (z. B. aus async-void-Klickhandlern), damit ein Fehler in einem
/// Werkzeug nicht die ganze App beendet – wichtig unter iOS, wo jede unbehandelte Ausnahme sofort zum Absturz führt.
/// Der Fehler wird in der Werkzeugleiste angezeigt und vollständig in die Konsole und nach DataDir/crash.log geschrieben.
/// </summary>
public static class ErrorGuard
{
    /// <summary>Anzeige in der Oberfläche (setzt die Hauptansicht).</summary>
    public static event Action<Exception> Reported;

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Report(e.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception, fatal: true);
    }

    public static void Report(Exception ex)
    {
        Log(ex, fatal: false);
        try { Reported?.Invoke(ex); } catch { }
    }

    private static void Log(Exception ex, bool fatal)
    {
        if (ex == null) return;
        var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(fatal ? "ABSTURZ" : "Fehler")}: {ex}\n";
        try { Console.Error.WriteLine("Net-Stuff " + text); } catch { }
        try { File.AppendAllText(Path.Combine(Platform.DataDir, "crash.log"), text + "\n"); } catch { }
    }
}
