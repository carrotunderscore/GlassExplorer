# ModernGlass – mörkt, modernt tema för Windows 10
# Kör:  högerklicka -> "Kör med PowerShell"
# Ändrar bara inställningar för din egen användare (HKCU). Ingen admin krävs.

param([switch]$SkipFilesApp)

$ErrorActionPreference = 'Stop'
$src  = Split-Path -Parent $MyInvocation.MyCommand.Path
$dest = Join-Path $env:LOCALAPPDATA 'ModernGlass'
New-Item -ItemType Directory -Force $dest | Out-Null

# 1. Säkerhetskopiera nuvarande inställningar (för Avinstallera-skriptet)
if (-not (Test-Path "$dest\backup-1.reg")) {
    $keys = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize',
            'HKCU\Software\Microsoft\Windows\DWM',
            'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent'
    $i = 1
    foreach ($k in $keys) {
        $ErrorActionPreference = 'Continue'
        reg export $k "$dest\backup-$i.reg" /y 2>$null | Out-Null
        $ErrorActionPreference = 'Stop'
        $i++
    }
    (Get-ItemProperty 'HKCU:\Control Panel\Desktop').WallPaper | Set-Content "$dest\old-wallpaper.txt"
}

# 2. Kopiera bakgrundsbild
Copy-Item (Join-Path $src 'wallpaper.jpg') (Join-Path $dest 'wallpaper.jpg') -Force
$wall = Join-Path $dest 'wallpaper.jpg'

# 3. Mörkt läge, transparens, blå accent (#4C9BFF – lyser fint mot mörkblått)
$p = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
Set-ItemProperty $p AppsUseLightTheme   0 -Type DWord
Set-ItemProperty $p SystemUsesLightTheme 0 -Type DWord
Set-ItemProperty $p EnableTransparency  1 -Type DWord
Set-ItemProperty $p ColorPrevalence     0 -Type DWord   # mörkt aktivitetsfält/start med glas

$d = 'HKCU:\Software\Microsoft\Windows\DWM'
# ABGR-format: FF FF 9B 4C  => #4C9BFF
Set-ItemProperty $d AccentColor         0xFFFF9B4C -Type DWord
Set-ItemProperty $d ColorizationColor   0xC44C9BFF -Type DWord
Set-ItemProperty $d ColorPrevalence     0 -Type DWord   # mörka fönsterlister

$a = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent'
New-Item $a -Force | Out-Null
Set-ItemProperty $a AccentColorMenu 0xFFFF9B4C -Type DWord
Set-ItemProperty $a StartColorMenu  0xFF6B3A1A -Type DWord

# 4. Skriv .theme-fil så temat syns under Inställningar -> Teman
$theme = @"
[Theme]
DisplayName=ModernGlass Mörk

[Control Panel\Desktop]
Wallpaper=$wall
WallpaperStyle=10
TileWallpaper=0
Pattern=

[VisualStyles]
Path=%SystemRoot%\resources\themes\Aero\Aero.msstyles
ColorStyle=NormalColor
Size=NormalSize
AutoColorization=0
ColorizationColor=0XC44C9BFF
SystemMode=Dark
AppMode=Dark

[MasterThemeSelector]
MTSM=RJSPBS

[Sounds]
SchemeName=@%SystemRoot%\System32\mmres.dll,-800
"@
$themeFile = Join-Path $dest 'ModernGlass.theme'
$theme | Set-Content $themeFile -Encoding Unicode

# 5. Sätt bakgrund direkt
Add-Type @'
using System.Runtime.InteropServices;
public class WP { [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int SystemParametersInfo(int a, int b, string c, int d); }
'@
Set-ItemProperty 'HKCU:\Control Panel\Desktop' WallpaperStyle '10'
Set-ItemProperty 'HKCU:\Control Panel\Desktop' TileWallpaper '0'
[WP]::SystemParametersInfo(20, 0, $wall, 3) | Out-Null

# 6. Starta om Utforskaren så allt slår igenom
Stop-Process -Name explorer -Force
Start-Sleep -Seconds 2
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }

Write-Host "`nModernGlass är installerat." -ForegroundColor Cyan

# 7. Valfritt: "Files" – modern filhanterare med rundade hörn, glas-effekt och flikar
if (-not $SkipFilesApp) {
    $ans = Read-Host "`nVill du installera appen Files (modern ersättare för Utforskaren, gratis/öppen källkod)? (j/n)"
    if ($ans -match '^[jJyY]') {
        winget install --id FilesCommunity.Files -e --source winget --accept-package-agreements
        Write-Host "`nÖppna Files -> Inställningar -> Utseende: välj Mörkt tema och bakgrund 'Acrylic'." -ForegroundColor Cyan
        Write-Host "Under Inställningar -> Avancerat kan du sätta Files som standard-filhanterare." -ForegroundColor Cyan
    }
}
Write-Host "`nTryck Enter för att stänga."; [void][Console]::ReadLine()
