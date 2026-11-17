using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GlassExplorer
{
    // Sidebar: places, pinned, recent folders, This PC + drives, and expandable "recent inside" lists.
    public partial class MainWindow
    {
        readonly List<SideEntry> places = new(), pinned = new(), recentEntries = new(), thisPcEntries = new(), drives = new();
        static readonly Brush FolderBrush = Fmt.Frozen("#F5C451");
        static readonly Brush FileBrush = Fmt.Frozen("#93A3BF");
        static readonly StringComparer IC = StringComparer.OrdinalIgnoreCase;

        void BuildSidebar()
        {
            void Add(string glyph, string color, string name, string path) =>
                places.Add(new SideEntry
                {
                    Glyph = glyph, GlyphBrush = Fmt.Frozen(color), Name = name, Path = path, ToolTipText = path == Home ? "Home" : path,
                    CanExpand = path != Home
                });

            Add("", "#4C9BFF", "Home", Home);
            Add("", "#4FD1C5", "Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add("", "#5BD68A", "Downloads", Native.DownloadsFolder());
            Add("", "#6FA8FF", "Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add("", "#B48CFF", "Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            Add("", "#FF7EB6", "Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
            Add("", "#FFA552", "Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
            Add("", "#93A3BF", "User folder", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach (var p in places) RestoreExpanded(p);
            if (snapshotPath != null && snapshotPath.Contains("expand"))
                foreach (var p in places.Where(p => p.CanExpand).Take(3)) { p.IsExpanded = true; FillChildren(p); }
            PlacesList.ItemsSource = places;

            thisPcEntries.Add(new SideEntry { Glyph = "", GlyphBrush = Fmt.Frozen("#8FB8FF"), Name = "This PC", Path = ThisPC, ToolTipText = "Drives and disk usage" });
            ThisPcList.ItemsSource = thisPcEntries;

            RefreshPinned();
            RefreshRecent();
            RefreshDrives();
        }

        SideEntry FolderEntry(string path, string glyph, Brush brush, bool pinnedEntry, bool recent)
        {
            var e = new SideEntry
            {
                Glyph = glyph, GlyphBrush = brush, Name = NameOf(path), Path = path, ToolTipText = path,
                IsPinned = pinnedEntry, IsRecent = recent, CanPin = true, CanExpand = true
            };
            RestoreExpanded(e);
            return e;
        }

        void RestoreExpanded(SideEntry e)
        {
            if (e.CanExpand && settings.Expanded.Contains(e.Path, IC))
            {
                e.IsExpanded = true;
                FillChildren(e);
            }
        }

        void RefreshPinned()
        {
            pinned.Clear();
            foreach (var p in settings.Pinned.Where(Directory.Exists))
                pinned.Add(FolderEntry(p, "", FolderBrush, true, false));
            PinnedList.ItemsSource = null;
            PinnedList.ItemsSource = pinned;
            PinnedHeader.Visibility = pinned.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActiveSide();
        }

        void RefreshRecent()
        {
            var exclude = new HashSet<string>(places.Select(p => p.Path).Concat(settings.Pinned), IC);
            recentEntries.Clear();
            foreach (var r in settings.Recent)
            {
                if (recentEntries.Count >= 5) break;
                if (!r.IsFolder || r.Path.Length <= 3 || exclude.Contains(r.Path) || !Directory.Exists(r.Path)) continue;
                recentEntries.Add(FolderEntry(r.Path, "", Fmt.Frozen("#8FB8FF"), false, true));
            }
            RecentList.ItemsSource = null;
            RecentList.ItemsSource = recentEntries;
            RecentHeader.Visibility = recentEntries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActiveSide();
        }

        void RefreshDrives()
        {
            drives.Clear();
            foreach (var d in SafeDrives())
            {
                double used = d.TotalSize > 0 ? (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize : 0;
                drives.Add(new SideEntry
                {
                    Name = DriveLabel(d), Path = d.RootDirectory.FullName,
                    ToolTipText = $"{Fmt.Size(d.TotalFreeSpace)} free of {Fmt.Size(d.TotalSize)}",
                    UsedStar = new GridLength(used, GridUnitType.Star), FreeStar = new GridLength(1 - used, GridUnitType.Star),
                    BarBrush = Fmt.Frozen(used > 0.9 ? "#FF6B6B" : "#4C9BFF")
                });
            }
            DrivesList.ItemsSource = null;
            DrivesList.ItemsSource = drives;
            UpdateActiveSide();
        }

        static IEnumerable<DriveInfo> SafeDrives()
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                bool ok;
                try { ok = d.IsReady && d.TotalSize > 0; } catch { ok = false; }
                if (ok) yield return d;
            }
        }

        static string DriveLabel(DriveInfo d)
        {
            string label;
            try { label = d.VolumeLabel; } catch { label = ""; }
            if (string.IsNullOrWhiteSpace(label)) label = d.DriveType == DriveType.Removable ? "USB Drive" : d.DriveType == DriveType.Network ? "Network Drive" : "Local Disk";
            return $"{label} ({d.Name.TrimEnd('\\')})";
        }

        static string ShortParent(string path)
        {
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            return parent == null ? path : "in " + (parent.Length <= 3 ? parent : Path.GetFileName(parent));
        }

        IEnumerable<SideEntry> AllSideEntries()
        {
            foreach (var e in places.Concat(pinned).Concat(recentEntries))
            {
                yield return e;
                foreach (var c in e.Children) yield return c;
            }
            foreach (var e in thisPcEntries.Concat(drives)) yield return e;
        }

        void UpdateActiveSide()
        {
            foreach (var e in AllSideEntries())
                e.IsActive = current != null && !searchMode && e.Path != null && !e.IsFile &&
                             string.Equals(e.Path.TrimEnd('\\'), current.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // ---------------- recent tracking ----------------
        void RecordRecent(string path, bool isFolder)
        {
            if (snapshotPath != null) return;
            settings.Recent.RemoveAll(r => IC.Equals(r.Path, path));
            settings.Recent.Insert(0, new RecentItem { Path = path, IsFolder = isFolder, Time = DateTime.Now });
            if (settings.Recent.Count > 300) settings.Recent.RemoveRange(300, settings.Recent.Count - 300);
            settings.Save();
            RefreshRecent();
            foreach (var e in places.Concat(pinned).Where(e => e.IsExpanded)) FillChildren(e);
            UpdateActiveSide();
        }

        /// The 5 most recently opened folders/files inside an entry; topped up with the most recently changed items.
        async void FillChildren(SideEntry e)
        {
            if (e.Path == null || IsVirtual(e.Path)) return;
            string root = e.Path;
            var recent = settings.Recent.ToList();
            var found = await Task.Run(() =>
            {
                var res = new List<(string path, bool isFile)>();
                var seen = new HashSet<string>(IC);
                string prefix = root.TrimEnd('\\') + "\\";
                foreach (var r in recent)
                {
                    if (res.Count >= 5) break;
                    if (!r.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (r.IsFolder ? !Directory.Exists(r.Path) : !File.Exists(r.Path)) continue;
                    if (seen.Add(r.Path)) res.Add((r.Path, !r.IsFolder));
                }
                if (res.Count < 5)
                {
                    try
                    {
                        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                        foreach (var fi in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts).OrderByDescending(f => f.LastWriteTime).Take(12))
                        {
                            if (res.Count >= 5) break;
                            if (seen.Add(fi.FullName)) res.Add((fi.FullName, fi is FileInfo));
                        }
                    }
                    catch { }
                }
                return res;
            });

            e.Children.Clear();
            foreach (var (path, isFile) in found)
                e.Children.Add(new SideEntry
                {
                    Name = isFile && (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path), Path = path, IsFile = isFile,
                    Glyph = isFile ? "" : "", GlyphBrush = isFile ? FileBrush : FolderBrush,
                    ToolTipText = Path.GetRelativePath(root, path), CanPin = !isFile
                });
            if (e.Children.Count == 0)
                e.Children.Add(new SideEntry { Name = "Nothing here yet", IsPlaceholder = true, Glyph = "", GlyphBrush = FileBrush });
            UpdateActiveSide();
        }

        // ---------------- events ----------------
        async void Side_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SideEntry s || s.IsPlaceholder || s.Path == null) return;
            if (s.IsFile)
            {
                TryStart(s.Path, null, Path.GetDirectoryName(s.Path));
                RecordRecent(s.Path, false);
            }
            else await Navigate(s.Path);
        }

        void SideExpand_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is not SideEntry s) return;
            s.IsExpanded = !s.IsExpanded;
            settings.Expanded.RemoveAll(p => IC.Equals(p, s.Path));
            if (s.IsExpanded) { settings.Expanded.Add(s.Path); FillChildren(s); }
            settings.Save();
        }

        void SidePin_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.DataContext is SideEntry s && s.Path != null) TogglePin(s.Path);
        }

        void Side_RightClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not SideEntry s || s.IsPlaceholder || s.Path == null || IsVirtual(s.Path)) return;
            var m = new ContextMenu();
            m.Items.Add(MI("Open", "", () => Side_Click(sender, null)));
            if (!s.IsFile)
            {
                m.Items.Add(MI("Open in new tab", "", () => _ = NewTab(s.Path, activate: false)));
                m.Items.Add(MI("Open in new window", "", () => OpenNewWindow(s.Path)));
                bool isPinned = settings.Pinned.Contains(s.Path, IC);
                m.Items.Add(MI(isPinned ? "Unpin from sidebar" : "Pin to sidebar", isPinned ? "" : "", () => TogglePin(s.Path)));
            }
            if (settings.Recent.Any(r => IC.Equals(r.Path, s.Path)))
                m.Items.Add(MI("Remove from recent", "", () => RemoveRecent(s.Path)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("Copy path", "", () => { try { Clipboard.SetText($"\"{s.Path}\""); } catch { } }));
            m.Items.Add(MI("Show in classic Explorer", "", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{s.Path}\"")));
            m.Items.Add(MI("Properties", "", () => Native.ShellVerb(s.Path, "properties", Hwnd)));
            m.PlacementTarget = (UIElement)sender;
            m.IsOpen = true;
            e.Handled = true;
        }

        void RemoveRecent(string path)
        {
            settings.Recent.RemoveAll(r => IC.Equals(r.Path, path));
            settings.Save();
            RefreshRecent();
            foreach (var e in places.Concat(pinned).Where(e => e.IsExpanded)) FillChildren(e);
        }

        void TogglePin(string path)
        {
            var hit = settings.Pinned.FirstOrDefault(p => IC.Equals(p, path));
            if (hit != null) settings.Pinned.Remove(hit); else settings.Pinned.Add(path);
            settings.Save();
            RefreshPinned();
            RefreshRecent();
            if (current == Home) _ = Reload(true);
        }
    }
}
