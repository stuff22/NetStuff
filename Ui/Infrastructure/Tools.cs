using NetStuff.Ui.Views;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Ein Eintrag der Navigation. <see cref="Create"/> = null: in der Mac-Version noch nicht umgesetzt.</summary>
public record ToolInfo(string Key, string Title, string Group, Func<ToolView> Create);

/// <summary>Werkzeugliste – Schlüssel wie in der WPF-App (für --page).</summary>
public static class Tools
{
    public static readonly ToolInfo[] All =
    {
        new("dashboard", "Übersicht", "Start", null),
        new("projects", "Projekte & Berichte", "Start", null),
        new("history", "Verlauf & Favoriten", "Start", null),

        new("ping", "Ping", "Erreichbarkeit", () => new PingView()),
        new("trace", "Traceroute & Karte", "Erreichbarkeit", () => new TracerouteView()),
        new("mtu", "Path-MTU", "Erreichbarkeit", null),
        new("portscan", "Port-Scanner", "Erreichbarkeit", null),
        new("speed", "Speedtest & iPerf3", "Erreichbarkeit", null),
        new("monitor", "Überwachung", "Erreichbarkeit", null),

        new("netscan", "Netzwerkscanner", "Lokales Netz", null),
        new("wlan", "WLAN-Scanner", "Lokales Netz", null),
        new("survey", "WLAN-Ausleuchtung", "Lokales Netz", null),
        new("lldp", "LLDP / CDP", "Lokales Netz", null),
        new("dhcp", "DHCP-Test", "Lokales Netz", null),
        new("snmp", "SNMP-Browser", "Lokales Netz", null),
        new("wol", "Wake-on-LAN", "Lokales Netz", null),
        new("subnet", "Subnetzrechner", "Lokales Netz", null),

        new("dns", "DNS-Checker", "DNS & Domains", () => new DnsView()),
        new("domaincheck", "Domain-Check", "DNS & Domains", null),
        new("resolver", "Resolver-Vergleich", "DNS & Domains", null),
        new("whois", "Whois / RDAP", "DNS & Domains", null),
        new("mail", "Mail-Check", "DNS & Domains", null),

        new("ssl", "SSL/TLS-Checker", "Web", null),
        new("http", "HTTP-Header", "Web", null),
        new("cms", "CMS-Checker", "Web", null),
        new("sitecopy", "Website-Kopierer", "Web", null),
    };

    public static ToolInfo Find(string key) => All.FirstOrDefault(t => t.Key == key);
}
