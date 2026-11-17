# Återställer inställningarna som fanns innan ModernGlass installerades.
$dest = Join-Path $env:LOCALAPPDATA 'ModernGlass'
$files = Get-ChildItem "$dest\backup-*.reg" -ErrorAction SilentlyContinue
if (-not $files) { Write-Host 'Ingen säkerhetskopia hittades.'; [void][Console]::ReadLine(); exit }

# Om Accent-nyckeln inte fanns innan, ta bort den nya
if (-not (Test-Path "$dest\backup-3.reg")) {
    Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent' -Recurse -ErrorAction SilentlyContinue
}
foreach ($f in $files) { reg import $f.FullName 2>$null | Out-Null }

$old = Get-Content "$dest\old-wallpaper.txt" -ErrorAction SilentlyContinue
if ($old) {
    Add-Type @'
using System.Runtime.InteropServices;
public class WP { [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int SystemParametersInfo(int a, int b, string c, int d); }
'@
    [WP]::SystemParametersInfo(20, 0, $old, 3) | Out-Null
}

Stop-Process -Name explorer -Force
Start-Sleep -Seconds 2
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }

Remove-Item $dest -Recurse -Force
Write-Host 'Återställt. (Appen Files, om installerad, avinstalleras via Inställningar -> Appar.)'
[void][Console]::ReadLine()
