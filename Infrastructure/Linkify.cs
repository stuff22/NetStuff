using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetStuff.Services;

namespace NetStuff.Infrastructure;

/// <summary>
/// Macht URLs, E-Mail-Adressen, IP-Adressen und Hostnamen in Texten anklickbar.
/// URL → Browser · E-Mail → Mailprogramm · IP/Host → Menü mit passenden Werkzeugen.
/// Verwendung: &lt;TextBlock infra:Linkify.Text="{Binding Wert}" /&gt;
/// Tabellen (DataGrid) und Textausgaben (schreibgeschützte TextBox, Doppelklick) werden automatisch erweitert.
/// </summary>
public static class Linkify
{
    /* ------------------------------------------------------------------ Erkennung */

    private static readonly Regex Pattern = new(
        @"(?<url>\b(?:https?|ftp)://[^\s""'<>()\[\]]+)" +
        @"|(?<mail>\b[\w.+-]+@(?:[\w-]+\.)+[a-zA-Z]{2,24}\b)" +
        @"|(?<mac>\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b)" +
        @"|(?<ip4>(?<![\w.])(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}(?:/\d{1,2})?(?![\w.]))" +
        @"|(?<ip6>(?<![\w:.])(?=[0-9a-fA-F:]*:[0-9a-fA-F]*:)[0-9a-fA-F:]{2,39}(?:%\w+)?(?:/\d{1,3})?(?![\w:]))" +
        @"|(?<host>(?<![\w@./-])(?:[a-zA-Z0-9_](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+(?<tld>[a-zA-Z]{2,24})\.?(?![\w-]))",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Tlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "info", "biz", "io", "dev", "app", "cloud", "online", "shop", "store", "site", "tech", "xyz",
        "local", "lan", "home", "arpa", "edu", "gov", "mil", "int", "eu", "asia", "mobi", "name", "pro", "aero", "museum",
        "wien", "berlin", "hamburg", "bayern", "swiss", "gmbh", "digital", "email", "network", "systems", "solutions", "services",
    };

