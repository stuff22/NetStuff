using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace NetStuff.Infrastructure;

/// <summary>
/// WPF-Teil der Mehrsprachigkeit: übersetzt alle TextBlocks der App beim Laden und bei Textänderungen
/// automatisch über die Tabelle von <see cref="L"/> (NetStuff.Core).
/// </summary>
public static class LUi
{
    private static readonly ConditionalWeakTable<TextBlock, object> Hooked = new();
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static void Init(string language)
    {
        if (!L.Init(language)) return;
        // Alle sichtbaren Bäume (Fenster, Popups, Menüs) regelmäßig und nach Layout-Durchläufen übersetzen.
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Loaded) { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) => TranslateAll();
        timer.Start();
    }

    private static DateTime _lastWalk = DateTime.MinValue;
    private static bool _scheduled;

    /// <summary>Für Fenster aufrufen: übersetzt neue Elemente direkt nach dem Layout (ohne sichtbares Flackern).</summary>
    public static void Attach(Window w)
    {
        if (L.IsGerman) return;
        w.LayoutUpdated += (_, _) =>
        {
            if (_scheduled || (DateTime.Now - _lastWalk).TotalMilliseconds < 120) return;
            _scheduled = true;
            w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _scheduled = false;
                TranslateAll();
            }));
        };
    }

    public static void TranslateAll()
    {
        if (L.IsGerman) return;
        _lastWalk = DateTime.Now;
        foreach (var src in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().ToList())
        {
            if (src.RootVisual != null) Walk(src.RootVisual);
        }
    }

    private static void Walk(DependencyObject node)
    {
        if (node is TextBlock tb) Hook(tb);
        if (node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void Hook(TextBlock tb)
    {
        if (Hooked.TryGetValue(tb, out _)) return;
        Hooked.Add(tb, null);
        Translate(tb);
        TextDescriptor.AddValueChanged(tb, OnTextChanged);
    }

    private static void OnTextChanged(object sender, EventArgs e)
    {
        if (sender is TextBlock tb) Translate(tb);
    }

    private static void Translate(TextBlock tb)
    {
        if (tb.Inlines.Count > 1)
        {
            foreach (var run in tb.Inlines.OfType<Run>().Concat(tb.Inlines.OfType<Span>().SelectMany(s => s.Inlines.OfType<Run>())))
            {
                var tr = L.T(run.Text);
                if (tr != run.Text) run.Text = tr;
            }
            return;
        }
        var text = tb.Text;
        if (string.IsNullOrEmpty(text)) return;
        var translated = L.T(text);
        if (translated != text) tb.SetCurrentValue(TextBlock.TextProperty, translated);
    }
}
