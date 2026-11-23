using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetStuff.Infrastructure;
using NetStuff.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NetStuff;

public class NavItem
{
    public NavItem(string key, string title, string glyph, string group, Func<ToolPage> factory, string keywords = "")
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        Group = group;
        Factory = factory;
        Keywords = keywords;
    }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string Group { get; }
    public string Keywords { get; }
    public Func<ToolPage> Factory { get; }
}

/// <summary>Ein geöffneter Tab.</summary>
public class ToolTab
{
    public NavItem Item { get; init; }
    public ToolPage Page { get; init; }
    public TabItem TabItem { get; init; }
}

public partial class MainWindow : FluentWindow
{
    private readonly List<NavItem> _items;
    private readonly List<ToolTab> _tabs = new();
    private readonly ICollectionView _view;
    private bool _syncingNav;
    private DispatcherTimer _infoTimer;

    private async Task StressAsync(int rounds)
    {
        var log = System.IO.Path.Combine(App.DataDir, "stress.log");
        void W(string s) => System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} {s}\n");
        System.IO.File.WriteAllText(log, "");
        var plan = new (string Key, string Target)[]
        {
            ("ping", "8.8.8.8, 1.1.1.1, it-stuff.at, 10.0.0.1"), ("trace", "it-stuff.at"), ("netscan", null),
            ("dhcp", null), ("survey", null), ("ping", "10.0.0.250"), ("http", "https://www.orf.at"),
        };
        for (int r = 1; r <= rounds; r++)
        {
            W($"Runde {r}: starte");
            foreach (var (key, target) in plan)
            {
                RunTool(key, target, true, newTab: true);
                await Task.Delay(400);
            }
            await Task.Delay(TimeSpan.FromSeconds(12 + r * 3));
            W($"Runde {r}: schließe {_tabs.Count} Tabs");
            foreach (var t in _tabs.ToList().Where(t => t.Item.Key != "dashboard")) { CloseTab(t); await Task.Delay(150); }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(3000);
        }
        W("FERTIG ohne Absturz");
        Application.Current.Shutdown();
    }

    public static event EventHandler ThemeChanged;
    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    public MainWindow()
    {
        InitializeComponent();
        LUi.Attach(this);
        Linkify.Attach(this);

        _items = new List<NavItem>
        {
            new("dashboard", "Übersicht", "", "Start", () => new DashboardPage(), "adapter ip öffentliche gateway overview"),
            new("projects", "Projekte & Berichte", "", "Start", () => new ProjectsPage(), "kunde bericht pdf report customer"),
            new("history", "Verlauf & Favoriten", "", "Start", () => new HistoryPage(), "history favorites"),

            new("ping", "Ping", "", "Erreichbarkeit", () => new PingPage(), "icmp latenz jitter"),
            new("trace", "Traceroute & Karte", "", "Erreichbarkeit", () => new TraceroutePage(), "tracert hops route map geo"),
            new("mtu", "Path-MTU", "", "Erreichbarkeit", () => new PathMtuPage(), "fragmentierung df mss"),
            new("portscan", "Port-Scanner", "", "Erreichbarkeit", () => new PortScanPage(), "tcp ports banner"),
            new("speed", "Speedtest & iPerf3", "", "Erreichbarkeit", () => new SpeedtestPage(), "bandbreite durchsatz download upload bandwidth"),
            new("monitor", "Überwachung", "", "Erreichbarkeit", () => new MonitorPage(), "monitoring alarm uptime"),

            new("netscan", "Netzwerkscanner", "", "Lokales Netz", () => new NetScanPage(), "lan arp hersteller geräte"),
            new("wlan", "WLAN-Scanner", "", "Lokales Netz", () => new WlanPage(), "wifi spektrum kanal ssid"),
            new("survey", "WLAN-Ausleuchtung", "", "Lokales Netz", () => new WlanSurveyPage(), "site survey raum heatmap"),
            new("lldp", "LLDP / CDP", "", "Lokales Netz", () => new LldpPage(), "switch port vlan nachbar neighbor"),
            new("dhcp", "DHCP-Test", "", "Lokales Netz", () => new DhcpPage(), "dhcp server rogue"),
            new("snmp", "SNMP-Browser", "", "Lokales Netz", () => new SnmpPage(), "mib oid switch drucker toner"),
            new("wol", "Wake-on-LAN", "", "Lokales Netz", () => new WolPage(), "magic packet aufwecken"),
            new("subnet", "Subnetzrechner", "", "Lokales Netz", () => new SubnetPage(), "cidr maske ipv6"),

            new("dns", "DNS-Checker", "", "DNS & Domains", () => new DnsPage(), "records mx txt aaaa"),
            new("domaincheck", "Domain-Check", "", "DNS & Domains", () => new DomainCheckPage(), "dnssec blacklist nameserver propagation"),
            new("resolver", "Resolver-Vergleich", "", "DNS & Domains", () => new ResolverPage(), "doh 1.1.1.1 8.8.8.8 propagation"),
            new("whois", "Whois / RDAP", "", "DNS & Domains", () => new WhoisPage(), "registrar domain inhaber"),
            new("mail", "Mail-Check", "", "DNS & Domains", () => new MailPage(), "spf dmarc dkim smtp starttls mx"),

            new("ssl", "SSL/TLS-Checker", "", "Web", () => new SslPage(), "zertifikat https tls"),
            new("http", "HTTP-Header", "", "Web", () => new HttpHeaderPage(), "security header hsts csp redirect"),
            new("cms", "CMS-Checker", "", "Web", () => new CmsPage(), "wordpress joomla typo3 shopware technologie"),
            new("sitecopy", "Website-Kopierer", "", "Web", () => new SiteCopyPage(), "httrack offline spiegel mirror download copier herunterladen"),

            new("settings", "Einstellungen", "", "", () => new SettingsPage(), "settings sprache language logo"),
        };

        _view = CollectionViewSource.GetDefaultView(_items.Where(i => i.Group != "").ToList());
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavItem.Group)));
        NavList.ItemsSource = _view;

        ProjectBox.ItemsSource = ProjectStore.Projects;
        ProjectBox.SelectedItem = ProjectStore.Active;
        ProjectStore.ActiveChanged += (_, _) => { if (ProjectBox.SelectedItem != ProjectStore.Active) ProjectBox.SelectedItem = ProjectStore.Active; };

        VersionText.Text = "Version " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);

        // Verlauf: jeder Klick auf einen Start-Button wird protokolliert
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyButtonClick), true);

        Loaded += (_, _) =>
        {
            switch (AppSettings.Current.Theme)
            {
                case "light": ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true); break;
                case "dark": ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true); break;
                default: ApplicationThemeManager.ApplySystemTheme(); break;
            }
            UpdateThemeLabel();
            if (!HandleStartArgs()) Open("dashboard");
        };
        Closing += (_, _) => { foreach (var t in _tabs) t.Page.OnClosing(); };
    }

    public string TitleOf(string key) => _items.FirstOrDefault(i => i.Key == key)?.Title ?? key;

    /* ------------------------------------------------------------------ Tabs */

    /// <summary>Öffnet ein Werkzeug (vorhandenen Tab aktivieren oder neuen Tab).</summary>
    public ToolPage Open(string key, object parameter = null, bool newTab = false)
    {
        var item = _items.FirstOrDefault(i => i.Key == key);
        if (item == null) return null;
        var tab = newTab ? null : _tabs.FirstOrDefault(t => t.Item.Key == key);
        if (tab == null) tab = CreateTab(item);
        Tabs.SelectedItem = tab.TabItem;
        tab.Page.OnNavigatedTo(parameter);
        return tab.Page;
    }

    /// <summary>Werkzeug öffnen, Ziel setzen und optional sofort starten.</summary>
    public void RunTool(string key, string target, bool run, bool newTab = false)
    {
        var page = Open(key, null, newTab);
        if (page == null) return;
        if (!string.IsNullOrEmpty(target)) page.SetPrimaryTarget(target);
        if (run) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(page.Run));
    }

    /// <summary>Kompatibilität: Navigation mit Parameter (öffnet/aktiviert den Tab).</summary>
    public void Navigate(string key, object parameter = null) => Open(key, parameter);

    private ToolTab CreateTab(NavItem item)
    {
        var page = item.Factory();
        int count = _tabs.Count(t => t.Item.Key == item.Key);

        var close = new System.Windows.Controls.Button
        {
            Content = new System.Windows.Controls.TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10 },
            Style = (Style)FindResource("TabHeaderClose"),
            Margin = new Thickness(8, 0, -4, 0),
        };
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = item.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), Margin = new Thickness(0, 2, 8, 0), Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush") });
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T(item.Title) + (count > 0 ? $" ({count + 1})" : "") });
        header.Children.Add(close);

        // Inhalt + Werkzeug-Buttons oben rechts im Tab (Zum Projektbericht, Favorit)
        var body = new Grid();
        body.Children.Add(new ContentControl { Content = page, Margin = new Thickness(24, 18, 24, 14), Focusable = false });
        bool reportable = item.Key is not ("projects" or "history" or "settings");
        Wpf.Ui.Controls.Button reportBtn = null, favBtn = null;
        if (reportable)
        {
            StackPanel Label(string glyph, string text)
            {
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                sp.Children.Add(new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), Margin = new Thickness(0, 2, 8, 0) });
                sp.Children.Add(new System.Windows.Controls.TextBlock { Text = L.T(text) });
                return sp;
            }
            reportBtn = new Wpf.Ui.Controls.Button { Content = Label("", "Zum Projektbericht"), Margin = new Thickness(0, 0, 6, 0) };
            favBtn = new Wpf.Ui.Controls.Button { Content = Label("", "Favorit") };
            var bar = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 24, 0),
            };
            bar.Children.Add(reportBtn);
            bar.Children.Add(favBtn);
            body.Children.Add(bar);
        }

        var tabItem = new TabItem
        {
            Header = header,
            Content = new Border
            {
                Margin = new Thickness(0, 4, 0, 0),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("LayerFillColorDefaultBrush"),
                BorderBrush = (Brush)FindResource("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                Child = body,
            },
        };
        var tab = new ToolTab { Item = item, Page = page, TabItem = tabItem };
        if (reportBtn != null) reportBtn.Click += async (_, _) => await AddToReportAsync(tab);
        if (favBtn != null) favBtn.Click += (_, _) => AddFavorite(tab);
        close.Click += (_, e) => { e.Handled = true; CloseTab(tab); };
        tabItem.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        _tabs.Add(tab);
        Tabs.Items.Add(tabItem);
        return tab;
    }

    private void CloseTab(ToolTab tab)
    {
        tab.Page.OnClosing();
        _tabs.Remove(tab);
        Tabs.Items.Remove(tab.TabItem);
        if (_tabs.Count == 0) Open("dashboard");
    }

    private ToolTab CurrentTab => _tabs.FirstOrDefault(t => t.TabItem == Tabs.SelectedItem);

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs) return;
        var tab = CurrentTab;
        if (tab == null) return;
        _syncingNav = true;
        NavList.SelectedItem = tab.Item.Group == "" ? null : tab.Item;
        if (NavList.SelectedItem != null) NavList.ScrollIntoView(tab.Item);
        _syncingNav = false;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav || NavList.SelectedItem is not NavItem item) return;
        bool newTab = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        Open(item.Key, null, newTab);
    }

    private void NavList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is NavItem item)
        {
            Open(item.Key, null, newTab: true);
            e.Handled = true;
        }
    }

    private void OpenInNewTab_Click(object sender, RoutedEventArgs e)
    {
        if (NavList.SelectedItem is NavItem item) Open(item.Key, null, newTab: true);
    }

    private void NavSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = NavSearch.Text?.Trim().ToLowerInvariant() ?? "";
        _view.Filter = q.Length == 0
            ? null
            : o => o is NavItem n && (n.Title.ToLowerInvariant().Contains(q) || L.T(n.Title).ToLowerInvariant().Contains(q)
                                      || n.Keywords.Contains(q) || n.Group.ToLowerInvariant().Contains(q));
    }

    /* ------------------------------------------------------------------ Werkzeugleiste */

    private void OnAnyButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase b || !ToolPage.RunButtonNames.Contains(b.Name)) return;
        DependencyObject d = b;
        while (d != null && d is not ToolPage) d = VisualTreeHelper.GetParent(d);
        if (d is not ToolPage page) return;
        var tab = _tabs.FirstOrDefault(t => t.Page == page);
        if (tab == null || tab.Item.Key is "projects" or "history" or "settings") return;
        HistoryStore.Add(tab.Item.Key, tab.Item.Title, page.PrimaryTarget);
    }

    public void ShowInfo(string text)
    {
        ToolbarInfo.Text = L.T(text);
        _infoTimer?.Stop();
        _infoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _infoTimer.Tick += (_, _) => { ToolbarInfo.Text = ""; _infoTimer.Stop(); };
        _infoTimer.Start();
    }

    private async void AddToReport_Click(object sender, RoutedEventArgs e) => await AddToReportAsync(CurrentTab);

    /// <summary>Ergebnis eines Tabs in das aktive Projekt übernehmen (legt bei Bedarf ein Projekt an).</summary>
    public async Task AddToReportAsync(ToolTab tab)
    {
        if (tab == null) return;
        if (tab.Item.Key is "projects" or "history" or "settings") { ShowInfo("Dieser Tab kann nicht in einen Bericht übernommen werden."); return; }

        var project = ProjectStore.Active;
        if (project == null)
        {
            project = ProjectStore.Create(L.T("Neues Projekt") + " " + DateTime.Now.ToString("dd.MM.yyyy"));
            ProjectStore.Active = project;
        }
        var entry = await ReportCapture.CaptureAsync(tab.Page, tab.Item.Key, L.T(tab.Item.Title));
        if (entry.Blocks.Count == 0) { ShowInfo("Keine Ergebnisse zum Übernehmen – bitte zuerst das Werkzeug ausführen."); return; }
        project.Entries.Add(entry);
        ProjectStore.Save(project);
        ShowInfo(L.F("✓ In „{0}“ übernommen ({1} Einträge)", project.Display, project.Entries.Count));
    }

    private void Favorite_Click(object sender, RoutedEventArgs e) => AddFavorite(CurrentTab);

    public void AddFavorite(ToolTab tab)
    {
        if (tab == null || tab.Item.Key is "projects" or "history" or "settings") return;
        var target = tab.Page.PrimaryTarget;
        HistoryStore.AddFavorite(new Favorite
        {
            Tool = tab.Item.Key,
            ToolTitle = tab.Item.Title,
            Target = target,
            Name = string.IsNullOrEmpty(target) ? L.T(tab.Item.Title) : $"{L.T(tab.Item.Title)}: {target}",
        });
        ShowInfo("★ Als Favorit gespeichert");
    }

    private void ProjectBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectBox.SelectedItem is Project p && p != ProjectStore.Active) ProjectStore.Active = p;
    }

    private void Projects_Click(object sender, RoutedEventArgs e) => Open("projects");

    private void Settings_Click(object sender, RoutedEventArgs e) => Open("settings");

    /* ------------------------------------------------------------------ Theme & Start */

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var next = IsDark ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(next, WindowBackdropType.Mica, true);
        UpdateThemeLabel();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateThemeLabel()
    {
        ThemeLabel.Text = L.T(IsDark ? "Helles Design" : "Dunkles Design");
    }

    /// <summary>
    /// Startparameter: NetStuff.exe --page dns [--param example.com] [--run] [--dark|--light]
    /// </summary>
    private bool HandleStartArgs()
    {
        var args = Environment.GetCommandLineArgs();
        string Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (args.Contains("--dark")) ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
        if (args.Contains("--light")) ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
        UpdateThemeLabel();

        // Test-/Dokumentationshilfe: --snapshot datei.png [--snapdelay s] → Fensterinhalt als Bild speichern und beenden
        var snap = Arg("--snapshot");
        if (snap != null)
        {
            int delay = int.TryParse(Arg("--snapdelay"), out var d) ? d : 8;
            var st = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            st.Tick += async (_, _) =>
            {
                st.Stop();
                var reportFile = Arg("--report");
                if (reportFile != null)
                {
                    try
                    {
                        var tab = CurrentTab;
                        var entry = await ReportCapture.CaptureAsync(tab.Page, tab.Item.Key, L.T(tab.Item.Title));
                        var p = new Project { Customer = "Muster GmbH", Site = "Testmessung", Address = "Hauptstraße 1\n5020 Salzburg", ContactPerson = "Max Mustermann", Notes = "Automatisch erzeugter Testbericht." };
                        p.Entries.Add(entry);
                        System.IO.File.WriteAllText(reportFile, ReportRenderer.Html(p));
                    }
                    catch (Exception ex) { System.IO.File.WriteAllText(reportFile + ".txt", ex.ToString()); }
                }
                try
                {
                    var root = (FrameworkElement)Content;
                    var dpi = VisualTreeHelper.GetDpi(this);
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(IsDark ? new SolidColorBrush(Color.FromRgb(32, 32, 32)) : new SolidColorBrush(Color.FromRgb(243, 243, 243)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                        dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                    }
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = System.IO.File.Create(snap);
                    enc.Save(fs);
                }
                catch (Exception ex) { System.IO.File.WriteAllText(snap + ".txt", ex.ToString()); }
                Application.Current.Shutdown();
            };
            st.Start();
        }

        // Belastungstest: mehrere Werkzeuge parallel starten, abbrechen, Tabs schließen – mehrere Runden
        if (int.TryParse(Arg("--stress"), out var rounds))
        {
            Open("dashboard");
            _ = StressAsync(rounds);
            return true;
        }

        var page = Arg("--page");
        if (page == null) return false;
        if (page != "dashboard") Open("dashboard");
        var param = Arg("--param");
        var p = Open(page);
        if (p == null) return false;
        if (param != null) p.SetPrimaryTarget(param);
        if (args.Contains("--run"))
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            t.Tick += (_, _) => { t.Stop(); p.Run(); };
            t.Start();
        }
        return true;
    }
}
