using System.Diagnostics;
using System.Net;
using NetStuff.Services;

namespace NetStuff.Desktop.Platforms;

/// <summary>
/// „--diag“: gibt Netzwerkdaten für die Fehlersuche im Terminal aus (Adapter, ARP-Tabelle, ARP/mDNS für das Gateway)
/// und beendet sich ohne Fenster.
/// </summary>
internal static class NetDiag
{
    public static void Run()
    {
        Console.WriteLine($"Net-Stuff Diagnose – {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"ARP-Dienst: {NetStuff.Platform.Arp?.GetType().Name ?? "(keiner)"}");
        Console.WriteLine();
        Console.WriteLine("Adapter (IPv4):");
        foreach (var (nic, addr, gw) in NetUtil.ActiveIPv4())
        {
            string mac;
            try { mac = NetUtil.FormatMac(nic.GetPhysicalAddress()); } catch (Exception ex) { mac = "Fehler: " + ex.Message; }
            string prefix;
            try { prefix = addr.PrefixLength.ToString(); } catch (Exception ex) { prefix = "Fehler: " + ex.Message; }
            string mask;
            try { mask = addr.IPv4Mask?.ToString() ?? "-"; } catch (Exception ex) { mask = "Fehler: " + ex.Message; }
            Console.WriteLine($"  {nic.Name,-8} {addr.Address,-16} /{prefix} Maske {mask}  GW {gw?.ToString() ?? "-"}  MAC {mac}  Typ {nic.NetworkInterfaceType}");
        }
        Console.WriteLine();
        Console.WriteLine($"Standardbereich: {NetUtil.DefaultScanRange()}");

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            Console.WriteLine();
            Console.WriteLine("arp -an (erste 15 Zeilen):");
            try
            {
                var psi = new ProcessStartInfo("/usr/sbin/arp", "-an") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                var err = p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in output.Split('\n').Take(15)) Console.WriteLine("  " + line);
                if (err != "") Console.WriteLine("  stderr: " + err);
                Console.WriteLine($"  Exit-Code {p.ExitCode}, {output.Split('\n').Length} Zeilen");
                if (OperatingSystem.IsMacOS()) Console.WriteLine($"  erkannte Einträge: {Mac.MacArpResolver.Parse(output).Count}");
            }
            catch (Exception ex) { Console.WriteLine("  Fehler: " + ex.Message); }
        }

        var gateway = NetUtil.ActiveIPv4().Select(x => x.Gateway).FirstOrDefault(g => g != null);
        if (gateway != null)
        {
            Console.WriteLine();
            OuiDatabase.EnsureLoadedAsync().GetAwaiter().GetResult();
            var sw = Stopwatch.StartNew();
            var mac = NetUtil.Arp(gateway);
            Console.WriteLine($"ARP {gateway}: {(mac == null ? "keine MAC" : NetUtil.FormatMac(mac))} ({sw.ElapsedMilliseconds} ms)");
            Console.WriteLine($"Hersteller: {(mac == null ? "-" : OuiDatabase.Lookup(NetUtil.FormatMac(mac)))}");
            sw.Restart();
            var name = NetUtil.ReverseDnsAsync(gateway).GetAwaiter().GetResult();
            Console.WriteLine($"Name {gateway}: {(name == "" ? "-" : name)} ({sw.ElapsedMilliseconds} ms)");
        }
    }
}
