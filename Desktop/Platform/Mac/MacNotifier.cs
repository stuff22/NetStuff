using System.Diagnostics;

namespace NetStuff.Desktop.Platforms.Mac;

/// <summary>macOS-Mitteilungen über AppleScript („display notification“).</summary>
public sealed class MacNotifier : INotifier
{
    public void Show(string title, string message)
    {
        static string Q(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add($"display notification \"{Q(message)}\" with title \"{Q(title)}\"");
        Process.Start(psi)?.Dispose();
    }
}
