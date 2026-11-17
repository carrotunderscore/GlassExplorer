# Installs GlassExplorer for the current user (no admin needed).
# Run from the unzipped release folder:  right-click Install.ps1 -> "Run with PowerShell"

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'GlassExplorer.exe'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GlassExplorer'
$exe  = Join-Path $dest 'GlassExplorer.exe'

if (-not (Test-Path $src)) { throw "GlassExplorer.exe not found next to this script." }

# .NET 8 Desktop Runtime is required
$hasRuntime = $false
try { $hasRuntime = [bool](& dotnet --list-runtimes 2>$null | Select-String 'Microsoft.WindowsDesktop.App 8\.') } catch { }
if (-not $hasRuntime) {
    Write-Host 'Installing .NET 8 Desktop Runtime...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --source winget --accept-package-agreements --accept-source-agreements
}

Get-Process GlassExplorer -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src $exe -Force

# Start menu shortcut
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'ModernGlass Explorer.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.Description = 'Dark glass file manager'
$s.Save()

Write-Host "`nInstalled in $dest" -ForegroundColor Cyan
Write-Host "Find it in the Start menu as 'ModernGlass Explorer'." -ForegroundColor Cyan
Write-Host "Tip: in the app, click ... (More) -> 'Default file manager' to open folders and Win+E in it." -ForegroundColor Cyan

$ans = Read-Host "`nStart it now? (y/n)"
if ($ans -match '^[yYjJ]') { Start-Process $exe }
