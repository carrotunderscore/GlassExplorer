# GlassExplorer

A dark, glassy file manager for Windows 10, built with WPF (.NET 8). Windows 10's own File Explorer
can't get rounded corners or a glass look, so this replaces it, with tabs, blur and a disk-usage map.

Part of the **ModernGlass** set, together with [GlassBar](https://github.com/carrotunderscore/GlassBar)
and the **ModernGlass theme** in the [`theme`](theme) folder of this repo.

## Install

1. Download `GlassExplorer-v1.0.0-win-x64.zip` from [Releases](https://github.com/carrotunderscore/GlassExplorer/releases/latest) and unzip it.
2. Right-click **`Install.ps1`** and choose **Run with PowerShell**.
   - Copies the app to `%LOCALAPPDATA%\Programs\GlassExplorer` (no admin needed)
   - Installs the .NET 8 Desktop Runtime with `winget` if it's missing
   - Adds **ModernGlass Explorer** to the Start menu
3. **Optional: make it the default.** In the app, click **⋯ (More) → Default file manager**.
   Folders, drives and **Win+E** then open in GlassExplorer. Click the same option again to go back to File Explorer.

> If PowerShell refuses to run the script, run
> `powershell -ExecutionPolicy Bypass -File .\Install.ps1` from the unzipped folder.

**Uninstall:** run `Uninstall.ps1` from the same zip. It also makes the normal File Explorer the default again.

## Features

- Dark glass window with blurred background and rounded corners
- **Tabs:** Ctrl+T new tab, Ctrl+W close, Ctrl+Tab / Ctrl+Shift+Tab switch, middle-click a folder to open it in a new tab.
  Folders opened from other programs arrive as a new tab in the open window.
- Card view and list view, image thumbnails, smooth scrolling
- **Rubber-band selection:** click and drag in an empty area to select several files. Ctrl/Shift+click works too.
- Pinned folders in the sidebar
- **Search** in the current folder; press Enter to search sub-folders too
- Drag and drop, plus copy/cut/paste that works together with the normal File Explorer
- Rename, Recycle Bin, Properties, *Open terminal here*
- **This PC** with drive usage and a **disk-space treemap**. Click a block to zoom in, right-click for more.

### Keyboard shortcuts

| Keys | Action |
| --- | --- |
| Ctrl+C / Ctrl+X / Ctrl+V | Copy / cut / paste |
| F2 | Rename |
| Delete / Shift+Delete | Recycle Bin / delete permanently |
| Ctrl+Shift+N | New folder |
| Alt+Left / Alt+Right / Alt+Up, Backspace | Back / forward / up |
| Ctrl+L | Edit the address |
| Ctrl+F | Search |
| F5 | Refresh |
| Ctrl+N | New window |
| Ctrl+T / Ctrl+W / Ctrl+Tab | New tab / close tab / next tab |
| Ctrl+1 … Ctrl+9 | Jump to tab 1–9 |

Some places still open in the normal File Explorer: the desktop, the Recycle Bin and other special shell folders.

## ModernGlass theme (optional)

The [`theme`](theme) folder is a dark-blue Windows 10 theme that the apps are designed for. It's also attached to the
release as `ModernGlass-theme.zip`.

- **Install:** right-click `Installera-ModernGlass.ps1` → *Run with PowerShell*. The script messages are in Swedish.
  - Turns on dark mode for Windows and apps, transparency and the blue accent colour `#4C9BFF`
  - Sets the included 4K wallpaper and saves the theme as *ModernGlass Mörk*
  - Only your own user settings change, and they're backed up first. No admin needed.
- **Undo:** run `Avinstallera-ModernGlass.ps1`.

## Build from source

Requires the .NET 8 SDK on Windows.

```powershell
cd src
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ..\publish
```

## Requirements

- Windows 10 or 11, 64-bit
- .NET 8 Desktop Runtime (the installer adds it)
