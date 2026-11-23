using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using NetStuff.Infrastructure;
using NetStuff.Services;

namespace NetStuff.Pages;

public partial class SubnetPage : ToolPage
{
    public record SplitRow(int Index, string Network, string Hosts);

    private (BigInteger Net, int Prefix, bool V6)? _current;

    public SubnetPage()
    {
        InitializeComponent();
        SplitBox.ValueChanged += (_, _) => UpdateSplit();
        Loaded += (_, _) => Calculate();
    }

    public override void OnNavigatedTo(object parameter)
    {
        if (parameter is string s) InputBox.Text = s;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => Calculate();

    private void Calculate()
    {
        if (ResultsPanel == null) return;
        ResultsPanel.Children.Clear();
        _current = null;
        var input = InputBox.Text.Trim();
        if (input == "") { UpdateSplit(); return; }

        try
        {
            string addrText; int prefix;
            var parts = input.Split(new[] { '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            addrText = parts[0];
            var ip = IPAddress.Parse(addrText);
            bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            int bits = v6 ? 128 : 32;
            if (parts.Length > 1)
            {
                if (parts[1].Contains('.'))
                {
                    var mask = NetUtil.ToUInt(IPAddress.Parse(parts[1]));
                    prefix = NetUtil.MaskToPrefix(mask);
                    if (NetUtil.PrefixToMask(prefix) != mask) throw new Exception("Ungültige Subnetzmaske (nicht zusammenhängend).");
                }
                else prefix = int.Parse(parts[1]);
            }
            else prefix = v6 ? 64 : 24;
            if (prefix < 0 || prefix > bits) throw new Exception($"Präfix muss zwischen 0 und {bits} liegen.");

            if (v6) ShowV6(ip, prefix);
            else ShowV4(ip, prefix);
        }
        catch (Exception ex)
        {
            ResultsPanel.Children.Add(UiFactory.Message(ex is FormatException ? "Ungültige Adresse." : ex.Message, Level.Warn));
        }
        UpdateSplit();
    }

    private void ShowV4(IPAddress ip, int prefix)
    {
        uint addr = NetUtil.ToUInt(ip);
        uint mask = NetUtil.PrefixToMask(prefix);
        uint net = addr & mask, bc = net | ~mask;
        long total = 1L << (32 - prefix);
        long usable = prefix >= 31 ? total : total - 2;
        uint first = prefix >= 31 ? net : net + 1, last = prefix >= 31 ? bc : bc - 1;
        _current = (net, prefix, false);

        string Bin(uint v) => string.Join(".", new[] { v >> 24, (v >> 16) & 255, (v >> 8) & 255, v & 255 }.Select(o => Convert.ToString((int)o, 2).PadLeft(8, '0')));
        byte b0 = (byte)(addr >> 24);
        string cls = b0 < 128 ? "A" : b0 < 192 ? "B" : b0 < 224 ? "C" : b0 < 240 ? "D (Multicast)" : "E (reserviert)";
        string kind = NetUtil.IsPrivate(ip) ? (b0 == 100 ? "Carrier-Grade-NAT (100.64/10)" : b0 == 169 ? "Link-Local (APIPA)" : b0 == 127 ? "Loopback" : "privat (RFC 1918)") : "öffentlich";

        ResultsPanel.Children.Add(UiFactory.Section("IPv4-Netz", new[]
        {
            new CheckItem("Adresse", $"{ip}/{prefix}"),
            new CheckItem("Netzadresse", $"{NetUtil.FromUInt(net)}/{prefix}", Level.Ok),
            new CheckItem("Subnetzmaske", NetUtil.FromUInt(mask).ToString()),
            new CheckItem("Wildcard-Maske", NetUtil.FromUInt(~mask).ToString()),
            new CheckItem("Broadcast", prefix >= 31 ? "– (Punkt-zu-Punkt)" : NetUtil.FromUInt(bc).ToString()),
            new CheckItem("Erster Host", NetUtil.FromUInt(first).ToString()),
            new CheckItem("Letzter Host", NetUtil.FromUInt(last).ToString()),
            new CheckItem("Nutzbare Hosts", usable.ToString("N0")),
            new CheckItem("Adressen gesamt", total.ToString("N0")),
            new CheckItem("Klasse / Art", $"Klasse {cls} · {kind}"),
            new CheckItem("Reverse-Zone", ReverseZone(net, prefix)),
        }));
        ResultsPanel.Children.Add(UiFactory.Section("Binär", new[]
        {
            new CheckItem("Adresse", Bin(addr)),
            new CheckItem("Maske", Bin(mask)),
            new CheckItem("Netz", Bin(net)),
        }));
    }

    private static string ReverseZone(uint net, int prefix)
    {
        var oct = new[] { net >> 24, (net >> 16) & 255, (net >> 8) & 255, net & 255 };
        if (prefix > 24)
            return $"{oct[2]}.{oct[1]}.{oct[0]}.in-addr.arpa  (klassenlose Delegation nach RFC 2317 nötig)";
        int full = Math.Max(1, (prefix + 7) / 8); // auf nächste Oktettgrenze aufrunden
        int zones = 1 << (full * 8 - prefix);
        var parts = oct.Take(full).Reverse().ToArray();
        var zone = string.Join(".", parts) + ".in-addr.arpa";
        if (zones == 1) return zone;
        var first = parts[0];
        var rest = string.Join(".", parts.Skip(1));
        return $"{first}–{first + zones - 1}.{rest}.in-addr.arpa  ({zones} Zonen)";
    }

    private void ShowV6(IPAddress ip, int prefix)
    {
        var addr = ToBig(ip);
        var all = (BigInteger.One << 128) - 1;
        var mask = prefix == 0 ? BigInteger.Zero : (all >> (128 - prefix)) << (128 - prefix);
        var net = addr & mask;
        var last = net | (all ^ mask);
        _current = (net, prefix, true);
        var count = BigInteger.One << (128 - prefix);

        string kind = ip.IsIPv6LinkLocal ? "Link-Local (fe80::/10)" : ip.IsIPv6UniqueLocal ? "Unique Local (fc00::/7)" : IPAddress.IsLoopback(ip) ? "Loopback"
            : ip.IsIPv6Multicast ? "Multicast" : (ip.GetAddressBytes()[0] & 0xE0) == 0x20 ? "Global Unicast (2000::/3)" : "sonstige";

        ResultsPanel.Children.Add(UiFactory.Section("IPv6-Netz", new[]
        {
            new CheckItem("Adresse", $"{ip}/{prefix}"),
            new CheckItem("Ausgeschrieben", Expand(ip)),
            new CheckItem("Netz", $"{FromBig(net)}/{prefix}", Level.Ok),
            new CheckItem("Erste Adresse", FromBig(net).ToString()),
            new CheckItem("Letzte Adresse", FromBig(last).ToString()),
            new CheckItem("Adressen", count > 1_000_000_000_000 ? $"2^{128 - prefix}" : count.ToString("N0")),
            new CheckItem("/64-Netze darin", prefix <= 64 ? (BigInteger.One << (64 - prefix)).ToString("N0") : "– (kleiner als /64)"),
            new CheckItem("Art", kind),
        }));
    }

    private static string Expand(IPAddress ip) =>
        string.Join(":", Enumerable.Range(0, 8).Select(i => ((ip.GetAddressBytes()[i * 2] << 8) | ip.GetAddressBytes()[i * 2 + 1]).ToString("x4")));

    private static BigInteger ToBig(IPAddress ip) => new(ip.GetAddressBytes(), isUnsigned: true, isBigEndian: true);

    private static IPAddress FromBig(BigInteger v)
    {
        var bytes = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[16];
        Array.Copy(bytes, 0, full, 16 - bytes.Length, bytes.Length);
        return new IPAddress(full);
    }

    private void UpdateSplit()
    {
        if (SplitGrid == null) return;
        SplitGrid.ItemsSource = null;
        SplitTitle.Text = "";
        if (_current is not { } c) return;
        int newPrefix = (int)(SplitBox.Value ?? 0);
        int bits = c.V6 ? 128 : 32;
        if (newPrefix <= c.Prefix || newPrefix > bits)
        {
            SplitTitle.Text = $"Aufteilen: Präfix muss größer als /{c.Prefix} sein";
            return;
        }
        var count = BigInteger.One << (newPrefix - c.Prefix);
        var size = BigInteger.One << (bits - newPrefix);
        var rows = new List<SplitRow>();
        for (int i = 0; i < 1024 && i < count; i++)
        {
            var start = c.Net + size * i;
            var end = start + size - 1;
            string net, hosts;
            if (c.V6)
            {
                net = $"{FromBig(start)}/{newPrefix}";
                hosts = $"{FromBig(start)} – {FromBig(end)}";
            }
            else
            {
                net = $"{NetUtil.FromUInt((uint)start)}/{newPrefix}";
                hosts = newPrefix >= 31 ? $"{NetUtil.FromUInt((uint)start)} – {NetUtil.FromUInt((uint)end)}"
                    : $"{NetUtil.FromUInt((uint)(start + 1))} – {NetUtil.FromUInt((uint)(end - 1))}";
            }
            rows.Add(new SplitRow(i + 1, net, hosts));
        }
        SplitGrid.ItemsSource = rows;
        SplitTitle.Text = $"{count:N0} Subnetze /{newPrefix}" + (count > 1024 ? " (erste 1024 angezeigt)" : "") +
                          (c.V6 ? "" : $" · je {(newPrefix >= 31 ? size : size - 2):N0} Hosts");
    }
}
