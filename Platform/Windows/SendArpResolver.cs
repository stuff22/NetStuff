using System.Net;
using System.Runtime.InteropServices;

namespace NetStuff.Platforms.Windows;

/// <summary>ARP über iphlpapi.dll (SendARP).</summary>
public sealed class SendArpResolver : IArpResolver
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref int physAddrLen);

    public byte[] Resolve(IPAddress ip)
    {
        // Laut API-Doku muss der Puffer mindestens zwei ULONG (8 Byte) groß sein – sonst Speicherüberschreibung!
        var buf = new byte[8];
        int len = buf.Length;
        uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        return SendARP(dest, 0, buf, ref len) == 0 && len == 6 ? buf[..6] : null;
    }
}
