using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using NetStuff.Services;
using NetStuff.Ui.Infrastructure;

namespace NetStuff.Ui.Views;

public partial class SettingsView : ToolView
{
    private bool _loading = true;
    private readonly string _startLanguage;

    public SettingsView()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        _startLanguage = s.Language;
        Select(LangBox, s.Language);
        Select(ThemeBox, s.Theme);
        NotifyBox.IsChecked = s.Notifications;
        NotifyBox.IsEnabled = Platform.Notifier != null;
        CompanyBox.Text = s.CompanyName;
        TechBox.Text = s.Technician;
        ContactBox.Text = s.CompanyContact;
        LogoBox.Text = s.LogoFile;
        ShowLogo();
        DataDirText.Text = Platform.DataDir;
        var version = (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(SettingsView).Assembly).GetName().Version?.ToString(3);
        AboutText.Text = L.F("Version {0}", version) + "  ·  " + System.Runtime.InteropServices.RuntimeInformation.OSDescription + "\n"
                         + L.T("Das Schweizer Taschenmesser für Netzwerktechniker.") + "\n"
                         + L.T("Verwendete Dienste: ip-api.com (Geo-IP), ipify.org, speed.cloudflare.com, rdap.org, OpenStreetMap.") + "\n"
                         + L.T("Bibliotheken: Avalonia, DnsClient.NET, SharpSnmpLib.");

        LangBox.SelectionChanged += (_, _) => Changed();
        ThemeBox.SelectionChanged += (_, _) => { Changed(); ApplyTheme(); };
        NotifyBox.IsCheckedChanged += (_, _) => Changed();
        foreach (var box in new[] { CompanyBox, TechBox, ContactBox }) box.LostFocus += (_, _) => Changed();
        _loading = false;
    }

    public override bool IsTool => false;
    public override string PrimaryTarget => "";

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    private void Changed()
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.Language = Tag(LangBox);
        s.Theme = Tag(ThemeBox);
        s.Notifications = NotifyBox.IsChecked == true;
        s.CompanyName = (CompanyBox.Text ?? "").Trim();
        s.Technician = (TechBox.Text ?? "").Trim();
        s.CompanyContact = (ContactBox.Text ?? "").Trim();
        s.Save();
        RestartHint.IsVisible = s.Language != _startLanguage;
    }

    private void ApplyTheme()
    {
        if (_loading || Application.Current == null) return;
        Application.Current.RequestedThemeVariant = Tag(ThemeBox) switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    private void ShowLogo()
    {
        try
        {
            var f = AppSettings.Current.LogoFile;
            LogoPreview.Source = !string.IsNullOrEmpty(f) && File.Exists(f) && !f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new Bitmap(f)
                : new Bitmap(AssetLoader.Open(new Uri("avares://NetStuff.Ui/Assets/netstuff.png")));
        }
        catch { LogoPreview.Source = null; }
    }

    private async void Logo_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickOpenFileAsync(L.T("Bilder"), "*.png", "*.jpg", "*.jpeg", "*.svg");
        if (file == null) return;
        try
        {
            var target = Path.Combine(Platform.DataDir, "logo" + Path.GetExtension(file).ToLowerInvariant());
            File.Copy(file, target, true);
            AppSettings.Current.LogoFile = target;
            AppSettings.Current.Save();
            LogoBox.Text = target;
            ShowLogo();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void LogoRemove_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.LogoFile = "";
        AppSettings.Current.Save();
        LogoBox.Text = "";
        ShowLogo();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => NetUtil.OpenUrl(Platform.DataDir);

    private async void Oui_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = L.T("Lade Herstellerliste …");
        try
        {
            await OuiDatabase.EnsureLoadedAsync(true);
            StatusText.Text = L.F("{0:N0} Einträge geladen", OuiDatabase.Count);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
