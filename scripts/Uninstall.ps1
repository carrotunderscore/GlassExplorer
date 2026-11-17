# Removes GlassExplorer and makes the normal File Explorer the default again.
# Right-click Uninstall.ps1 -> "Run with PowerShell"

$dest = Join-Path $env:LOCALAPPDATA 'Programs\GlassExplorer'
Get-Process GlassExplorer -ErrorAction SilentlyContinue | Stop-Process -Force

# remove the "default file manager" registration (only our own keys)
foreach ($cls in 'Directory', 'Drive') {
    $k = "HKCU:\Software\Classes\$cls\shell"
    if ((Get-ItemProperty $k -ErrorAction SilentlyContinue).'(default)' -eq 'glassexplorer') {
        Remove-ItemProperty $k -Name '(default)' -ErrorAction SilentlyContinue
    }
    Remove-Item "$k\glassexplorer" -Recurse -Force -ErrorAction SilentlyContinue
}
$winE = 'HKCU:\Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}'
$cmd = (Get-ItemProperty "$winE\shell\opennewwindow\command" -ErrorAction SilentlyContinue).'(default)'
if ($cmd -like '*GlassExplorer*') { Remove-Item $winE -Recurse -Force -ErrorAction SilentlyContinue }

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'ModernGlass Explorer.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'GlassExplorer') -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'GlassExplorer is removed and the normal File Explorer is the default again.'
Write-Host 'Press Enter to close.'; [void][Console]::ReadLine()
