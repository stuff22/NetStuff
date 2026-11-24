using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetStuff.Services;

/// <summary>
/// ICMP-Echo über einen Datagramm-Socket (SOCK_DGRAM/IPPROTO_ICMP) – unter macOS und iOS ohne Root-Rechte erlaubt.
/// Anders als System.Net.NetworkInformation.Ping liefert er bei abgelaufener TTL die Adresse des antwortenden
/// Routers (Traceroute) und die echte TTL der Antwort.
/// </summary>
public sealed class DatagramIcmpSender : IIcmpSender
{
    private static int _seq = Random.Shared.Next(0, 30000);
    private static readonly ushort Id = (ushort)(Environment.ProcessId & 0xFFFF);
    private readonly IIcmpSender _fallback = new SystemIcmpSender();

    /// <summary>Prüft, ob das System ICMP-Datagramm-Sockets erlaubt.</summary>
    public static bool IsSupported()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp);
            return true;
        }
        catch { return false; }
    }

    public async Task<IcmpResult> SendAsync(IPAddress ip, int timeoutMs, byte[] payload, int ttl, bool dontFragment)
    {
        bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        Socket s;
        try
        {
            s = new Socket(ip.AddressFamily, SocketType.Dgram, v6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp);
            s.Ttl = (short)Math.Clamp(ttl, 1, 255);
            if (dontFragment && !v6) s.DontFragment = true;
        }
        catch (Exception)
        {
            return await _fallback.SendAsync(ip, timeoutMs, payload, ttl, dontFragment);
        }

        using (s)
        {
            ushort seq = (ushort)(Interlocked.Increment(ref _seq) & 0xFFFF);
            var packet = new byte[8 + payload.Length];
            packet[0] = (byte)(v6 ? 128 : 8);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), Id);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), seq);
            payload.CopyTo(packet, 8);
            if (!v6) BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), Checksum(packet)); // ICMPv6: Prüfsumme setzt der Kernel

            var sw = Stopwatch.StartNew();
            try { await s.SendToAsync(packet, SocketFlags.None, new IPEndPoint(ip, 0)); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
            {
                return new IcmpResult(IPStatus.PacketTooBig, ip, sw.Elapsed.TotalMilliseconds, 0);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
            {
                var st = ex.SocketErrorCode == SocketError.HostUnreachable ? IPStatus.DestinationHostUnreachable : IPStatus.DestinationNetworkUnreachable;
                return new IcmpResult(st, ip, sw.Elapsed.TotalMilliseconds, 0);
            }

            var buf = new byte[2048];
            EndPoint any = new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            while (true)
            {
                var remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remaining <= 0) return new IcmpResult(IPStatus.TimedOut, null, sw.Elapsed.TotalMilliseconds, 0);
                SocketReceiveFromResult r;
                using (var cts = new CancellationTokenSource(remaining))
                {
                    try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, any, cts.Token); }
                    catch (OperationCanceledException) { return new IcmpResult(IPStatus.TimedOut, null, sw.Elapsed.TotalMilliseconds, 0); }
                    catch (SocketException) { return new IcmpResult(IPStatus.TimedOut, null, sw.Elapsed.TotalMilliseconds, 0); }
                }
                var from = ((IPEndPoint)r.RemoteEndPoint).Address;
                var res = v6 ? Parse6(buf.AsSpan(0, r.ReceivedBytes), seq) : Parse4(buf.AsSpan(0, r.ReceivedBytes), seq);
                if (res is { } x) return new IcmpResult(x.Status, from, sw.Elapsed.TotalMilliseconds, x.Ttl);
            }
        }
    }

    /// <summary>IPv4: macOS liefert den IP-Kopf mit (daraus die TTL), Linux nicht – beides wird erkannt.</summary>
    internal static (IPStatus Status, int Ttl)? Parse4(ReadOnlySpan<byte> d, ushort seq)
    {
        int off = 0, ttl = 0;
        if (d.Length >= 20 && d[0] >> 4 == 4)
        {
            off = (d[0] & 0x0F) * 4;
            ttl = d[8];
        }
        if (d.Length < off + 8) return null;
        byte type = d[off], code = d[off + 1];
        switch (type)
        {
            case 0: // Echo Reply
                return SeqAt(d, off + 6) == seq ? (IPStatus.Success, ttl) : null;
            case 11: // Time Exceeded
            case 3:  // Destination Unreachable
                int inner = off + 8;
                if (d.Length < inner + 20) return null;
                int innerIcmp = inner + (d[inner] & 0x0F) * 4;
                if (d.Length < innerIcmp + 8 || SeqAt(d, innerIcmp + 6) != seq) return null;
                if (type == 11) return (IPStatus.TtlExpired, ttl);
                return (code switch
                {
                    0 => IPStatus.DestinationNetworkUnreachable,
                    1 => IPStatus.DestinationHostUnreachable,
                    4 => IPStatus.PacketTooBig,
                    _ => IPStatus.DestinationUnreachable,
                }, ttl);
            default:
                return null;
        }
    }

    /// <summary>IPv6: ohne IP-Kopf; Fehlermeldungen enthalten den ursprünglichen IPv6-Kopf (40 Bytes).</summary>
    internal static (IPStatus Status, int Ttl)? Parse6(ReadOnlySpan<byte> d, ushort seq)
    {
        if (d.Length < 8) return null;
        byte type = d[0], code = d[1];
        if (type == 129) return SeqAt(d, 6) == seq ? (IPStatus.Success, 0) : null;
        if (type is not (1 or 2 or 3)) return null;
        int innerIcmp = 8 + 40;
        if (d.Length < innerIcmp + 8 || SeqAt(d, innerIcmp + 6) != seq) return null;
        return (type switch
        {
            3 => IPStatus.TimeExceeded,
            2 => IPStatus.PacketTooBig,
            _ => code == 3 ? IPStatus.DestinationHostUnreachable : IPStatus.DestinationUnreachable,
        }, 0);
    }

    private static ushort SeqAt(ReadOnlySpan<byte> d, int i) => BinaryPrimitives.ReadUInt16BigEndian(d[i..]);

    internal static ushort Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xFFFF) + (sum >> 16);
        return (ushort)~sum;
    }
}
