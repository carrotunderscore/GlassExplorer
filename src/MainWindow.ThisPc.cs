using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace GlassExplorer
{
    public class SizeRowItem
    {
        public SizeNode Node { get; set; }
        public string Name { get; set; }
        public string SizeText { get; set; }
        public string ToolTipText { get; set; }
        public Brush Brush { get; set; }
        public GridLength BarStar { get; set; }
        public GridLength RestStar { get; set; }
    }

    public class ScanCrumb
    {
        public string Name { get; set; }
        public SizeNode Node { get; set; }
        public Visibility ChevronVisibility { get; set; }
    }

    // "This PC": drives with free space + a WizTree-style space map of the selected drive.
    public partial class MainWindow
    {
        const string TreemapHint = "Click a block to zoom in · right-click for more";
        readonly Dictionary<string, SizeNode> scans = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> scanInfo = new(StringComparer.OrdinalIgnoreCase);
        string scanDrive, scanningRoot;
        SizeNode scanView;
        CancellationTokenSource scanCts;
        DateTime scanStart;
        long scanUsedBytes;
        DispatcherTimer scanTimer;
        bool pcInit;

        void InitPc()
        {
            if (pcInit) return;
            pcInit = true;
            Treemap.HoverChanged += n => TreemapHover.Text = n == null ? NodeSummary(scanView) : Describe(n);
            Treemap.NodeClicked += (deep, top) => { if (!top.IsPseudo && top.HasChildren) ShowScanNode(top); };
            Treemap.NodeRightClicked += (deep, top) => ScanMenu(deep, Treemap);
            scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            scanTimer.Tick += (s, e) => UpdateScanProgress();
            SmoothScroll.Attach(SizeList);
            Closing += (s, e) => scanCts?.Cancel();
        }

        string Describe(SizeNode n)
        {
            double pct = scanView != null && scanView.Size > 0 ? 100.0 * n.Size / scanView.Size : 0;
            var where = n.IsPseudo ? $"{n.Parent.FullPath} — {n.DisplayName}" : n.FullPath;
            return $"{where}   ·   {Fmt.Size(n.Size)}   ·   {pct:0.0}%";
        }

        string NodeSummary(SizeNode n)
        {
            if (n == null) return TreemapHint;
            var s = $"{Fmt.Size(n.Size)} in {n.FileCountTotal:N0} files";
            if (n.Parent == null && scanInfo.TryGetValue(n.Name, out var info)) s += "   ·   " + info;
            return s + "   ·   " + TreemapHint;
        }

        void ShowThisPc()
        {
            InitPc();
            GridList.Visibility = DetailsHost.Visibility = EmptyPanel.Visibility = Visibility.Collapsed;
            PcHost.Visibility = Visibility.Visible;
            SelectionText.Text = "";

            var items = new List<FileItem>();
            long total = 0, free = 0;
            foreach (var d in SafeDrives())
            {
                double used = (double)(d.TotalSize - d.TotalFreeSpace) / d.TotalSize;
                var dash = new DoubleCollection { Math.Max(0.01, used * Math.PI * 47 / 5), 1000 };
                dash.Freeze();
                total += d.TotalSize; free += d.TotalFreeSpace;
                items.Add(new FileItem
                {
                    Kind = ItemKind.Drive, Name = DriveLabel(d), FullPath = d.RootDirectory.FullName,
                    UsageText = $"{Fmt.Size(d.TotalFreeSpace)} free of {Fmt.Size(d.TotalSize)}",
                    PercentText = $"{used * 100:0}%", RingDash = dash,
                    RingBrush = Fmt.Frozen(used > 0.9 ? "#FF6B6B" : GlassTheme.Current.Accent),
                    UsedStar = new GridLength(used, GridUnitType.Star), FreeStar = new GridLength(1 - used, GridUnitType.Star),
                    ToolTipText = $"{DriveLabel(d)}\nUsed: {Fmt.Size(d.TotalSize - d.TotalFreeSpace)}\nFree: {Fmt.Size(d.TotalFreeSpace)}\nDouble-click to open"
                });
            }
            CountText.Text = items.Count.ToString();
            SubText.Text = $"{Fmt.Size(total)} total · {Fmt.Size(free)} free";

            PcDrives.ItemsSource = items;
            var want = scanDrive ?? Path.GetPathRoot(Environment.SystemDirectory);
            PcDrives.SelectedItem = items.FirstOrDefault(i => string.Equals(i.FullPath, want, StringComparison.OrdinalIgnoreCase)) ?? items.FirstOrDefault();
        }

        void PcDrives_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (PcDrives.SelectedItem is FileItem d) AnalyzeDrive(d.FullPath);
        }

        async void PcDrives_DoubleClick(object s, MouseButtonEventArgs e)
        {
            if (ItemAt(e.OriginalSource)?.DataContext is FileItem d) await Navigate(d.FullPath);
        }

        void AnalyzeDrive(string root)
        {
            scanDrive = root;
            if (scans.TryGetValue(root, out var res)) { ShowScanNode(res); UpdateScanButton(); return; }
            if (string.Equals(scanningRoot, root, StringComparison.OrdinalIgnoreCase)) { ShowScanProgress(); return; }
            StartScan(root);
        }

        async void StartScan(string root)
        {
            if (snapshotPath != null && snapshotPath.Contains("noscan")) return;
            scanCts?.Cancel();
            var cts = scanCts = new CancellationTokenSource();
            scanningRoot = root;
            scanStart = DateTime.Now;
            try { var di = new DriveInfo(root); scanUsedBytes = di.TotalSize - di.TotalFreeSpace; } catch { scanUsedBytes = 0; }
            ShowScanProgress();
            scanTimer.Start();
            UpdateScanButton();

            SizeNode result;
            try
            {
                result = await Task.Run(() => DiskScanner.Scan(root, cts.Token), cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (scanCts == cts)
                {
                    scanningRoot = null;
                    scanTimer.Stop();
                    ScanProgressTitle.Text = "Scan cancelled";
                    ScanProgressText.Text = "Press Scan to start again.";
                    UpdateScanButton();
                }
                return;
            }
            if (scanCts != cts) return;

            scanTimer.Stop();
            scanningRoot = null;
            long unknown = scanUsedBytes - result.Size;
            if (unknown > scanUsedBytes * 0.005) { result.Unknown = unknown; result.Size += unknown; }
            scans[root] = result;
            scanInfo[root] = $"scanned {DiskScanner.FoldersScanned:N0} folders in {(DateTime.Now - scanStart).TotalSeconds:0.0} s";
            if (string.Equals(scanDrive, root, StringComparison.OrdinalIgnoreCase) && current == ThisPC) ShowScanNode(result);
            UpdateScanButton();
        }

        void ShowScanProgress()
        {
            scanView = null;
            Treemap.Root = null;
            SizeList.ItemsSource = null;
            ScanProgressPanel.Visibility = Visibility.Visible;
            ScanProgressTitle.Text = $"Scanning {NameOf(scanningRoot ?? scanDrive)}…";
            ScanCrumbs.ItemsSource = new[] { new ScanCrumb { Name = NameOf(scanDrive), ChevronVisibility = Visibility.Collapsed } };
            ScanUpBtn.IsEnabled = false;
            TreemapHover.Text = "Reading every folder on the drive – large drives can take a minute.";
            UpdateScanProgress();
        }

        void UpdateScanProgress()
        {
            if (scanningRoot == null) return;
            long bytes = Interlocked.Read(ref DiskScanner.BytesScanned);
            double frac = scanUsedBytes > 0 ? Math.Min(0.99, (double)bytes / scanUsedBytes) : 0;
            ScanProgressFill.Width = 340 * frac;
            ScanProgressText.Text =
                $"{Interlocked.Read(ref DiskScanner.FilesScanned):N0} files · {Fmt.Size(bytes)} of ~{Fmt.Size(scanUsedBytes)}\n" +
                $"{(DateTime.Now - scanStart).TotalSeconds:0} s";
        }

        void UpdateScanButton()
        {
            bool scanning = scanningRoot != null && string.Equals(scanningRoot, scanDrive, StringComparison.OrdinalIgnoreCase);
            ScanBtnText.Text = scanning ? "Cancel" : scans.ContainsKey(scanDrive ?? "") ? "Rescan" : "Scan";
            ScanBtnGlyph.Text = scanning ? "" : "";
        }

        void ShowScanNode(SizeNode n)
        {
            scanView = n;
            ScanProgressPanel.Visibility = Visibility.Collapsed;
            Treemap.Root = n;

            var kids = n.Display();
            long max = kids.Count > 0 ? Math.Max(1, kids[0].Size) : 1;
            var rows = new List<SizeRowItem>();
            for (int i = 0; i < kids.Count && i < 80; i++)
            {
                var k = kids[i];
                double pct = n.Size > 0 ? 100.0 * k.Size / n.Size : 0;
                double bar = (double)k.Size / max;
                var brush = new SolidColorBrush(DiskScanner.ColorFor(k, i));
                brush.Freeze();
                rows.Add(new SizeRowItem
                {
                    Node = k, Name = k.DisplayName, Brush = brush,
                    SizeText = $"{Fmt.Size(k.Size)}  ·  {pct:0.0}%",
                    BarStar = new GridLength(bar, GridUnitType.Star), RestStar = new GridLength(1 - bar, GridUnitType.Star),
                    ToolTipText = (k.IsPseudo ? k.DisplayName : k.FullPath) + (k.HasChildren ? "\nClick to zoom in" : "")
                });
            }
            SizeList.ItemsSource = rows;

            var chain = new List<SizeNode>();
            for (var x = n; x != null; x = x.Parent) chain.Insert(0, x);
            ScanCrumbs.ItemsSource = chain.Select((x, i) => new ScanCrumb
            {
                Name = i == 0 ? NameOf(x.Name) : x.Name, Node = x,
                ChevronVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible
            }).ToList();
            ScanUpBtn.IsEnabled = n.Parent != null;
            TreemapHover.Text = NodeSummary(n);
        }

        void Scan_Click(object s, RoutedEventArgs e)
        {
            if (scanDrive == null) return;
            if (scanningRoot != null && string.Equals(scanningRoot, scanDrive, StringComparison.OrdinalIgnoreCase))
            {
                scanCts?.Cancel();
                return;
            }
            scans.Remove(scanDrive);
            StartScan(scanDrive);
        }

        void ScanUp_Click(object s, RoutedEventArgs e)
        {
            if (scanView?.Parent != null) ShowScanNode(scanView.Parent);
        }

        void ScanCrumb_Click(object s, RoutedEventArgs e)
        {
            if ((s as Button)?.Tag is SizeNode n) ShowScanNode(n);
        }

        void SizeList_Click(object s, MouseButtonEventArgs e)
        {
            if (ItemAt(e.OriginalSource)?.DataContext is SizeRowItem r && !r.Node.IsPseudo && r.Node.HasChildren)
                ShowScanNode(r.Node);
        }

        void SizeList_RightClick(object s, MouseButtonEventArgs e)
        {
            if (ItemAt(e.OriginalSource)?.DataContext is SizeRowItem r) { ScanMenu(r.Node, SizeList); e.Handled = true; }
        }

        void ScanMenu(SizeNode n, UIElement target)
        {
            var folder = n.IsPseudo ? n.Parent.FullPath : n.FullPath;
            var m = new ContextMenu();
            if (!n.IsPseudo && n.HasChildren) m.Items.Add(MI("Zoom in", "", () => ShowScanNode(n)));
            m.Items.Add(MI("Open folder", "", async () => await Navigate(folder)));
            m.Items.Add(MI("Open in new tab", "", () => _ = NewTab(folder, activate: false)));
            m.Items.Add(MI("Open in new window", "", () => OpenNewWindow(folder)));
            m.Items.Add(MI("Show in classic Explorer", "", () => System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{folder}\"")));
            m.Items.Add(MI("Copy path", "", () => { try { Clipboard.SetText($"\"{folder}\""); } catch { } }));
            if (!n.IsPseudo && n.Parent != null)
            {
                m.Items.Add(new Separator());
                m.Items.Add(MI("Delete…", "", () =>
                {
                    if (Dialog.Confirm(this, "Delete folder?", $"Move \"{folder}\" ({Fmt.Size(n.Size)}) to the Recycle Bin?", "Delete"))
                        Native.Delete(Hwnd, new[] { folder }, false, () => { });
                }));
            }
            m.PlacementTarget = target;
            m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            m.IsOpen = true;
        }
    }
}
