using System.Diagnostics;

namespace NetStuff.Ui.Infrastructure;

/// <summary>
/// HTML → PDF über einen installierten Chromium-Browser im Hintergrund (Edge, Chrome, Brave, Chromium).
/// Ohne solchen Browser wird der Bericht im Standardbrowser geöffnet („Drucken → Als PDF sichern“).
/// </summary>
public static class PdfExport
{
    public static string FindBrowser()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     })
            {
                if (string.IsNullOrEmpty(root)) continue;
                candidates.Add(Path.Combine(root, @"Microsoft\Edge\Application\msedge.exe"));
                candidates.Add(Path.Combine(root, @"Google\Chrome\Application\chrome.exe"));
                candidates.Add(Path.Combine(root, @"BraveSoftware\Brave-Browser\Application\brave.exe"));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var apps in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                candidates.Add(Path.Combine(apps, "Google Chrome.app/Contents/MacOS/Google Chrome"));
                candidates.Add(Path.Combine(apps, "Microsoft Edge.app/Contents/MacOS/Microsoft Edge"));
                candidates.Add(Path.Combine(apps, "Brave Browser.app/Contents/MacOS/Brave Browser"));
                candidates.Add(Path.Combine(apps, "Chromium.app/Contents/MacOS/Chromium"));
            }
        }
        else
        {
            candidates.AddRange(new[] { "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge" });
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Druckt die HTML-Datei als PDF. false, wenn kein geeigneter Browser vorhanden ist oder der Druck scheitert.</summary>
    public static async Task<bool> HtmlToPdfAsync(string htmlFile, string pdfFile)
    {
        var browser = FindBrowser();
        if (browser == null) return false;
        var profile = Path.Combine(Path.GetTempPath(), "NetStuff-pdf-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (File.Exists(pdfFile)) File.Delete(pdfFile);
            var psi = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[]
                     {
                         "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check",
                         "--no-pdf-header-footer", "--print-to-pdf-no-header",
                         "--user-data-dir=" + profile,
                         "--print-to-pdf=" + pdfFile,
                         new Uri(htmlFile).AbsoluteUri,
                     })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return false; }
            return File.Exists(pdfFile) && new FileInfo(pdfFile).Length > 0;
        }
        catch { return false; }
        finally
        {
            try { Directory.Delete(profile, true); } catch { }
        }
    }
}
