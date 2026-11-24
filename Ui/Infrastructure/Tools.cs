using NetStuff.Ui.Views;

namespace NetStuff.Ui.Infrastructure;

/// <summary>Ein Eintrag der Navigation. <see cref="Create"/> = null: in der Mac-Version noch nicht umgesetzt.</summary>
public record ToolInfo(string Key, string Title, string Group, Func<ToolView> Create);

/// <summary>Werkzeugliste – Schlüssel wie in der WPF-App (für --page).</summary>
public static class Tools
{
    public static readonly ToolInfo[] All =
    {
        new("dashboard", "Übersicht", "Start", () => new DashboardView()),
        new("projects", "Projekte & Berichte", "Start", () => new ProjectsView()),
        new("history", "Verlauf & Favoriten", "Start", () => new HistoryView()),

        new("ping", "Ping", "Erreichbarkeit", () => new PingView()),
        new("trace", "Traceroute & Karte", "Erreichbarkeit", () => new TracerouteView()),
        new("mtu", "Path-MTU", "Erreichbarkeit", () => new PathMtuView()),
        new("portscan", "Port-Scanner", "Erreichbarkeit", () => new PortScanView()),
        new("speed", "Speedtest & iPerf3", "Erreichbarkeit", () => new SpeedView()),
        new("monitor", "Überwachung", "Erreichbarkeit", () => new MonitorView()),

        new("netscan", "Netzwerkscanner", "Lokales Netz", () => new NetScanView()),
        new("wlan", "WLAN-Scanner", "Lokales Netz", () => new WlanView()),
        new("survey", "WLAN-Ausleuchtung", "Lokales Netz", () => new SurveyView()),
        new("lldp", "LLDP / CDP", "Lokales Netz", null),
        new("dhcp", "DHCP-Test", "Lokales Netz", null),
        new("snmp", "SNMP-Browser", "Lokales Netz", null),
        new("wol", "Wake-on-LAN", "Lokales Netz", () => new WolView()),
        new("subnet", "Subnetzrechner", "Lokales Netz", () => new SubnetView()),

        new("dns", "DNS-Checker", "DNS & Domains", () => new DnsView()),
        new("domaincheck", "Domain-Check", "DNS & Domains", () => new DomainCheckView()),
        new("resolver", "Resolver-Vergleich", "DNS & Domains", () => new ResolverView()),
        new("whois", "Whois / RDAP", "DNS & Domains", () => new WhoisView()),
        new("mail", "Mail-Check", "DNS & Domains", () => new MailView()),

        new("ssl", "SSL/TLS-Checker", "Web", () => new SslView()),
        new("http", "HTTP-Header", "Web", () => new HttpHeaderView()),
        new("cms", "CMS-Checker", "Web", () => new CmsView()),
        new("sitecopy", "Website-Kopierer", "Web", () => new SiteCopyView()),

        new("settings", "Einstellungen", "App", () => new SettingsView()),
    };

    public static ToolInfo Find(string key) => All.FirstOrDefault(t => t.Key == key);
}
