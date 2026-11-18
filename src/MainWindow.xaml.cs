using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace GlassExplorer
{
    public partial class MainWindow : Window
    {
        const string Home = "::home";
        const string ThisPC = "::thispc";
        static bool IsVirtual(string p) => p == Home || p == ThisPC;

        readonly Settings settings = Settings.Load();
        readonly IconLoader icons;
        List<string> back = new(), fwd = new();
        readonly DispatcherTimer refreshTimer;
        readonly string snapshotPath;

        string current;
        List<FileItem> allItems = new();
        FileSystemWatcher watcher;
        CancellationTokenSource loadCts;
        bool searchMode;
        bool suppressFilter;
        double snapScanSeconds;

        ListBox ActiveList => DetailsHost.Visibility == Visibility.Visible ? DetailList : GridList;
        IEnumerable<FileItem> Selected => ActiveList.SelectedItems.Cast<FileItem>();
        bool InRealFolder => !IsVirtual(current) && !searchMode;
        IntPtr Hwnd => new WindowInteropHelper(this).Handle;

        public MainWindow(string startPath, string snapshot)
        {
            InitializeComponent();
            snapshotPath = snapshot;
            icons = new IconLoader(Dispatcher);
            TrackWindowState();

            Width = settings.Width; Height = settings.Height;
            if (settings.Maximized && snapshot == null) WindowState = WindowState.Maximized;

            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            refreshTimer.Tick += (s, e) => { refreshTimer.Stop(); _ = Reload(keepState: true); };

            foreach (var lb in new[] { GridList, DetailList })
            {
                lb.MouseDoubleClick += List_DoubleClick;
                lb.PreviewMouseLeftButtonDown += List_PreviewDown;
                lb.PreviewMouseMove += List_PreviewMove;
                lb.PreviewMouseLeftButtonUp += List_PreviewUp;
                lb.PreviewMouseRightButtonDown += List_PreviewRightDown;
                lb.ContextMenuOpening += List_ContextMenuOpening;
                lb.SelectionChanged += (s, e) => UpdateSelectionText();
                lb.DragOver += List_DragOver;
                lb.LostMouseCapture += (s, e) => EndBand();
                lb.PreviewMouseDown += List_MiddleDown;
                lb.Drop += List_Drop;
                SmoothScroll.Attach(lb);
            }

            SourceInitialized += (s, e) =>
            {
                var src = HwndSource.FromHwnd(Hwnd);
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
                Native.EnableBlur(Hwnd);
                src.AddHook(Native.MaximizeToWorkAreaHook);
            };
            StateChanged += (s, e) => UpdateMaxState();
            Closing += (s, e) => SaveWindowState();
            PreviewKeyDown += Window_PreviewKeyDown;
            MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1) GoBack();
                else if (e.ChangedButton == MouseButton.XButton2) GoForward();
            };

            BuildSidebar();
            Loaded += async (s, e) =>
            {
                UpdateMaxState();
                TabsList.ItemsSource = tabs;
                await NewTab(ParseStartPath(startPath) ?? Home);
                if (snapshotPath != null && Environment.GetEnvironmentVariable("GE_TABTEST") is string tt)
                    foreach (var extra in tt.Split('|')) await NewTab(extra, activate: false);
                if (snapshotPath != null && Environment.GetEnvironmentVariable("GE_BANDTEST") != null)
                {
                    await Task.Delay(1200);
                    var sv = GetScroll(ActiveList);
                    BeginBand(ActiveList, new Point(sv.ActualWidth * 0.97, 4), false);
                    UpdateBand(new Point(sv.ActualWidth * 0.45, sv.ActualHeight * 0.55));
                    UpdateSelectionText();
                }
                if (snapshotPath != null)
                {
                    await Task.Delay(2500);
                    var t0 = DateTime.Now;
                    while (scanningRoot != null && (DateTime.Now - t0).TotalSeconds < 300) await Task.Delay(500);
                    if (current == ThisPC) { snapScanSeconds = (DateTime.Now - t0).TotalSeconds; await Task.Delay(800); }
                    SaveSnapshot(snapshotPath);
                    Close();
                }
            };
        }

        static string ParseStartPath(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = p.Trim().Trim('"');
            if (p.Length == 2 && p[1] == ':') p += "\\";
            return p;
        }

        // =====================================================================
        // Navigering
        // =====================================================================
        async Task Navigate(string path, bool addHistory = true)
        {
            if (path.Trim().Equals("this pc", StringComparison.OrdinalIgnoreCase)) path = ThisPC;
            if (path.Trim().Equals("home", StringComparison.OrdinalIgnoreCase)) path = Home;
            if (!IsVirtual(path))
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || path.StartsWith("::"))
                {
                    TryStart("explorer.exe", path);
                    return;
                }
                if (File.Exists(path) && !Directory.Exists(path))
                {
                    var parent = Path.GetDirectoryName(path);
                    await Navigate(parent, addHistory);
                    SelectPaths(new[] { path });
                    return;
                }
                try { path = Path.GetFullPath(path); } catch { }
                if (!Directory.Exists(path))
                {
                    Dialog.Info(this, "Folder not found", $"\"{path}\" doesn't exist or can't be reached.");
                    return;
                }
                if (path.Length > 3) path = path.TrimEnd('\\');
            }

            if (addHistory && current != null && !string.Equals(current, path, StringComparison.OrdinalIgnoreCase))
            {
                back.Add(current);
                fwd.Clear();
            }
            var previous = current;
            current = path;
            searchMode = false;
            suppressFilter = true;
            SearchBox.Text = "";
            suppressFilter = false;

            if (!await Reload(keepState: false))
            {
                // Ingen åtkomst – gå tillbaka
                current = previous;
                if (addHistory && back.Count > 0) back.RemoveAt(back.Count - 1);
                if (previous != null) await Reload(keepState: false);
                return;
            }
            SetupWatcher();
            UpdateChrome();
            if (!IsVirtual(current)) RecordRecent(current, true);
        }

        async void GoBack()
        {
            if (back.Count == 0) return;
            var p = back[^1]; back.RemoveAt(back.Count - 1);
            fwd.Add(current);
            await Navigate(p, addHistory: false);
        }

        async void GoForward()
        {
            if (fwd.Count == 0) return;
            var p = fwd[^1]; fwd.RemoveAt(fwd.Count - 1);
            back.Add(current);
            await Navigate(p, addHistory: false);
        }

        async void GoUp()
        {
            if (current == Home) return;
            if (current == ThisPC) { await Navigate(Home); return; }
            if (searchMode) { await Navigate(current, false); return; }
            var parent = Directory.GetParent(current);
            await Navigate(parent?.FullName ?? ThisPC);
        }

        void Back_Click(object s, RoutedEventArgs e) => GoBack();
        void Forward_Click(object s, RoutedEventArgs e) => GoForward();
        void Up_Click(object s, RoutedEventArgs e) => GoUp();
        async void Refresh_Click(object s, RoutedEventArgs e) { RefreshDrives(); await Reload(true); }
        async void Crumb_Click(object s, RoutedEventArgs e) { if ((s as Button)?.Tag is string p) await Navigate(p); }

        /// Läser in mappen (eller hemvyn) på en bakgrundstråd.
        async Task<bool> Reload(bool keepState)
        {
            if (current == null) return false;
            loadCts?.Cancel();
            var cts = loadCts = new CancellationTokenSource();
            int gen = icons.NewGeneration();

            var keepSel = keepState ? Selected.Select(i => i.FullPath).ToList() : null;
            double offset = keepState ? GetScroll(ActiveList)?.VerticalOffset ?? 0 : 0;
            var oldIcons = keepState ? allItems.Where(i => i.Icon != null).GroupBy(i => i.FullPath).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) : null;

            if (current == ThisPC)
            {
                allItems = new List<FileItem>();
                ShowThisPc();
                return true;
            }
            PcHost.Visibility = Visibility.Collapsed;

            List<FileItem> items;
            string path = current;
            var recentSnap = settings.Recent.ToList();
            bool hidden = settings.ShowHidden;
            try
            {
                items = await Task.Run(() => path == Home ? BuildHome(recentSnap) : ReadFolder(path, hidden, cts.Token), cts.Token);
            }
            catch (OperationCanceledException) { return false; }
            catch (UnauthorizedAccessException)
            {
                Dialog.Info(this, "Access denied", $"You don't have permission to open \"{path}\".");
                return false;
            }
            catch (Exception ex)
            {
                Dialog.Info(this, "Couldn't open folder", ex.Message);
                return false;
            }
            if (cts.IsCancellationRequested) return false;

            if (oldIcons != null)
                foreach (var it in items)
                    if (oldIcons.TryGetValue(it.FullPath, out var o) && o.Modified == it.Modified) it.Icon = o.Icon;

            allItems = items;
            ApplyView();
            icons.Enqueue(items, gen);

            if (keepSel != null && keepSel.Count > 0) SelectPaths(keepSel, scroll: false);
            if (offset > 0)
                _ = Dispatcher.BeginInvoke(() => GetScroll(ActiveList)?.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
            return true;
        }

        static List<FileItem> ReadFolder(string path, bool showHidden, CancellationToken ct)
        {
            var dir = new DirectoryInfo(path);
            // Kasta UnauthorizedAccess tidigt om vi inte kan lista mappen
            using (var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) e.MoveNext();

            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true, RecurseSubdirectories = false,
                AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
            };
            var list = new List<FileItem>();
            foreach (var fi in dir.EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(MakeItem(fi, null));
            }
            return list;
        }

        static readonly Dictionary<string, string> typeCache = new(StringComparer.OrdinalIgnoreCase);
        static string TypeOf(string ext)
        {
            lock (typeCache)
            {
                if (!typeCache.TryGetValue(ext, out var t)) typeCache[ext] = t = Native.TypeName(ext);
                return t;
            }
        }

        static FileItem MakeItem(FileSystemInfo fi, string group)
        {
            if (fi is DirectoryInfo d)
            {
                var date = Fmt.Date(d.LastWriteTime);
                return new FileItem
                {
                    Kind = ItemKind.Folder, Name = d.Name, FullPath = d.FullName, Modified = d.LastWriteTime,
                    Group = group ?? "Folders", TypeText = "File folder", ModifiedText = date, SizeText = "",
                    Subtitle = "Modified " + date,
                    ToolTipText = $"{d.Name}\nModified: {d.LastWriteTime:g}"
                };
            }
            var f = (FileInfo)fi;
            var type = TypeOf(f.Extension);
            return new FileItem
            {
                Kind = ItemKind.File, Name = f.Name, FullPath = f.FullName, Modified = f.LastWriteTime, Size = f.Length,
                Group = group ?? "Files", TypeText = type, SizeText = Fmt.Size(f.Length), ModifiedText = Fmt.Date(f.LastWriteTime),
                Subtitle = Fmt.Size(f.Length),
                ToolTipText = $"{f.Name}\nType: {type}\nSize: {Fmt.Size(f.Length)}\nModified: {f.LastWriteTime:g}"
            };
        }

        List<FileItem> BuildHome(List<RecentItem> recent)
        {
            var list = new List<FileItem>();
            var homeRecent = new List<FileItem>();
            foreach (var p in places.Skip(1).Select(x => x.Path).Concat(settings.Pinned).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(p)) continue;
                var it = MakeItem(new DirectoryInfo(p), "Quick access");
                it.Name = places.FirstOrDefault(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))?.Name ?? NameOf(p);
                list.Add(it);
            }
            foreach (var r in recent)
            {
                if (homeRecent.Count >= 10) break;
                try
                {
                    if (r.IsFolder && Directory.Exists(r.Path)) homeRecent.Add(MakeItem(new DirectoryInfo(r.Path), "Recent"));
                    else if (!r.IsFolder && File.Exists(r.Path)) homeRecent.Add(MakeItem(new FileInfo(r.Path), "Recent"));
                }
                catch { }
            }
            foreach (var it in homeRecent) it.Subtitle = ShortParent(it.FullPath);
            list.AddRange(homeRecent);
            foreach (var d in SafeDrives())
            {
                double used = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                var dash = new DoubleCollection { Math.Max(0.01, used * Math.PI * 45 / 5), 1000 };
                dash.Freeze();
                list.Add(new FileItem
                {
                    Kind = ItemKind.Drive, Name = DriveLabel(d), FullPath = d.RootDirectory.FullName, Group = "Drives",
                    UsageText = $"{Fmt.Size(d.TotalFreeSpace)} free of {Fmt.Size(d.TotalSize)}",
                    PercentText = $"{used * 100:0}%", RingDash = dash,
                    RingBrush = Fmt.Frozen(used > 0.9 ? "#FF6B6B" : GlassTheme.Current.Accent),
                    TypeText = "Drive", SizeText = Fmt.Size(d.TotalSize), ModifiedText = "",
                    ToolTipText = $"{DriveLabel(d)}\n{Fmt.Size(d.TotalFreeSpace)} free of {Fmt.Size(d.TotalSize)}"
                });
            }
            return list;
        }

        static string NameOf(string path)
        {
            if (path == Home) return "Home";
            if (path == ThisPC) return "This PC";
            if (path.Length <= 3 && path.EndsWith(":\\"))
                try { return DriveLabel(new DriveInfo(path)); } catch { }
            try { return Native.DisplayName(path); } catch { return Path.GetFileName(path.TrimEnd('\\')); }
        }

        // =====================================================================
        // Vy, sortering, filtrering
        // =====================================================================
        void ApplyView()
        {
            if (current == ThisPC)
            {
                GridList.Visibility = DetailsHost.Visibility = EmptyPanel.Visibility = Visibility.Collapsed;
                return;
            }
            IEnumerable<FileItem> q = allItems;
            var filter = SearchBox.Text.Trim();
            if (!searchMode && filter.Length > 0)
                q = q.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            if (current != Home) q = Sort(q);
            var list = q.ToList();

            bool grid = settings.GridView && list.Count <= 2500;
            if (grid)
            {
                var view = new ListCollectionView(list);
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FileItem.Group)));
                GridList.ItemsSource = view;
                DetailList.ItemsSource = null;
            }
            else
            {
                DetailList.ItemsSource = list;
                GridList.ItemsSource = null;
            }
            GridList.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
            DetailsHost.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
            ViewBtn.Content = settings.GridView ? "\uE8FD" : "\uF0E2";

            EmptyPanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = filter.Length > 0 || searchMode ? "No results" : "This folder is empty";

            int folders = list.Count(i => i.Kind != ItemKind.File), files = list.Count - folders;
            CountText.Text = list.Count.ToString();
            SubText.Text = current == Home
                ? $"{list.Count(i => i.Group == "Quick access")} places · {list.Count(i => i.Kind == ItemKind.Drive)} drives"
                : string.Join(" · ", new[]
                  {
                      folders > 0 ? $"{folders} {(folders == 1 ? "folder" : "folders")}" : null,
                      files > 0 ? $"{files} {(files == 1 ? "file" : "files")}" : null
                  }.Where(x => x != null));
            UpdateSelectionText();
        }

        IEnumerable<FileItem> Sort(IEnumerable<FileItem> q)
        {
            Comparison<FileItem> cmp = settings.SortKey switch
            {
                "date" => (a, b) => a.Modified.CompareTo(b.Modified),
                "size" => (a, b) => a.Size.CompareTo(b.Size),
                "type" => (a, b) => string.Compare(a.TypeText, b.TypeText, StringComparison.CurrentCultureIgnoreCase),
                _ => (a, b) => Native.StrCmpLogicalW(a.Name, b.Name)
            };
            var list = q.ToList();
            list.Sort((a, b) =>
            {
                if (a.IsContainer != b.IsContainer) return a.IsContainer ? -1 : 1;
                int r = cmp(a, b);
                if (r == 0 && settings.SortKey != "name") r = Native.StrCmpLogicalW(a.Name, b.Name);
                return settings.SortDesc ? -r : r;
            });
            return list;
        }

        void UpdateChrome()
        {
            string title = searchMode ? $"Search: \"{SearchBox.Text.Trim()}\"" : NameOf(current);
            TitleText.Text = title;
            Title = (searchMode ? title : NameOf(current)) + " – Explorer";
            SearchBox.Tag = IsVirtual(current) ? "Search" : $"Search {NameOf(current)}";

            var crumbs = new List<Crumb>();
            if (IsVirtual(current)) crumbs.Add(new Crumb { Name = NameOf(current), Path = current, ChevronVisibility = Visibility.Collapsed });
            else
            {
                crumbs.Add(new Crumb { Name = "This PC", Path = ThisPC, ChevronVisibility = Visibility.Collapsed });
                var parts = new List<string>();
                for (var d = new DirectoryInfo(current); d != null; d = d.Parent) parts.Insert(0, d.FullName);
                if (parts.Count > 5)
                {
                    crumbs.Add(new Crumb { Name = NameOf(parts[0]), Path = parts[0], ChevronVisibility = Visibility.Visible });
                    crumbs.Add(new Crumb { Name = "…", Path = parts[^4], ChevronVisibility = Visibility.Visible });
                    parts = parts.Skip(parts.Count - 3).ToList();
                }
                foreach (var p in parts)
                    crumbs.Add(new Crumb
                    {
                        Name = p.Length <= 3 ? NameOf(p) : Path.GetFileName(p),
                        Path = p, ChevronVisibility = Visibility.Visible
                    });
            }
            Crumbs.ItemsSource = crumbs;
            _ = Dispatcher.BeginInvoke(() => CrumbScroll.ScrollToRightEnd(), DispatcherPriority.Loaded);

            BackBtn.IsEnabled = back.Count > 0;
            FwdBtn.IsEnabled = fwd.Count > 0;
            UpBtn.IsEnabled = current != Home;
            UpdateActiveSide();
            UpdateTabInfo(title);
        }

        void UpdateSelectionText()
        {
            var sel = Selected.ToList();
            if (sel.Count == 0) { SelectionText.Text = ""; return; }
            var files = sel.Where(i => i.Kind == ItemKind.File).ToList();
            var text = sel.Count == 1 ? "1 selected" : $"{sel.Count} selected";
            if (files.Count > 0) text += " · " + Fmt.Size(files.Sum(f => f.Size));
            SelectionText.Text = text;
        }

        async void View_Click(object s, RoutedEventArgs e)
        {
            settings.GridView = !settings.GridView;
            settings.Save();
            var sel = Selected.Select(i => i.FullPath).ToList();
            ApplyView();
            SelectPaths(sel);
            await Task.CompletedTask;
        }

        void Sort_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            void Opt(string key, string label, string glyph) =>
                m.Items.Add(MI(label, glyph, () => SetSort(key, settings.SortDesc), check: settings.SortKey == key));
            Opt("name", "Name", "\uE8C1");
            Opt("date", "Date modified", "\uE787");
            Opt("type", "Type", "\uE7C3");
            Opt("size", "Size", "\uE9D2");
            m.Items.Add(new Separator());
            m.Items.Add(MI("Ascending", "\uE74A", () => SetSort(settings.SortKey, false), check: !settings.SortDesc));
            m.Items.Add(MI("Descending", "\uE74B", () => SetSort(settings.SortKey, true), check: settings.SortDesc));
            ShowMenu(m, (UIElement)s);
        }

        void Header_Click(object s, RoutedEventArgs e)
        {
            var key = (string)((Button)s).Tag;
            SetSort(key, settings.SortKey == key ? !settings.SortDesc : false);
        }

        void SetSort(string key, bool desc)
        {
            settings.SortKey = key; settings.SortDesc = desc; settings.Save();
            var sel = Selected.Select(i => i.FullPath).ToList();
            ApplyView();
            SelectPaths(sel);
        }

        void More_Click(object s, RoutedEventArgs e)
        {
            var m = new ContextMenu();
            m.Items.Add(MI("Show hidden files", "\uE7B3", ToggleHidden, check: settings.ShowHidden));
            m.Items.Add(OpacityItem());
            m.Items.Add(MI("Default file manager (replace Explorer)", "\uE8B7", ToggleDefault, check: DefaultHandler.IsEnabled()));
            m.Items.Add(new Separator());
            m.Items.Add(MI("New tab", "\uE710", () => _ = NewTab(Home), "Ctrl+T"));
            m.Items.Add(MI("New window", "\uE737", () => OpenNewWindow(IsVirtual(current) ? null : current), "Ctrl+N"));
            m.Items.Add(MI("Open terminal here", "\uE756", () => OpenTerminal(InRealFolder ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), enabled: true));
            m.Items.Add(MI("Open in classic Explorer", "\uE8DA", () => OpenClassic(InRealFolder ? current : null)));
            m.Items.Add(new Separator());
            m.Items.Add(MI("About", "\uE946", () => Dialog.Info(this, "ModernGlass Explorer",
                "A modern dark-glass file manager for Windows 10.\n\nShortcuts: Ctrl+C/X/V, F2 rename, Delete, Ctrl+Shift+N new folder, " +
                "Alt+Left/Right/Up, Ctrl+L address bar, Ctrl+F search (Enter searches subfolders), F5 refresh.")));
            ShowMenu(m, (UIElement)s);
        }

        MenuItem OpacityItem()
        {
            var pct = new TextBlock { Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                                      Foreground = (Brush)FindResource("TextSecondary"), Text = $"{settings.Opacity * 100:0}%" };
            var slider = new Slider { Minimum = 0.3, Maximum = 1, Value = System.Math.Clamp(settings.Opacity, 0.3, 1), Width = 160,
                                      Margin = new Thickness(12, 0, 8, 0), Style = (Style)FindResource("GlassSlider"), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (s, e) =>
            {
                double v = System.Math.Round(slider.Value, 2);
                pct.Text = $"{v * 100:0}%";
                App.WindowOpacity = v;
                foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.settings.Opacity = v;
                ((App)Application.Current).ApplyTheme();
            };
            slider.LostMouseCapture += (s, e) => settings.Save();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "Opacity", VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(slider);
            row.Children.Add(pct);
            return new MenuItem { Header = row, Tag = "\uE790", StaysOpenOnClick = true, ToolTip = "How see-through the window is (the bar has its own)" };
        }

        async void ToggleHidden()
        {
            settings.ShowHidden = !settings.ShowHidden;
            settings.Save();
            await Reload(true);
        }

        void ToggleDefault()
        {
            try
            {
                if (DefaultHandler.IsEnabled())
                {
                    DefaultHandler.Disable();
                    Dialog.Info(this, "Explorer is back", "Folders and Win+E now open in the regular File Explorer again.");
                    return;
                }
                var exe = Environment.ProcessPath;
                if (!Dialog.Confirm(this, "Replace File Explorer?",
                        "Opening a folder or drive, or pressing Win+E, will open this app instead of File Explorer.\n\n" +
                        $"The app runs from:\n{exe}\nDon't move it afterwards (or turn this on again if you do).\n\n" +
                        "Only affects your account and can be turned off here at any time.", "Replace")) return;
                DefaultHandler.Enable(exe);
                Dialog.Info(this, "Done", "ModernGlass Explorer is now your default file manager. Try Win+E!");
            }
            catch (Exception ex)
            {
                Dialog.Info(this, "Couldn't change setting", ex.Message);
            }
        }

        // =====================================================================
        // Sök
        // =====================================================================
        void SearchBox_TextChanged(object s, TextChangedEventArgs e)
        {
            if (suppressFilter || searchMode) return;
            ApplyView();
        }

        async void SearchBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (searchMode) await Navigate(current, false);
                else { SearchBox.Text = ""; ActiveList.Focus(); }
            }
            else if (e.Key == Key.Enter && InRealFolder && SearchBox.Text.Trim().Length > 0)
            {
                e.Handled = true;
                await DeepSearch(SearchBox.Text.Trim());
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                FocusFirstItem();
            }
        }

        async Task DeepSearch(string term)
        {
            loadCts?.Cancel();
            var cts = loadCts = new CancellationTokenSource();
            int gen = icons.NewGeneration();
            string root = current;
            searchMode = true;
            TitleText.Text = $"Searching for \"{term}\"…";
            SubText.Text = "";
            CountText.Text = "…";

            var results = await Task.Run(() =>
            {
                var list = new List<FileItem>();
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true, RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive,
                    AttributesToSkip = settings.ShowHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
                };
                try
                {
                    foreach (var fi in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + term + "*", opts))
                    {
                        if (cts.IsCancellationRequested || list.Count >= 2000) break;
                        var it = MakeItem(fi, null);
                        var parent = Path.GetDirectoryName(fi.FullName);
                        it.Subtitle = Path.GetRelativePath(root, parent) is var rel && rel != "." ? rel : NameOf(root);
                        if (it.Kind == ItemKind.File) it.SizeText = it.Subtitle.Length > 22 ? "…" + it.Subtitle[^21..] : it.Subtitle;
                        list.Add(it);
                    }
                }
                catch { }
                return list;
            });
            if (cts.IsCancellationRequested) return;
            allItems = results;
            ApplyView();
            icons.Enqueue(results, gen);
            UpdateChrome();
            if (results.Count >= 2000) SubText.Text += " · showing first 2000";
        }

        // =====================================================================
        // Adressfält
        // =====================================================================
        void Address_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (FindAncestor<Button>(e.OriginalSource) != null) return;
            BeginAddressEdit();
            e.Handled = true;
        }

        void BeginAddressEdit()
        {
            Crumbs.Visibility = Visibility.Collapsed;
            AddressBox.Visibility = Visibility.Visible;
            AddressBox.Text = IsVirtual(current) ? NameOf(current) : current;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }

        void EndAddressEdit()
        {
            AddressBox.Visibility = Visibility.Collapsed;
            Crumbs.Visibility = Visibility.Visible;
        }

        async void AddressBox_KeyDown(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { EndAddressEdit(); ActiveList.Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = AddressBox.Text;
                EndAddressEdit();
                ActiveList.Focus();
                if (!string.IsNullOrWhiteSpace(text)) await Navigate(text);
            }
        }

        void AddressBox_LostFocus(object s, KeyboardFocusChangedEventArgs e) => EndAddressEdit();

        // =====================================================================
        // Lista: klick, dubbelklick, dra & släpp
        // =====================================================================
        Point dragStart;
        FileItem pressedItem;
        bool deferSelect;

        static ListBoxItem ItemAt(object src) => FindAncestor<ListBoxItem>(src);

        static T FindAncestor<T>(object o) where T : DependencyObject
        {
            var d = o as DependencyObject;
            while (d != null && d is not T)
                d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        void List_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (ItemAt(e.OriginalSource)?.DataContext is FileItem it) OpenItems(new[] { it });
        }

        void List_PreviewDown(object s, MouseButtonEventArgs e)
        {
            var lb = (ListBox)s;
            var container = ItemAt(e.OriginalSource);
            if (container == null)
            {
                pressedItem = null;
                if (FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(e.OriginalSource) == null && GetScroll(lb) is ScrollViewer sv)
                {
                    lb.Focus();
                    BeginBand(lb, e.GetPosition(sv), (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                    lb.CaptureMouse();
                    e.Handled = true;
                }
                return;
            }
            dragStart = e.GetPosition(this);
            pressedItem = container.DataContext as FileItem;
            deferSelect = e.ClickCount == 1 && container.IsSelected && lb.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None;
            if (deferSelect) { e.Handled = true; container.Focus(); }
        }

        void List_PreviewMove(object s, MouseEventArgs e)
        {
            if (banding)
            {
                if (e.LeftButton != MouseButtonState.Pressed) EndBand();
                else UpdateBand(e.GetPosition(bandSv));
                return;
            }
            if (pressedItem == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            var lb = (ListBox)s;
            var items = lb.SelectedItems.Contains(pressedItem) ? lb.SelectedItems.Cast<FileItem>().ToList() : new List<FileItem> { pressedItem };
            var paths = items.Where(i => i.Kind != ItemKind.Drive).Select(i => i.FullPath).ToArray();
            pressedItem = null;
            deferSelect = false;
            if (paths.Length == 0) return;
            try
            {
                DragDrop.DoDragDrop(lb, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch { }
        }

        void List_PreviewUp(object s, MouseButtonEventArgs e)
        {
            if (banding) { EndBand(); e.Handled = true; return; }
            if (deferSelect && pressedItem != null)
            {
                var lb = (ListBox)s;
                lb.SelectedItems.Clear();
                lb.SelectedItem = pressedItem;
            }
            deferSelect = false;
            pressedItem = null;
        }

        void List_PreviewRightDown(object s, MouseButtonEventArgs e)
        {
            var lb = (ListBox)s;
            var c = ItemAt(e.OriginalSource);
            if (c == null) lb.UnselectAll();
            else if (!c.IsSelected) { lb.SelectedItems.Clear(); c.IsSelected = true; }
            c?.Focus();
        }

        string DropTarget(DragEventArgs e)
        {
            if (ItemAt(e.OriginalSource)?.DataContext is FileItem it && it.IsContainer) return it.FullPath;
            return InRealFolder ? current : null;
        }

        static DragDropEffects ChooseEffect(DragEventArgs e, string[] src, string target)
        {
            string t = target.TrimEnd('\\');
            if (src.Any(p => string.Equals(p.TrimEnd('\\'), t, StringComparison.OrdinalIgnoreCase))) return DragDropEffects.None;
            bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            bool shift = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;
            DragDropEffects want;
            if (ctrl) want = DragDropEffects.Copy;
            else if (shift) want = DragDropEffects.Move;
            else
            {
                bool sameFolder = src.All(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\'))?.TrimEnd('\\'), t, StringComparison.OrdinalIgnoreCase));
                if (sameFolder) return DragDropEffects.None;
                bool sameDrive = string.Equals(Path.GetPathRoot(src[0]), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);
                want = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
            }
            return (e.AllowedEffects & want) != 0 ? want : (e.AllowedEffects & DragDropEffects.Copy);
        }

        void List_DragOver(object s, DragEventArgs e)
        {
            e.Effects = DragDropEffects.None;
            if (e.Data.GetDataPresent(DataFormats.FileDrop) && DropTarget(e) is string target && e.Data.GetData(DataFormats.FileDrop) is string[] src && src.Length > 0)
                e.Effects = ChooseEffect(e, src, target);
            e.Handled = true;
        }

        void List_Drop(object s, DragEventArgs e)
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || DropTarget(e) is not string target) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] src || src.Length == 0) return;
            var eff = ChooseEffect(e, src, target);
            if (eff == DragDropEffects.None) return;
            Native.Transfer(Hwnd, src, target, eff == DragDropEffects.Move, ScheduleRefresh);
        }

        void FocusFirstItem()
        {
            var lb = ActiveList;
            if (lb.Items.Count == 0) return;
            if (lb.SelectedItem == null) lb.SelectedIndex = 0;
            (lb.ItemContainerGenerator.ContainerFromItem(lb.SelectedItem) as ListBoxItem)?.Focus();
        }

        void SelectPaths(IEnumerable<string> paths, bool scroll = true)
        {
            var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var lb = ActiveList;
            lb.SelectedItems.Clear();
            FileItem first = null;
            foreach (FileItem it in lb.Items)
                if (set.Contains(it.FullPath)) { lb.SelectedItems.Add(it); first ??= it; }
            if (first != null && scroll)
                Dispatcher.BeginInvoke(() =>
                {
                    lb.ScrollIntoView(first);
                    (lb.ItemContainerGenerator.ContainerFromItem(first) as ListBoxItem)?.Focus();
                }, DispatcherPriority.Loaded);
        }

        static ScrollViewer GetScroll(DependencyObject d)
        {
            if (d is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                if (GetScroll(VisualTreeHelper.GetChild(d, i)) is ScrollViewer r) return r;
            return null;
        }

        // =====================================================================
        // Öppna
        // =====================================================================
        async void OpenItems(IEnumerable<FileItem> items)
        {
            var list = items.ToList();
            var folder = list.FirstOrDefault(i => i.IsContainer);
            foreach (var f in list.Where(i => i.Kind == ItemKind.File))
            {
                if (Path.GetExtension(f.FullPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase) && list.Count == 1)
                {
                    var target = ResolveShortcut(f.FullPath);
                    if (target != null && Directory.Exists(target)) { await Navigate(target); return; }
                }
                TryStart(f.FullPath, null, Path.GetDirectoryName(f.FullPath));
                RecordRecent(f.FullPath, false);
            }
            if (folder != null) await Navigate(folder.FullPath);
        }

        static string ResolveShortcut(string lnk)
        {
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic sh = Activator.CreateInstance(t);
                string target = sh.CreateShortcut(lnk).TargetPath;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
        }

        void TryStart(string file, string args = null, string workDir = null)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                if (workDir != null) psi.WorkingDirectory = workDir;
                Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1155)
            {
                Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {file}");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* avbrutet av användaren */ }
            catch (Exception ex)
            {
                Dialog.Info(this, "Couldn't open", ex.Message);
            }
        }

        void OpenNewWindow(string path)
        {
            var w = new MainWindow(path ?? Home, null);
            if (WindowState == WindowState.Normal) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = Left + 40; w.Top = Top + 40; }
            w.Show();
        }

        void OpenTerminal(string dir)
        {
            try { Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{dir.TrimEnd('\\')}\\.\"") { UseShellExecute = true }); }
            catch { TryStart("powershell.exe", "-NoExit", dir); }
        }

        void OpenClassic(string path)
        {
            if (path == null) Process.Start("explorer.exe", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}");
            else if (Directory.Exists(path) && !Selected.Any()) Native.ShellVerb(path, "explore", Hwnd);
            else Process.Start("explorer.exe", $"/select,\"{path}\"");
        }

        // =====================================================================
        // Filoperationer
        // =====================================================================
        void ScheduleRefresh()
        {
            if (IsVirtual(current)) return;
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        void SetupWatcher()
        {
            watcher?.Dispose();
            watcher = null;
            if (IsVirtual(current)) return;
            try
            {
                watcher = new FileSystemWatcher(current)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                FileSystemEventHandler h = (s, e) => Dispatcher.BeginInvoke(() => { if (!searchMode) ScheduleRefresh(); });
                watcher.Created += h; watcher.Deleted += h; watcher.Changed += h;
                watcher.Renamed += (s, e) => h(s, e);
                watcher.EnableRaisingEvents = true;
            }
            catch { watcher = null; }
        }

        void CopySelection(bool cut)
        {
            var paths = Selected.Where(i => i.Kind != ItemKind.Drive).Select(i => i.FullPath).ToArray();
            if (paths.Length == 0) return;
            var sc = new StringCollection();
            sc.AddRange(paths);
            var data = new DataObject();
            data.SetFileDropList(sc);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetDataObject(data, true); return; }
                catch { Thread.Sleep(60); }
            }
        }

        static bool ClipboardHasFiles()
        {
            try { return Clipboard.ContainsFileDropList(); } catch { return false; }
        }

        void Paste(string target = null)
        {
            target ??= InRealFolder ? current : null;
            if (target == null || !ClipboardHasFiles()) return;
            string[] files;
            bool move = false;
            try
            {
                files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    move = (BitConverter.ToInt32(b, 0) & 2) != 0;
                }
            }
            catch { return; }
            if (files.Length == 0) return;
            Native.Transfer(Hwnd, files, target, move, ScheduleRefresh);
            if (move) try { Clipboard.Clear(); } catch { }
        }

        void DeleteSelection(bool permanent)
        {
            var paths = Selected.Where(i => i.Kind != ItemKind.Drive).Select(i => i.FullPath).ToArray();
            if (paths.Length == 0) return;
            Native.Delete(Hwnd, paths, permanent, ScheduleRefresh);
        }

        void RenameSelection()
        {
            var it = Selected.FirstOrDefault();
            if (it == null || it.Kind == ItemKind.Drive) return;
            int stem = it.Kind == ItemKind.File && Path.GetExtension(it.Name).Length > 0 ? Path.GetFileNameWithoutExtension(it.Name).Length : it.Name.Length;
            var name = Dialog.Prompt(this, "Rename", null, it.Name, stem);
            if (string.IsNullOrWhiteSpace(name) || name == it.Name) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                Dialog.Info(this, "Invalid name", "A file name can't contain any of the following characters:\n\\ / : * ? \" < > |");
                return;
            }
            var to = Path.Combine(Path.GetDirectoryName(it.FullPath), name.Trim());
            if (Native.Rename(Hwnd, it.FullPath, to))
            {
                allItems.Remove(it);
                _ = Reload(true).ContinueWith(_ => Dispatcher.BeginInvoke(() => SelectPaths(new[] { to })));
            }
        }

        async void NewFolder()
        {
            if (!InRealFolder) return;
            var baseName = "New folder";
            var name = baseName;
            for (int i = 2; Directory.Exists(Path.Combine(current, name)) || File.Exists(Path.Combine(current, name)); i++) name = $"{baseName} ({i})";
            name = Dialog.Prompt(this, "New folder", "Name the new folder:", name);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var path = Path.Combine(current, name.Trim());
                Directory.CreateDirectory(path);
                await Reload(true);
                SelectPaths(new[] { path });
            }
            catch (Exception ex) { Dialog.Info(this, "Couldn't create folder", ex.Message); }
        }

        void NewFolder_Click(object s, RoutedEventArgs e) => NewFolder();

        void CopyPaths()
        {
            var text = string.Join(Environment.NewLine, Selected.Select(i => $"\"{i.FullPath}\""));
            if (text.Length == 0 && InRealFolder) text = $"\"{current}\"";
            try { Clipboard.SetText(text); } catch { }
        }

        // =====================================================================
        // Snabbmeny
        // =====================================================================
        static MenuItem MI(string header, string glyph, Action a, string gesture = null, bool enabled = true, bool? check = null)
        {
            var m = new MenuItem { Header = header, Tag = glyph, InputGestureText = gesture ?? "", IsEnabled = enabled };
            if (check == true) m.IsChecked = true;
            m.Click += (s, e) => a();
            return m;
        }

        static void ShowMenu(ContextMenu m, UIElement target)
        {
            m.PlacementTarget = target;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            m.IsOpen = true;
        }

        void List_ContextMenuOpening(object s, ContextMenuEventArgs e)
        {
            e.Handled = true;
            var sel = Selected.ToList();
            var m = new ContextMenu();

            if (sel.Count == 0)
            {
                if (InRealFolder)
                {
                    m.Items.Add(MI("New folder", "\uE8F4", NewFolder, "Ctrl+Shift+N"));
                    m.Items.Add(MI("Paste", "\uE77F", () => Paste(), "Ctrl+V", ClipboardHasFiles()));
                    m.Items.Add(new Separator());
                }
                m.Items.Add(MI(settings.GridView ? "List view" : "Card view", settings.GridView ? "\uE8FD" : "\uF0E2", () => View_Click(null, null)));
                m.Items.Add(MI("Show hidden files", "\uE7B3", ToggleHidden, check: settings.ShowHidden));
                m.Items.Add(MI("Refresh", "\uE72C", () => Refresh_Click(null, null), "F5"));
                if (InRealFolder)
                {
                    m.Items.Add(new Separator());
                    m.Items.Add(MI("Copy path", "\uE71B", CopyPaths));
                    m.Items.Add(MI("Open terminal here", "\uE756", () => OpenTerminal(current)));
                    m.Items.Add(MI("Open in classic Explorer", "\uE8DA", () => OpenClassic(current)));
                    m.Items.Add(MI("Properties", "\uE946", () => Native.ShellVerb(current, "properties", Hwnd)));
                }
            }
            else
            {
                var one = sel.Count == 1 ? sel[0] : null;
                bool anyDrive = sel.Any(i => i.Kind == ItemKind.Drive);
                m.Items.Add(MI("Open", "\uE8E5", () => OpenItems(sel), "Enter"));
                if (one != null && one.IsContainer)
                {
                    m.Items.Add(MI("Open in new tab", "\uE8A7", () => _ = NewTab(one.FullPath, activate: false)));
                    m.Items.Add(MI("Open in new window", "\uE737", () => OpenNewWindow(one.FullPath)));
                    bool isPinned = settings.Pinned.Any(p => string.Equals(p, one.FullPath, StringComparison.OrdinalIgnoreCase));
                    m.Items.Add(MI(isPinned ? "Unpin from sidebar" : "Pin to sidebar", isPinned ? "\uE77A" : "\uE718", () => TogglePin(one.FullPath)));
                }
                if (one != null && one.Kind == ItemKind.File)
                    m.Items.Add(MI("Open with…", "\uE7AC", () => Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {one.FullPath}")));
                if (!anyDrive)
                {
                    m.Items.Add(new Separator());
                    m.Items.Add(MI("Cut", "\uE8C6", () => CopySelection(true), "Ctrl+X"));
                    m.Items.Add(MI("Copy", "\uE8C8", () => CopySelection(false), "Ctrl+C"));
                    if (one != null && one.Kind == ItemKind.Folder && ClipboardHasFiles())
                        m.Items.Add(MI("Paste into folder", "\uE77F", () => Paste(one.FullPath)));
                    m.Items.Add(MI("Copy path", "\uE71B", CopyPaths));
                    m.Items.Add(new Separator());
                    m.Items.Add(MI("Rename", "\uE8AC", RenameSelection, "F2", one != null));
                    m.Items.Add(MI("Delete", "\uE74D", () => DeleteSelection(false), "Delete"));
                }
                m.Items.Add(new Separator());
                if (one != null && one.IsContainer) m.Items.Add(MI("Open terminal here", "\uE756", () => OpenTerminal(one.FullPath)));
                if (one != null) m.Items.Add(MI("Show in classic Explorer", "\uE8DA", () => OpenClassic(one.FullPath)));
                m.Items.Add(MI("Properties", "\uE946", () => Native.ShellVerb(sel[0].FullPath, "properties", Hwnd), "Alt+Enter"));
            }
            m.PlacementTarget = (UIElement)s;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            m.IsOpen = true;
        }

        // =====================================================================
        // Tangentbord
        // =====================================================================
        void Window_PreviewKeyDown(object s, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            bool inText = Keyboard.FocusedElement is TextBox;

            if (mods == ModifierKeys.Alt)
            {
                if (key == Key.Left) { GoBack(); e.Handled = true; }
                else if (key == Key.Right) { GoForward(); e.Handled = true; }
                else if (key == Key.Up) { GoUp(); e.Handled = true; }
                else if (key == Key.Enter && Selected.Any()) { Native.ShellVerb(Selected.First().FullPath, "properties", Hwnd); e.Handled = true; }
                else if (key == Key.D) { BeginAddressEdit(); e.Handled = true; }
                return;
            }
            if (mods == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.L: BeginAddressEdit(); e.Handled = true; return;
                    case Key.F: case Key.E: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return;
                    case Key.N: OpenNewWindow(IsVirtual(current) ? null : current); e.Handled = true; return;
                    case Key.W: CloseTab(activeTab); e.Handled = true; return;
                    case Key.T: _ = NewTab(Home); e.Handled = true; return;
                    case Key.Tab: CycleTab(1); e.Handled = true; return;
                    case >= Key.D1 and <= Key.D9: SelectTabNumber(key - Key.D1); e.Handled = true; return;
                }
                if (inText) return;
                switch (key)
                {
                    case Key.C: CopySelection(false); e.Handled = true; return;
                    case Key.X: CopySelection(true); e.Handled = true; return;
                    case Key.V: Paste(); e.Handled = true; return;
                }
                return;
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N) { NewFolder(); e.Handled = true; return; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab) { CycleTab(-1); e.Handled = true; return; }

            switch (key)
            {
                case Key.F5: Refresh_Click(null, null); e.Handled = true; return;
                case Key.F3: SearchBox.Focus(); e.Handled = true; return;
                case Key.F4: BeginAddressEdit(); e.Handled = true; return;
            }
            if (inText) return;
            switch (key)
            {
                case Key.Enter: if (Selected.Any()) OpenItems(Selected); e.Handled = true; break;
                case Key.Back: GoBack(); e.Handled = true; break;
                case Key.F2: RenameSelection(); e.Handled = true; break;
                case Key.Delete: DeleteSelection(mods == ModifierKeys.Shift); e.Handled = true; break;
            }
        }

        // =====================================================================
        // Fönster
        // =====================================================================
        void Drag_MouseDown(object s, MouseButtonEventArgs e)
        {
            if (e.OriginalSource != s && FindAncestor<Button>(e.OriginalSource) != null) return;
            if (e.ClickCount == 2) { Max_Click(null, null); return; }
            try { DragMove(); } catch { }
        }

        void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object s, RoutedEventArgs e) => Close();

        void UpdateMaxState()
        {
            bool max = WindowState == WindowState.Maximized;
            RootBorder.Margin = new Thickness(0);
            RootBorder.CornerRadius = new CornerRadius(max ? 0 : 16);
            Sidebar.CornerRadius = new CornerRadius(max ? 0 : 15, 0, 0, max ? 0 : 15);
            MaxBtn.Content = max ? "\uE923" : "\uE922";
            MaxBtn.ToolTip = max ? "Restore" : "Maximize";
        }

        void SaveWindowState()
        {
            if (snapshotPath != null) return;
            settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { settings.Width = Width; settings.Height = Height; }
            settings.Save();
            watcher?.Dispose();
        }

        /// Används bara för att ta en skärmbild av gränssnittet vid utveckling (--snapshot fil.png).
        void SaveSnapshot(string file)
        {
            double w = RootBorder.ActualWidth, h = RootBorder.ActualHeight;
            var dpi = VisualTreeHelper.GetDpi(this);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(16, 28, 54), Color.FromRgb(34, 56, 94), 90), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(RootBorder), null, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(file);
            enc.Save(fs);
        }
    }
}
