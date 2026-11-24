# Baut das MSIX-Paket für den Microsoft Store (Partner Center).
#   Aufruf im Projektordner:  powershell -ExecutionPolicy Bypass -File Store\build-msix.ps1
#   Ergebnis:                 Net-Stuff-<Version>\Net-Stuff-<Version>.msix  (unsigniert – der Store signiert selbst)
# Die Identitätsdaten stehen in Store\store-identity.json und müssen exakt mit dem Partner Center übereinstimmen
# (App → Produktverwaltung → Produktidentität).
param(
    [string]$Version = "",
    [switch]$Test     # zusätzlich das Paket lokal registrieren (Entwicklermodus nötig) – zum Ausprobieren vor dem Hochladen
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---------- Version und Identität ----------
if (-not $Version) {
    $Version = ([xml](Get-Content "NetStuff.csproj" -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$parts = @($Version.Split('.'))
while ($parts.Count -lt 3) { $parts += "0" }
$msixVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"   # Store: vierte Stelle muss 0 sein
$id = Get-Content (Join-Path $PSScriptRoot "store-identity.json") -Raw -Encoding UTF8 | ConvertFrom-Json
if ($id.IdentityName -like "PLATZHALTER*") {
    Write-Warning "Store\store-identity.json enthält noch Platzhalter – das Paket lässt sich so nicht hochladen."
}

$out = Join-Path $root "Net-Stuff-$Version"
$layout = Join-Path $out "msix-layout"
$msix = Join-Path $out "Net-Stuff-$Version.msix"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

# ---------- makeappx finden oder aus dem NuGet-Paket Microsoft.Windows.SDK.BuildTools holen ----------
function Find-MakeAppx {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $exe = Get-ChildItem $kits -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
        if ($exe) { return $exe.FullName }
    }
    $tools = Join-Path $env:LOCALAPPDATA "NetStuff-build\sdk-buildtools"
    $exe = Get-ChildItem $tools -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if ($exe) { return $exe.FullName }

    Write-Host "Lade Windows-SDK-Werkzeuge (Microsoft.Windows.SDK.BuildTools) ..."
    New-Item -ItemType Directory -Force $tools | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ver = "10.0.28000.2957"
    $zip = Join-Path $tools "buildtools.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$ver/microsoft.windows.sdk.buildtools.$ver.nupkg" -OutFile $zip -UseBasicParsing
    Expand-Archive $zip -DestinationPath $tools -Force
    Remove-Item $zip
    $exe = Get-ChildItem $tools -Recurse -Filter makeappx.exe | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if (-not $exe) { throw "makeappx.exe nicht gefunden." }
    return $exe.FullName
}
$makeappx = Find-MakeAppx

# ---------- App veröffentlichen (Ordner, keine Einzeldatei – so startet sie schneller) ----------
Write-Host "Veröffentliche Net-Stuff $Version ..."
dotnet publish NetStuff.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -p:Version=$Version -o $layout -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen." }
Get-ChildItem $layout -Filter *.pdb -Recurse | Remove-Item

# ---------- Symbole aus Assets\netstuff.png erzeugen ----------
Add-Type -AssemblyName System.Drawing
$img = Join-Path $layout "Images"
New-Item -ItemType Directory -Force $img | Out-Null
$src = [System.Drawing.Image]::FromFile((Join-Path $root "Assets\netstuff.png"))
function Save-Logo([string]$name, [int]$w, [int]$h, [double]$scale) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $size = [int]([Math]::Min($w, $h) * $scale)
    $g.DrawImage($src, [int](($w - $size) / 2), [int](($h - $size) / 2), $size, $size)
    $bmp.Save((Join-Path $img $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}
Save-Logo "StoreLogo.png" 50 50 1.0
Save-Logo "Square44x44Logo.png" 44 44 1.0
Save-Logo "Square44x44Logo.targetsize-44_altform-unplated.png" 44 44 1.0
Save-Logo "Square150x150Logo.png" 150 150 0.7
Save-Logo "Wide310x150Logo.png" 310 150 0.7
$src.Dispose()

# ---------- Paketmanifest ----------
function X([string]$s) { [System.Security.SecurityElement]::Escape($s) }
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="$(X $id.IdentityName)" Publisher="$(X $id.Publisher)" Version="$msixVersion" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>$(X $id.DisplayName)</DisplayName>
    <PublisherDisplayName>$(X $id.PublisherDisplayName)</PublisherDisplayName>
    <Logo>Images\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="de-DE" />
    <Resource Language="en-US" />
  </Resources>
  <Applications>
    <Application Id="NetStuff" Executable="NetStuff.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="$(X $id.DisplayName)" Description="$(X $id.Description)" BackgroundColor="transparent"
                          Square150x150Logo="Images\Square150x150Logo.png" Square44x44Logo="Images\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Images\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <Capability Name="internetClientServer" />
    <Capability Name="privateNetworkClientServer" />
    <rescap:Capability Name="runFullTrust" />
    <DeviceCapability Name="location" />
  </Capabilities>
</Package>
"@
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding $false))

# ---------- Packen ----------
if (Test-Path $msix) { Remove-Item $msix }
& $makeappx pack /d $layout /p $msix /o /h SHA256
if ($LASTEXITCODE -ne 0) { throw "makeappx fehlgeschlagen." }

if ($Test) {
    Write-Host "Registriere das Paket lokal zum Testen (Entwicklermodus nötig) ..."
    Add-AppxPackage -Register (Join-Path $layout "AppxManifest.xml")
    Write-Host "Net-Stuff ist jetzt im Startmenü (Store-Variante). Entfernen: Get-AppxPackage *NetStuff* | Remove-AppxPackage"
}

Write-Host ""
Write-Host "Fertig: $msix"
Write-Host "Version im Paket: $msixVersion"
