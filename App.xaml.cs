using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NetStuff;

public partial class App : Application
{
    /// <summary>Datenordner (%LOCALAPPDATA%\Net-Stuff) – festgelegt im Kern (Platform.DataDir).</summary>
    public static string DataDir => Platform.DataDir;

    /// <summary>Automatisierter Lauf (Selbsttest/Screenshot) – keine Dialoge anzeigen.</summary>
    public static bool Headless { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        Platforms.Windows.WindowsPlatform.Register();
        var args = Environment.GetCommandLineArgs();
        var langArg = Array.IndexOf(args, "--lang") is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        Infrastructure.LUi.Init(langArg ?? Infrastructure.AppSettings.Current.EffectiveLanguage);
        Headless = args.Contains("--selftest") || args.Contains("--snapshot");
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();
        base.OnStartup(e);

        var st = Array.IndexOf(args, "--selftest");
        if (st >= 0 && st + 1 < args.Length)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                await Tests.SelfTest.RunAsync(args[st + 1]);
                Shutdown();
            });
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            try { File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:s} FATAL {a.ExceptionObject}\n\n"); } catch { }
        };

        MainWindow = new MainWindow();
        // Nach dem Schließen sofort beenden: verhindert Abstürze beim Aufräumen der WPF-Fensterhandles
        // (alle Daten werden bereits beim Ändern gespeichert).
        MainWindow.Closed += (_, _) => Environment.Exit(0);
        MainWindow.Show();
    }

    private static bool _dialogOpen;
    private static string _lastError;
    private static DateTime _lastErrorTime;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        var text = e.Exception.ToString();
        var head = e.Exception.GetType().Name + e.Exception.StackTrace?.Split('\n').FirstOrDefault();

        // Gleiche Fehler in schneller Folge nur einmal protokollieren
        bool repeat = head == _lastError && (DateTime.Now - _lastErrorTime).TotalSeconds < 30;
        _lastError = head;
        _lastErrorTime = DateTime.Now;
        if (!repeat)
        {
            try { File.AppendAllText(Path.Combine(DataDir, "error.log"), $"{DateTime.Now:s} {text}\n\n"); } catch { }
        }

        // Interne WPF-Fehler (Tooltip-/Fensterverwaltung) nicht als Dialog melden – die App läuft weiter.
        if (Headless || repeat || _dialogOpen || text.Contains("PopupControlService") || text.Contains("HwndMouseInputProvider")) return;
        _dialogOpen = true;
        try { MessageBox.Show(e.Exception.Message, "Net-Stuff – Fehler", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _dialogOpen = false; }
    }
}
