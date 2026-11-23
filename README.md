# Net-Stuff

Das Schweizer Taschenmesser für Netzwerktechniker – native Windows-11-App (WPF, .NET 10, Fluent-Design mit Mica).
Deutsch und Englisch, läuft ohne Administratorrechte (nur LLDP/CDP fragt einmal nach).

## Werkzeuge (26)

| Bereich | Werkzeug | Funktionen |
|---|---|---|
| Start | **Übersicht** | Adapter, IPs, Gateway, DNS, DHCP, MTU; öffentliche IPv4/IPv6, Provider, AS, Standort |
| | **Projekte & Berichte** | Kundenprojekte, Ergebnisse jedes Werkzeugs per „Zum Bericht“ sammeln (inkl. Diagramme und Karte), Kommentare, Reihenfolge, Export als PDF/HTML mit eigenem Logo |
| | **Verlauf & Favoriten** | Jede Ausführung wird protokolliert; Favoriten per Stern-Button; Doppelklick startet erneut |
| Erreichbarkeit | **Ping** | bis 12 Ziele parallel, Verlust, Jitter, Live-Diagramm, DF-Bit |
| | **Traceroute & Karte** | Hops mit RTT, Reverse-DNS, Ort, Provider/AS, Weltkarte (OpenStreetMap) |
| | **Path-MTU** | binäre Suche mit DF-Bit, IPv4/IPv6, TCP-MSS |
| | **Port-Scanner** | TCP-Connect-Scan, Dienstnamen, Banner (SSH, HTTP, TLS …) |
| | **Speedtest & iPerf3** | Internet-Speedtest (Cloudflare): Download, Upload, Ping, Jitter; eingebauter iPerf3-Client (TCP, Upload/Download, mehrere Streams) – keine iperf3.exe nötig |
| | **Überwachung** | Ping/TCP/HTTP/DNS/TLS-Zertifikat dauerhaft prüfen, Verfügbarkeit, Ereignisprotokoll, Windows-Benachrichtigung bei Ausfall/Wiederkehr |
| Lokales Netz | **Netzwerkscanner** | ARP + Ping, Hostname, MAC-Hersteller, Gerätetyp, Ports, UPnP/NetBIOS, CSV |
| | **WLAN-Scanner** | Live-Spektrum 2,4/5/6 GHz, Kanalbreite bis 160 MHz, Wi-Fi 4–7, WPA2/3, Kanalempfehlung |
| | **WLAN-Ausleuchtung** | Messpunkte pro Raum: Signal, AP, Kanal, Datenrate, Ping zum Gateway, Durchsatz, Bewertung |
| | **LLDP / CDP** | Switch, Port, VLAN, Voice-VLAN, PoE, Management-IP über das Windows-Bordmittel pktmon |
| | **DHCP-Test** | DHCPDISCOVER, alle antwortenden Server mit Optionen, Warnung bei mehreren (Rogue-)Servern |
| | **SNMP-Browser** | v1/v2c: Systeminfo, Ports (Status, Speed, VLAN, Zähler), Live-Traffic je Port, LLDP-Nachbarn, Tonerstände, freier Walk |
| | **Wake-on-LAN**, **Subnetzrechner** | Magic Packet mit Geräteliste; IPv4/IPv6-Rechner mit Subnetting |
| DNS & Domains | **DNS-Checker** | 13 Eintragstypen, System-/öffentlicher/autoritativer Server, TTL, DNSSEC |
| | **Domain-Check** | alles auf einmal: Delegation, Nameserver (autoritativ, Serial, Redundanz), SOA, DNSSEC-Kette, Web/IPv6, Wildcard, Mail-Basis, CAA, Blacklists, Propagation, Gesamtbewertung |
| | **Resolver-Vergleich**, **Whois/RDAP**, **Mail-Check** | wie bisher |
| Web | **SSL/TLS**, **HTTP-Header**, **CMS-Checker** | wie bisher |
| | **Website-Kopierer** | ganze Website offline speichern (ähnlich HTTrack): Ordnerstruktur wie am Webserver (ein Ordner je Host), Links in HTML/CSS werden auf relative lokale Pfade umgeschrieben, Linktiefe, Datei-/Größenlimit, robots.txt, Ausschlussliste; fremde Domains nur nach Rückfrage (Erlauben/Ablehnen je Domain) |

