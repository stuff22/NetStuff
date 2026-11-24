using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>
/// Paketmitschnitt unter macOS mit dem Bordmittel tcpdump (BPF). Dafür sind Administratorrechte nötig –
/// macOS fragt über den üblichen Kennwortdialog („do shell script … with administrator privileges“).
/// Es wird auf allen aktiven en*-Schnittstellen gleichzeitig mitgeschnitten (eingebaut, USB-/Thunderbolt-Ethernet, WLAN).
/// Sobald tcpdump läuft, legt das Skript eine Marker-Datei an; dann wird <c>onReady</c> aufgerufen.
/// </summary>
public sealed class TcpdumpCapture : IPacketCapture
{
    public async Task<List<(byte[] Frame, double Time)>> CaptureAsync(string filter, int seconds, string reason, Func<Task> onReady, CancellationToken ct)
    {
        var ifaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && Regex.IsMatch(n.Name, @"^en\d+$"))
            .Select(n => n.Name).Distinct().ToList();
        if (ifaces.Count == 0) throw new Exception(L.T("Keine aktive Netzwerkschnittstelle (en0, en1 …) gefunden."));

        var dir = Path.Combine(Path.GetTempPath(), "NetStuff-cap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var ready = Path.Combine(dir, "ready");
        try
        {
            // Ein tcpdump je Schnittstelle; nach dem Start Marker-Datei anlegen, nach der Wartezeit beenden
            // (SIGTERM schreibt die Datei sauber zu Ende)
            var sh = "P=''; "
                     + string.Concat(ifaces.Select(i => $"/usr/sbin/tcpdump -i {i} -s 0 -U -w '{dir}/{i}.pcap' '{filter}' >/dev/null 2>&1 & P=\"$P $!\"; "))
                     + $"sleep 1; touch '{ready}'; chmod 666 '{ready}'; sleep {seconds}; kill $P 2>/dev/null; wait; chmod 644 '{dir}'/*.pcap 2>/dev/null; true";
            var apple = $"do shell script \"{Escape(sh)}\" with administrator privileges with prompt \"{Escape(reason)}\"";

            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(apple);
            using var p = Process.Start(psi) ?? throw new Exception(L.T("osascript konnte nicht gestartet werden."));
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                if (onReady != null)
                {
                    // warten, bis der Mitschnitt läuft (der Kennwortdialog kann beliebig lange offen sein)
                    while (!p.HasExited && !File.Exists(ready)) await Task.Delay(100, ct);
                    if (!p.HasExited) await onReady();
                }
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw;
            }
            var err = await errTask;
            if (p.ExitCode != 0)
            {
                if (err.Contains("-128") || err.Contains("abgebrochen", StringComparison.OrdinalIgnoreCase) || err.Contains("cancel", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(L.T("Administratorrechte wurden nicht erteilt."));
                throw new Exception(L.T("Mitschnitt fehlgeschlagen: ") + err.Trim());
            }

            var frames = new List<(byte[], double)>();
            foreach (var f in Directory.GetFiles(dir, "*.pcap"))
            {
                try { frames.AddRange(PcapNg.ReadFramesTimed(await File.ReadAllBytesAsync(f, ct))); } catch { }
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

/// <summary>LLDP/CDP-Mitschnitt unter macOS über <see cref="TcpdumpCapture"/>.</summary>
public sealed class TcpdumpLldpSource : ILldpCaptureSource
{
    private const string Filter = "ether proto 0x88cc or ether dst 01:80:c2:00:00:0e or ether dst 01:00:0c:cc:cc:cc";

    public async Task<List<byte[]>> CaptureFramesAsync(int seconds, CancellationToken ct)
    {
        var frames = await new TcpdumpCapture().CaptureAsync(Filter, seconds,
            L.T("Net-Stuff möchte LLDP/CDP-Pakete der Switches mitschneiden (tcpdump)."), null, ct);
        return frames.Select(f => f.Frame).ToList();
    }
}
