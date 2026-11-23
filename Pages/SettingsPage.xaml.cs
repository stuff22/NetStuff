using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SettingsPage : ToolPage
{
    private bool _loading = true;
    private readonly string _startLanguage;

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        _startLanguage = s.Language;
        Select(LangBox, s.Language);
        Select(ThemeBox, s.Theme);
        NotifyBox.IsChecked = s.Notifications;
        CompanyBox.Text = s.CompanyName;
        TechBox.Text = s.Technician;
        ContactBox.Text = s.CompanyContact;
        LogoBox.Text = s.LogoFile;
        ShowLogo();
        DataDirText.Text = App.DataDir;
        AboutText.Text = L.F("Version {0}", typeof(App).Assembly.GetName().Version?.ToString(3)) + "\n"
                         + L.T("Das Schweizer Taschenmesser für Netzwerktechniker.") + "\n"
                         + L.T("Verwendete Dienste: ip-api.com (Geo-IP), ipify.org, speed.cloudflare.com, rdap.org, OpenStreetMap.") + "\n"
                         + L.T("Bibliotheken: WPF-UI, DnsClient.NET, SharpSnmpLib, Microsoft WebView2, Windows Community Toolkit.");
        _loading = false;
    }

    public override string PrimaryTarget => "";

    private static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items[0];
    }

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = AppSettings.Current;
        s.Language = Tag(LangBox);
        s.Theme = Tag(ThemeBox);
        s.Notifications = NotifyBox.IsChecked == true;
        s.CompanyName = CompanyBox.Text.Trim();
        s.Technician = TechBox.Text.Trim();
        s.CompanyContact = ContactBox.Text.Trim();
        s.Save();
        RestartHint.Visibility = s.Language != _startLanguage ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowLogo()
    {
        try
        {
            var f = AppSettings.Current.LogoFile;
            if (!string.IsNullOrEmpty(f) && File.Exists(f) && !f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(f);
                bmp.EndInit();
                LogoPreview.Source = bmp;
            }
            else LogoPreview.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/netstuff.png"));
        }
        catch { LogoPreview.Source = null; }
    }

    private void Logo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = L.T("Bilder") + "|*.png;*.jpg;*.jpeg;*.svg" };
        if (dlg.ShowDialog() != true) return;
        var target = Path.Combine(App.DataDir, "logo" + Path.GetExtension(dlg.FileName).ToLowerInvariant());
        File.Copy(dlg.FileName, target, true);
        AppSettings.Current.LogoFile = target;
        AppSettings.Current.Save();
        LogoBox.Text = target;
        ShowLogo();
    }

    private void LogoRemove_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.LogoFile = "";
        AppSettings.Current.Save();
        LogoBox.Text = "";
        ShowLogo();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => NetUtil.OpenUrl(App.DataDir);

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
