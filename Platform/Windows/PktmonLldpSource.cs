using System.Diagnostics;
using System.IO;
using System.Text;
using NetStuff.Services;

namespace NetStuff.Platforms.Windows;

/// <summary>Fängt LLDP- und CDP-Pakete mit dem Windows-Bordmittel pktmon (benötigt Administratorrechte).</summary>
public sealed class PktmonLldpSource : ILldpCaptureSource
{
    public async Task<List<byte[]>> CaptureFramesAsync(int seconds, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "NetStuff-lldp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var etl = Path.Combine(dir, "cap.etl");
        var pcap = Path.Combine(dir, "cap.pcapng");
        var script = Path.Combine(dir, "cap.cmd");

        File.WriteAllText(script, string.Join("\r\n", new[]
        {
            "@echo off",
            "pktmon stop >nul 2>&1",
            "pktmon filter remove >nul 2>&1",
            "pktmon filter add LLDP -d 0x88CC >nul",
            "pktmon filter add LLDPMAC -m 01-80-C2-00-00-0E >nul",
            "pktmon filter add CDP -m 01-00-0C-CC-CC-CC >nul",
            $"pktmon start --capture --pkt-size 0 --file-name \"{etl}\" >nul",
            $"ping -n {seconds + 1} 127.0.0.1 >nul",
            "pktmon stop >nul",
            $"pktmon etl2pcap \"{etl}\" --out \"{pcap}\" >nul",
            "pktmon filter remove >nul",
        }), Encoding.ASCII);

        var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        Process p;
        try { p = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { throw new Exception("Administratorrechte wurden nicht erteilt."); }

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c pktmon stop") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }); } catch { }
            throw;
        }
        if (!File.Exists(pcap)) throw new Exception("Mitschnitt fehlgeschlagen (pktmon nicht verfügbar?).");

        var frames = PcapNg.ReadFrames(File.ReadAllBytes(pcap));
        try { Directory.Delete(dir, true); } catch { }
        return frames;
    }
}