## Bedienung

- **Tabs:** Klick in der Navigation öffnet das Werkzeug (bzw. wechselt zum offenen Tab). **Strg+Klick**, **Mittelklick** oder Rechtsklick → „In neuem Tab öffnen“ startet eine weitere Instanz (z. B. zwei Pings gleichzeitig). Laufende Tests laufen im Hintergrund weiter.
- **Berichte:** oben ein Projekt wählen (oder automatisch anlegen lassen) → in jedem Werkzeug „Zum Bericht“ → unter *Projekte & Berichte* Kundendaten ergänzen und als PDF speichern. Firmendaten und Logo unter *Einstellungen*.
- **Sprache:** *Einstellungen → Sprache* (Systemsprache, Deutsch, English), wirksam nach Neustart.

## Projektstruktur

| Ordner | Inhalt |
|---|---|
| `Core\` (NetStuff.Core, `net10.0`) | plattformunabhängiger Kern: Dienste (DNS, SNMP, Speedtest, iPerf3, Whois, Website-Kopierer …), Parser (LLDP/CDP, DHCP, WLAN-Beacons), Projekte/Verlauf/Einstellungen, Übersetzungen (`Core\Assets\lang`), HTML-Berichte |
| `Core\Platform\Platform.cs` | Schnittstellen für Plattformfunktionen: Benachrichtigungen, ARP, LLDP-Mitschnitt, Datenordner |
| `CoreTests\` | Selbsttest des Kerns als Konsolenprogramm – läuft auf jedem System: `dotnet run --project CoreTests` (zusätzlich `-- --live` für eine echte Website-Kopie) |
| `Platform\Windows\` | Windows-Umsetzungen: Toasts, SendARP, pktmon, WLAN-API |
| `Pages\`, `Controls\`, `Infrastructure\` | WPF-Oberfläche (nur Windows) |

`NetStuff.sln` enthält beide Projekte. Ziel: dieselbe Core-Bibliothek später für macOS und iPhone/iPad (Avalonia) verwenden.

## Starten / Weitergeben

```powershell
dotnet run
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

`publish\NetStuff.exe` + Ordner `Assets` auf jeden Windows-10/11-PC (x64) kopieren – kein .NET nötig.

Startparameter: `NetStuff.exe --page dns --param example.com --run [--lang en] [--dark|--light]`
Seiten: `dashboard projects history ping trace mtu portscan speed monitor netscan wlan survey lldp dhcp snmp wol subnet dns domaincheck resolver whois mail ssl http cms sitecopy settings`
Entwickler: `--selftest ergebnis.txt` (Parser-, iPerf3- und SNMP-Tests), `--snapshot bild.png --snapdelay 10` (Fensterbild).

## Hinweise

- WLAN-Scan unter Windows 11 benötigt die Standortberechtigung für Desktop-Apps (Hinweis mit Direktlink in der App).
- LLDP/CDP gibt es nur am Kabel; Switches senden alle 30 s (LLDP) bzw. 60 s (CDP).
- DHCP-Test: Beim ersten Mal ggf. die Windows-Firewall-Abfrage bestätigen, sonst kommen die Antworten nicht an.
- Spamhaus beantwortet Blacklist-Abfragen über öffentliche Resolver nicht – das wird als „nicht prüfbar“ angezeigt.
- Externe Dienste: ip-api.com (Geo-IP, nicht-kommerziell kostenlos), ipify.org, speed.cloudflare.com, rdap.org, OpenStreetMap.
- Website-Kopierer: Standardziel `Downloads\Net-Stuff Websites\<host>`, Einstieg über `index.html` im Kopie-Ordner. Per JavaScript nachgeladene Inhalte, Formulare und Suche funktionieren offline nicht. `--selftest datei.txt --copytest` testet zusätzlich eine echte Kopie (books.toscrape.com).
- Daten (Projekte, Verlauf, Favoriten, Überwachung, Einstellungen, Logo): `%LOCALAPPDATA%\Net-Stuff`
- Übersetzungen: `Core/Assets/lang/en.json` (Deutsch → Englisch, Platzhalter `{0}`); weitere Sprachen als zusätzliche Datei möglich.
