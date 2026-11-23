using System.Runtime.InteropServices;
using System.Text;

namespace NetStuff.Services;

/// <summary>Native WLAN API (wlanapi.dll) – Scan und BSS-Liste mit Auswertung der Informationselemente.</summary>
public sealed class WlanApi : IDisposable
{
    private IntPtr _handle;

    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr bssList);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);

    public WlanApi()
    {
        uint r = WlanOpenHandle(2, IntPtr.Zero, out _, out _handle);
        if (r != 0) throw new WlanException("WLAN-Dienst nicht verfügbar (WLAN AutoConfig). Fehler " + r, r);
    }

    public record Interface(Guid Id, string Description, int State);

    public List<Interface> Interfaces()
    {
        var list = new List<Interface>();
        uint r = WlanEnumInterfaces(_handle, IntPtr.Zero, out var ptr);
        if (r != 0) throw new WlanException("WLAN-Schnittstellen konnten nicht gelesen werden. Fehler " + r, r);
        try
        {
            int count = Marshal.ReadInt32(ptr, 0);
            for (int i = 0; i < count; i++)
            {
                var item = ptr + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var desc = Marshal.PtrToStringUni(item + 16, 256).TrimEnd('\0');
                int state = Marshal.ReadInt32(item + 16 + 512);
                list.Add(new Interface(guid, desc, state));
            }
        }
        finally { WlanFreeMemory(ptr); }
        return list;
    }

    public void Scan(Guid iface)
    {
        WlanScan(_handle, ref iface, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>BSSID des aktuell verbundenen Access Points (oder null).</summary>
    public string ConnectedBssid(Guid iface)
    {
        uint r = WlanQueryInterface(_handle, ref iface, 7 /* current_connection */, IntPtr.Zero, out _, out var data, IntPtr.Zero);
        if (r != 0) return null;
        try
        {
            var mac = new byte[6];
            Marshal.Copy(data + 560, mac, 0, 6);
            return NetUtil.FormatMac(mac);
        }
        finally { WlanFreeMemory(data); }
    }

    public record Connection(string Profile, string Ssid, string Bssid, int SignalQuality, int RxMbps, int TxMbps);

    /// <summary>Aktuelle Verbindung (oder null, wenn nicht verbunden).</summary>
    public Connection CurrentConnection(Guid iface)
    {
        uint r = WlanQueryInterface(_handle, ref iface, 7, IntPtr.Zero, out _, out var data, IntPtr.Zero);
        if (r != 0) return null;
        try
        {
            if (Marshal.ReadInt32(data, 0) != 1) return null; // wlan_interface_state_connected
            var profile = Marshal.PtrToStringUni(data + 8, 256).TrimEnd('\0');
            int ssidLen = Math.Min(32, Marshal.ReadInt32(data, 520));
            var ssid = new byte[ssidLen];
            Marshal.Copy(data + 524, ssid, 0, ssidLen);
            var mac = new byte[6];
            Marshal.Copy(data + 560, mac, 0, 6);
            return new Connection(profile, DecodeSsid(ssid), NetUtil.FormatMac(mac),
                Marshal.ReadInt32(data, 576), Marshal.ReadInt32(data, 580) / 1000, Marshal.ReadInt32(data, 584) / 1000);
        }
        finally { WlanFreeMemory(data); }
    }

    public List<WlanBss> GetBssList(Guid iface)
    {
        uint r = WlanGetNetworkBssList(_handle, ref iface, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out var ptr);
        if (r == 5)
            throw new WlanException("Zugriff verweigert – unter Windows 11 benötigt der WLAN-Scan die Standortberechtigung.", r);
        if (r != 0) throw new WlanException("BSS-Liste konnte nicht gelesen werden. Fehler " + r, r);

        var connected = ConnectedBssid(iface);
        var list = new List<WlanBss>();
        try
        {
            int count = Marshal.ReadInt32(ptr, 4);
            for (int i = 0; i < count; i++)
            {
                var e = ptr + 8 + i * 360;
                int ssidLen = Math.Min(32, Marshal.ReadInt32(e, 0));
                var ssidBytes = new byte[ssidLen];
                Marshal.Copy(e + 4, ssidBytes, 0, ssidLen);
                var mac = new byte[6];
                Marshal.Copy(e + 40, mac, 0, 6);
                int phy = Marshal.ReadInt32(e, 52);
                int rssi = Marshal.ReadInt32(e, 56);
                int quality = Marshal.ReadInt32(e, 60);
                ushort capability = (ushort)Marshal.ReadInt16(e, 88);
                int freqKhz = Marshal.ReadInt32(e, 92);
                int rateCount = Marshal.ReadInt32(e, 96);
                double maxRate = 0;
                for (int k = 0; k < Math.Min(rateCount / 2, 126); k++)
                {
                    ushort rate = (ushort)Marshal.ReadInt16(e, 100 + k * 2);
                    maxRate = Math.Max(maxRate, (rate & 0x7FFF) * 0.5);
                }
                int ieOffset = Marshal.ReadInt32(e, 352);
                int ieSize = Marshal.ReadInt32(e, 356);
                var ies = new byte[ieSize];
                if (ieSize > 0) Marshal.Copy(e + ieOffset, ies, 0, ieSize);

                var bss = new WlanBss
                {
                    Ssid = DecodeSsid(ssidBytes),
                    Bssid = NetUtil.FormatMac(mac),
                    Rssi = rssi,
                    Quality = quality,
                    FrequencyMhz = freqKhz / 1000,
                    MaxRateMbps = maxRate,
                };
                bss.Connected = connected != null && connected == bss.Bssid;
                (bss.Channel, bss.Band) = ChannelOf(bss.FrequencyMhz);
                ParseIes(bss, ies, capability, phy);
                list.Add(bss);
            }
        }
        finally { WlanFreeMemory(ptr); }
        return list;
    }

    public static (int Channel, string Band) ChannelOf(int mhz) => WlanParser.ChannelOf(mhz);
    private static string DecodeSsid(byte[] b) => WlanParser.DecodeSsid(b);
    private static void ParseIes(WlanBss bss, byte[] ies, ushort capability, int phy) => WlanParser.ParseIes(bss, ies, capability, phy);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
    }
}