    /// <summary>Dateiendungen, die wie Länderkennungen aussehen, aber keine Hostnamen sind.</summary>
    private static readonly HashSet<string> NotTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "js", "cs", "py", "sh", "md", "rb", "db", "gz", "7z", "ts", "ps", "mk", "hh", "ms",
    };

    public enum Kind { None, Url, Mail, Ip, Host }

    public static Kind Classify(Match m, out string target)
    {
        target = m.Value;
        if (m.Groups["url"].Success) { target = m.Value.TrimEnd('.', ',', ';', ':', '!', '?'); return Kind.Url; }
        if (m.Groups["mail"].Success) return Kind.Mail;
        if (m.Groups["mac"].Success) return Kind.None;
        if (m.Groups["ip4"].Success)
        {
            var ip = m.Value.Split('/')[0];
            target = m.Value.Contains('/') ? m.Value : ip;
            return IPAddress.TryParse(ip, out _) ? Kind.Ip : Kind.None;
        }
        if (m.Groups["ip6"].Success)
        {
            var raw = m.Value.Split('/')[0];
            var noZone = raw.Split('%')[0];
            target = raw;
            return noZone.Contains(':') && IPAddress.TryParse(noZone, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? Kind.Ip : Kind.None;
        }
        if (m.Groups["host"].Success)
        {
            var tld = m.Groups["tld"].Value;
            target = m.Value.TrimEnd('.');
            if (NotTlds.Contains(tld)) return Kind.None;
            return Tlds.Contains(tld) || tld.Length == 2 ? Kind.Host : Kind.None;
        }
        return Kind.None;
    }

    /* ------------------------------------------------------------------ TextBlock */

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Linkify), new PropertyMetadata(null, OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    /// <summary>Ursprünglicher Bindungspfad einer umgewandelten Tabellenspalte (für Berichte).</summary>
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(Linkify), new PropertyMetadata(null));

    public static string GetPath(DependencyObject d) => (string)d.GetValue(PathProperty);
    public static void SetPath(DependencyObject d, string value) => d.SetValue(PathProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        var text = L.T(e.NewValue as string ?? "");
        tb.Inlines.Clear();
        if (text.Length == 0) return;

        int last = 0;
        foreach (Match m in Pattern.Matches(text))
        {
            var kind = Classify(m, out var target);
            if (kind == Kind.None) continue;
            if (m.Index > last) tb.Inlines.Add(new Run(text[last..m.Index]));
            tb.Inlines.Add(MakeLink(tb, m.Value, kind, target));
            last = m.Index + m.Length;
        }
        if (last < text.Length) tb.Inlines.Add(new Run(text[last..]));
    }

    private static Hyperlink MakeLink(FrameworkElement owner, string text, Kind kind, string target)
    {
        var link = new Hyperlink(new Run(text)) { TextDecorations = null, Cursor = Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        link.Click += (_, e) =>
        {
            e.Handled = true;
            Activate(owner, kind, target);
        };
        return link;
    }

    /// <summary>Aktion für einen erkannten Link ausführen.</summary>
    public static void Activate(FrameworkElement owner, Kind kind, string target)
    {
        switch (kind)
        {
            case Kind.Url: NetUtil.OpenUrl(target); break;
            case Kind.Mail: NetUtil.OpenUrl("mailto:" + target); break;
            case Kind.Ip:
            case Kind.Host: ShowMenu(owner, target, kind); break;
        }
    }

    private static void ShowMenu(FrameworkElement owner, string target, Kind kind)
    {
        var main = Application.Current.MainWindow as MainWindow;
        var host = target.Split('/')[0];          // CIDR-Suffix für Einzelwerkzeuge entfernen
        var plain = host.Split('%')[0];            // IPv6-Zone entfernen
        bool isIp = kind == Kind.Ip;
        bool isNet = target.Contains('/');
        bool isV6 = plain.Contains(':');
        var urlHost = isV6 ? $"[{plain}]" : plain;

        var menu = new ContextMenu { PlacementTarget = owner, Placement = PlacementMode.MousePoint };
        void Item(string header, string glyph, Action action)
        {
            var mi = new MenuItem { Header = L.T(header), Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") } };
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }

        var title = new MenuItem { Header = target, IsEnabled = false, FontWeight = FontWeights.SemiBold };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        if (isNet && !isV6) Item("Netzwerk scannen", "", () => main?.RunTool("netscan", target, true));
        Item("Ping", "", () => main?.RunTool("ping", plain, true));
        Item("Traceroute & Karte", "", () => main?.RunTool("trace", plain, true));
        Item("Port-Scan", "", () => main?.RunTool("portscan", plain, true));
        Item(isIp ? "DNS (Reverse-Lookup)" : "DNS-Checker", "", () => main?.RunTool("dns", plain, true));
        if (!isIp) Item("Domain-Check", "", () => main?.RunTool("domaincheck", plain, true));
        if (!isIp || !NetUtil.IsPrivate(IPAddress.Parse(plain))) Item("Whois / RDAP", "", () => main?.RunTool("whois", plain, true));
        Item("SSL/TLS prüfen", "", () => main?.RunTool("ssl", plain, true));
        Item("HTTP-Header", "", () => main?.RunTool("http", "https://" + urlHost, true));
        if (isIp && !isV6 && NetUtil.IsPrivate(IPAddress.Parse(plain))) Item("SNMP abfragen", "", () => main?.RunTool("snmp", plain, true));
        menu.Items.Add(new Separator());
        Item("Im Browser öffnen (https)", "", () => NetUtil.OpenUrl("https://" + urlHost));
        Item("Im Browser öffnen (http)", "", () => NetUtil.OpenUrl("http://" + urlHost));
        Item("Kopieren", "", () => { try { Clipboard.SetText(target); } catch { } });
        menu.IsOpen = true;
    }

    /* ------------------------------------------------------------------ Automatische Erweiterung */

    private static readonly ConditionalWeakTable<TextBox, object> HookedBoxes = new();
    private static DateTime _lastWalk = DateTime.MinValue;
    private static bool _scheduled;

    /// <summary>Für das Hauptfenster aufrufen: erweitert Tabellen und Textausgaben laufend.</summary>
    public static void Attach(Window w)
    {
        w.LayoutUpdated += (_, _) =>
        {
            if (_scheduled || (DateTime.Now - _lastWalk).TotalMilliseconds < 150) return;
            _scheduled = true;
            w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                _scheduled = false;
                _lastWalk = DateTime.Now;
                Walk(w);
            }));
        };
    }

    private static void Walk(DependencyObject node)
    {
        switch (node)
        {
            case DataGrid dg:
                ConvertGrid(dg);
                break; // Zellen werden über die Vorlagen erweitert
            case TextBox box when box.IsReadOnly && box.AcceptsReturn:
                HookBox(box);
                return;
        }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i));
    }

    /// <summary>Ersetzt Textspalten einer Tabelle durch Spalten mit anklickbaren Links.</summary>
    public static void ConvertGrid(DataGrid dg)
    {
        for (int i = 0; i < dg.Columns.Count; i++)
        {
            if (dg.Columns[i] is not DataGridTextColumn tc || tc.Binding is not Binding b || b.Path == null) continue;
            var path = b.Path.Path;
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
            if (tc.ElementStyle != null) factory.SetValue(FrameworkElement.StyleProperty, tc.ElementStyle);
            else factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var col = new DataGridTemplateColumn
            {
                Header = tc.Header,
                Width = tc.Width,
                MinWidth = tc.MinWidth,
                MaxWidth = tc.MaxWidth,
                Visibility = tc.Visibility,
                CanUserSort = tc.CanUserSort,
                SortMemberPath = string.IsNullOrEmpty(tc.SortMemberPath) ? path : tc.SortMemberPath,
                IsReadOnly = true,
                CellTemplate = new DataTemplate { VisualTree = factory },
            };
            SetPath(col, path);
            dg.Columns[i] = col;
        }
    }

    /// <summary>Schreibgeschützte Textausgaben: Doppelklick auf eine Adresse öffnet das Menü.</summary>
    private static void HookBox(TextBox box)
    {
        if (HookedBoxes.TryGetValue(box, out _)) return;
        HookedBoxes.Add(box, null);
        box.MouseDoubleClick += (_, e) =>
        {
            var text = box.Text;
            int pos = box.GetCharacterIndexFromPoint(e.GetPosition(box), true);
            if (pos < 0 || pos >= text.Length) return;
            static bool Stop(char c) => char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '(' or ')' or ',' or ';' or '[' or ']';
            int s = pos, t = pos;
            while (s > 0 && !Stop(text[s - 1])) s--;
            while (t < text.Length && !Stop(text[t])) t++;
            var token = text[s..t].Trim('.', ':');
            var m = Pattern.Match(token);
            if (!m.Success || m.Index != 0 || m.Length != token.Length) return;
            var kind = Classify(m, out var target);
            if (kind == Kind.None) return;
            e.Handled = true;
            Activate(box, kind, target);
        };
    }
}
