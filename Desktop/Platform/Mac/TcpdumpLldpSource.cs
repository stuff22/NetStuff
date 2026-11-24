using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using NetStuff.Services;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// LLDP/CDP-Mitschnitt unter macOS mit dem Bordmittel tcpdump (BPF). Dafür sind Administratorrechte nötig –
/// macOS fragt über den üblichen Kennwortdialog („do shell script … with administrator privileges“).
/// Es wird auf allen aktiven en*-Schnittstellen gleichzeitig mitgeschnitten (eingebaut, USB-/Thunderbolt-Ethernet, WLAN).
/// </summary>
public sealed class TcpdumpLldpSource : ILldpCaptureSource
{
    private const string Filter = "ether proto 0x88cc or ether dst 01:80:c2:00:00:0e or ether dst 01:00:0c:cc:cc:cc";

    public async Task<List<byte[]>> CaptureFramesAsync(int seconds, CancellationToken ct)
    {
        var ifaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && Regex.IsMatch(n.Name, @"^en\d+$"))
            .Select(n => n.Name).Distinct().ToList();
        if (ifaces.Count == 0) throw new Exception("Keine aktive Netzwerkschnittstelle (en0, en1 …) gefunden.");

        var dir = Path.Combine(Path.GetTempPath(), "NetStuff-lldp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // Ein tcpdump je Schnittstelle, nach der Wartezeit beenden (SIGTERM schreibt die Datei sauber zu Ende)
            var sh = "P=''; "
                     + string.Concat(ifaces.Select(i => $"/usr/sbin/tcpdump -i {i} -s 0 -U -w '{dir}/{i}.pcap' '{Filter}' >/dev/null 2>&1 & P=\"$P $!\"; "))
                     + $"sleep {seconds}; kill $P 2>/dev/null; wait; chmod 644 '{dir}'/*.pcap 2>/dev/null; true";
            var apple = $"do shell script \"{Escape(sh)}\" with administrator privileges with prompt \"{Escape("Net-Stuff möchte LLDP/CDP-Pakete der Switches mitschneiden (tcpdump).")}\"";

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(apple);
            using var p = Process.Start(psi) ?? throw new Exception("osascript konnte nicht gestartet werden.");
            var errTask = p.StandardError.ReadToEndAsync();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            var err = await errTask;
            if (p.ExitCode != 0)
            {
                if (err.Contains("-128") || err.Contains("abgebrochen", StringComparison.OrdinalIgnoreCase) || err.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Administratorrechte wurden nicht erteilt.");
                throw new Exception("Mitschnitt fehlgeschlagen: " + err.Trim());
            }

            var frames = new List<byte[]>();
            foreach (var f in Directory.GetFiles(dir, "*.pcap"))
            {
                try { frames.AddRange(PcapNg.ReadFrames(await File.ReadAllBytesAsync(f, ct))); } catch { }
            }
            return frames;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
