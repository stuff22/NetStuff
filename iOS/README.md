# Net-Stuff für iPhone und iPad

Gleiche Oberfläche wie am Mac (`Ui/`), auf schmalen Bildschirmen mit einklappbarer Navigation (☰).
Gebaut wird nur am Mac (Xcode muss installiert sein).

## Einmalig

```bash
sudo dotnet workload install ios
```

## Im Simulator starten

```bash
cd iOS
dotnet run -f net10.0-ios -r iossimulator-arm64
```

Die Build-Ausgabe liegt am Mac in `iOS/bin.nosync` und `iOS/obj.nosync` – Ordner mit `.nosync` gleicht iCloud
nicht ab (sonst scheitert codesign mit „detritus not allowed“, wenn das Projekt auf Schreibtisch/Dokumente liegt).
(Nur `-t:Run` reicht ab .NET 10 nicht mehr – „The app must be built before …“.)

Startet den Standard-Simulator. Ein bestimmtes Gerät: Simulator-App öffnen, Gerät starten, dann denselben Befehl.

## Auf dem eigenen iPhone/iPad

1. iPhone per Kabel anschließen, am iPhone **Einstellungen → Datenschutz & Sicherheit → Entwicklermodus** einschalten.
2. Einmalig ein Bereitstellungsprofil erzeugen lassen: In Xcode ein neues Projekt „App“ anlegen,
   Bundle Identifier `at.itstuff.netstuff`, unter *Signing & Capabilities* das eigene Team (Apple-ID) wählen,
   iPhone als Ziel wählen und einmal starten (▶). Danach kann das Xcode-Projekt gelöscht werden.
3. Installieren:

   ```bash
   cd iOS
   dotnet run -f net10.0-ios -r ios-arm64
   ```

4. Beim ersten Start am iPhone: **Einstellungen → Allgemein → VPN & Geräteverwaltung** → Entwickler-App vertrauen.

Mit einer kostenlosen Apple-ID läuft die App 7 Tage, danach Schritt 3 wiederholen.

## Für den App Store bauen (iPhone + iPad)

```bash
bash iOS/build-appstore.sh
```

Erzeugt `iOS/bin.nosync/appstore/Net-Stuff-iOS-<Version>-<Build>.ipa` (eine Datei für iPhone und iPad), die mit
der App **Transporter** hochgeladen wird. Voraussetzungen stehen oben im Skript: Zertifikat „Apple Distribution“
und ein Provisioning Profile „App Store Connect“ (iOS) als `iOS/NetStuff_AppStore.mobileprovision` (nicht im Repository).
Vor jedem weiteren Upload derselben Version `ApplicationVersion` in `NetStuff.iOS.csproj` erhöhen.

## Einschränkungen unter iOS

- Ausgeblendet: WLAN-Scanner, WLAN-Ausleuchtung, LLDP/CDP (iOS gibt keine WLAN-Liste frei und erlaubt keinen Mitschnitt).
- Netzwerkscanner: keine MAC-Adressen/Hersteller (kein ARP-Zugriff).
- Broadcast/Multicast (Wake-on-LAN an die Broadcast-Adresse, DHCP-Test, UPnP/SSDP) braucht von Apple die
  Berechtigung `com.apple.developer.networking.multicast` – ohne sie können diese Funktionen fehlschlagen.
- Überwachung läuft nur, solange die App im Vordergrund ist.
- PDF-Export: Der Bericht wird über „Teilen“ angeboten (In Dateien sichern, Drucken → als PDF).
