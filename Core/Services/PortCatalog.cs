namespace NetStuff.Services;

public static class PortCatalog
{
    public static readonly Dictionary<int, string> Names = new()
    {
        [20] = "FTP-Data", [21] = "FTP", [22] = "SSH", [23] = "Telnet", [25] = "SMTP", [53] = "DNS", [67] = "DHCP",
        [69] = "TFTP", [80] = "HTTP", [81] = "HTTP-Alt", [88] = "Kerberos", [110] = "POP3", [111] = "RPCbind",
        [119] = "NNTP", [123] = "NTP", [135] = "MS-RPC", [137] = "NetBIOS-NS", [139] = "NetBIOS", [143] = "IMAP",
        [161] = "SNMP", [179] = "BGP", [389] = "LDAP", [443] = "HTTPS", [445] = "SMB", [465] = "SMTPS",
        [500] = "IKE", [515] = "LPD", [548] = "AFP", [554] = "RTSP", [587] = "Submission", [631] = "IPP",
        [636] = "LDAPS", [853] = "DNS-over-TLS", [873] = "rsync", [902] = "VMware", [990] = "FTPS", [993] = "IMAPS",
        [995] = "POP3S", [1080] = "SOCKS", [1194] = "OpenVPN", [1433] = "MS-SQL", [1521] = "Oracle",
        [1701] = "L2TP", [1723] = "PPTP", [1883] = "MQTT", [1900] = "UPnP", [2049] = "NFS", [2082] = "cPanel",
        [2083] = "cPanel-SSL", [2222] = "SSH-Alt", [2375] = "Docker", [2376] = "Docker-TLS", [3000] = "Web-Dev",
        [3128] = "Proxy", [3306] = "MySQL", [3389] = "RDP", [3478] = "STUN", [4433] = "HTTPS-Alt", [5000] = "UPnP/Synology",
        [5001] = "Synology-SSL", [5060] = "SIP", [5061] = "SIP-TLS", [5222] = "XMPP", [5353] = "mDNS",
        [5432] = "PostgreSQL", [5900] = "VNC", [5985] = "WinRM", [5986] = "WinRM-SSL", [6379] = "Redis",
        [6443] = "Kubernetes", [7547] = "TR-069", [8000] = "HTTP-Alt", [8006] = "Proxmox", [8008] = "HTTP-Alt",
        [8009] = "Chromecast", [8080] = "HTTP-Proxy", [8081] = "HTTP-Alt", [8083] = "HTTP-Alt", [8086] = "InfluxDB",
        [8123] = "Home Assistant", [8443] = "HTTPS-Alt", [8448] = "Matrix", [8883] = "MQTT-TLS",
        [8888] = "HTTP-Alt", [9000] = "Portainer", [9090] = "Cockpit", [9100] = "Drucker (RAW)", [9200] = "Elasticsearch",
        [9443] = "HTTPS-Alt", [10000] = "Webmin", [11211] = "Memcached", [27017] = "MongoDB", [32400] = "Plex",
        [49152] = "UPnP", [51820] = "WireGuard", [62078] = "Apple-Sync",
    };

    /// <summary>Kurze Liste für die Geräteerkennung im Netzwerkscan.</summary>
    public static readonly int[] Quick = { 21, 22, 23, 53, 80, 139, 443, 445, 548, 554, 631, 1883, 3389, 1400, 5000, 5900, 8008, 8009, 8080, 8123, 8443, 9100, 32400, 49000, 62078 };

    public static readonly int[] Top100 =
    {
        7, 9, 13, 21, 22, 23, 25, 26, 37, 53, 79, 80, 81, 88, 106, 110, 111, 113, 119, 135, 139, 143, 144, 179, 199,
        389, 427, 443, 444, 445, 465, 513, 514, 515, 543, 544, 548, 554, 587, 631, 646, 873, 990, 993, 995, 1025,
        1026, 1027, 1028, 1029, 1110, 1433, 1720, 1723, 1755, 1883, 1900, 2000, 2001, 2049, 2121, 2717, 3000, 3128,
        3306, 3389, 3986, 4899, 5000, 5009, 5051, 5060, 5101, 5190, 5357, 5432, 5631, 5666, 5800, 5900, 6000, 6001,
        6646, 7070, 8000, 8008, 8009, 8080, 8081, 8443, 8888, 9100, 9999, 10000, 32768, 49152, 49153, 49154, 49155,
        49156, 49157,
    };

    public static string Name(int port) => Names.TryGetValue(port, out var n) ? n : "";

    /// <summary>Parst "22,80,443", "1-1024" oder Kombinationen.</summary>
    public static List<int> Parse(string spec)
    {
        var set = new SortedSet<int>();
        foreach (var part in (spec ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('-'))
            {
                var ab = part.Split('-');
                int a = int.Parse(ab[0]), b = int.Parse(ab[1]);
                if (a > b) (a, b) = (b, a);
                for (int p = Math.Max(1, a); p <= Math.Min(65535, b); p++) set.Add(p);
            }
            else
            {
                int p = int.Parse(part);
                if (p is >= 1 and <= 65535) set.Add(p);
            }
        }
        return set.ToList();
    }
}
