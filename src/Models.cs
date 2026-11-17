using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassExplorer
{
    public enum ItemKind { Drive, Folder, File }

    public class FileItem : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public ItemKind Kind { get; set; }
        public string Group { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string TypeText { get; set; }
        public string Subtitle { get; set; }
        public string SizeText { get; set; }
        public string ModifiedText { get; set; }
        public string ToolTipText { get; set; }
        // Enheter
        public string UsageText { get; set; }
        public string PercentText { get; set; }
        public DoubleCollection RingDash { get; set; }
        public Brush RingBrush { get; set; }
        public GridLength UsedStar { get; set; }
        public GridLength FreeStar { get; set; }

        ImageSource icon;
        public ImageSource Icon
        {
            get => icon;
            set { icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
        }
        public bool IsContainer => Kind != ItemKind.File;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class SideEntry : INotifyPropertyChanged
    {
        public string Glyph { get; set; }
        public Brush GlyphBrush { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string ToolTipText { get; set; }
        public bool IsPinned { get; set; }
        public bool IsRecent { get; set; }
        public bool IsFile { get; set; }
        public bool IsPlaceholder { get; set; }
        public bool CanPin { get; set; }
        public bool CanExpand { get; set; }
        public GridLength UsedStar { get; set; }
        public GridLength FreeStar { get; set; }
        public Brush BarBrush { get; set; }

        public string PinGlyph => IsPinned ? "" : "";
        public string PinTip => IsPinned ? "Unpin" : "Pin to sidebar";
        public Visibility PinVisibility => CanPin ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ExpandVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;
        public System.Collections.ObjectModel.ObservableCollection<SideEntry> Children { get; } = new();

        bool expanded;
        public bool IsExpanded
        {
            get => expanded;
            set
            {
                if (expanded == value) return;
                expanded = value;
                Notify(nameof(IsExpanded)); Notify(nameof(ChildrenVisibility)); Notify(nameof(ChevronGlyph));
            }
        }
        public Visibility ChildrenVisibility => expanded ? Visibility.Visible : Visibility.Collapsed;
        public string ChevronGlyph => expanded ? "" : "";

        bool active;
        public bool IsActive
        {
            get => active;
            set { if (active == value) return; active = value; Notify(nameof(IsActive)); }
        }
        void Notify(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class RecentItem
    {
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public DateTime Time { get; set; }
    }

    public class Crumb
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public Visibility ChevronVisibility { get; set; }
    }

    public class CardSelector : DataTemplateSelector
    {
        public DataTemplate Folder { get; set; }
        public DataTemplate File { get; set; }
        public DataTemplate Drive { get; set; }
        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is FileItem f ? f.Kind switch { ItemKind.Drive => Drive, ItemKind.Folder => Folder, _ => File } : null;
    }

    static class Fmt
    {
        public static readonly CultureInfo Sv = new CultureInfo("en-US");

        public static string Size(long b)
        {
            string[] u = { "bytes", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < 4) { v /= 1024; i++; }
            return i == 0 ? (b == 1 ? "1 byte" : $"{b} bytes") : v.ToString(v < 10 ? "0.0" : "0", Sv) + " " + u[i];
        }

        public static string Date(DateTime d)
        {
            var t = DateTime.Today;
            if (d.Date == t) return "Today " + d.ToString("HH:mm", Sv);
            if (d.Date == t.AddDays(-1)) return "Yesterday " + d.ToString("HH:mm", Sv);
            return d.ToString("d MMM yyyy", Sv);
        }

        public static Brush Frozen(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }

    public class Settings
    {
        public bool GridView { get; set; } = true;
        public bool ShowHidden { get; set; }
        public string SortKey { get; set; } = "name";
        public bool SortDesc { get; set; }
        public List<string> Pinned { get; set; } = new List<string>();
        public List<RecentItem> Recent { get; set; } = new List<RecentItem>();
        public List<string> Expanded { get; set; } = new List<string>();
        public double Width { get; set; } = 1180;
        public double Height { get; set; } = 740;
        public bool Maximized { get; set; }

        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassExplorer", "settings.json");

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); }
            catch { return new Settings(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// Hämtar ikoner/miniatyrer på en egen tråd så att gränssnittet aldrig fryser.
    class IconLoader
    {
        readonly BlockingCollection<(FileItem item, int gen)> queue = new();
        readonly ConcurrentDictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
        readonly Dispatcher ui;
        int gen;

        static readonly HashSet<string> Unique = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".lnk", ".ico", ".url", ".msi", ".appref-ms", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
            ".tif", ".tiff", ".heic", ".jfif", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".psd", ".pdf", ".cur"
        };

        public IconLoader(Dispatcher ui)
        {
            this.ui = ui;
            var t = new Thread(Run) { IsBackground = true, Name = "IconLoader" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        public int NewGeneration() => Interlocked.Increment(ref gen);

        public void Enqueue(IEnumerable<FileItem> items, int g)
        {
            foreach (var i in items) if (i.Icon == null) queue.Add((i, g));
        }

        void Run()
        {
            foreach (var (item, g) in queue.GetConsumingEnumerable())
            {
                if (g != Volatile.Read(ref gen)) continue;
                var img = Load(item);
                if (img != null) ui.BeginInvoke(() => item.Icon = img, DispatcherPriority.Background);
            }
        }

        ImageSource Load(FileItem it)
        {
            string key = null;
            if (it.Kind == ItemKind.File)
            {
                var ext = Path.GetExtension(it.FullPath);
                if (!Unique.Contains(ext)) key = "ext:" + ext;
            }
            else if (it.Kind == ItemKind.Folder)
            {
                try { if (!File.Exists(Path.Combine(it.FullPath, "desktop.ini"))) key = "folder"; } catch { }
            }
            if (key != null && cache.TryGetValue(key, out var c)) return c;
            var img = Native.GetImage(it.FullPath, it.Kind == ItemKind.File ? 128 : 64, it.Kind != ItemKind.File);
            if (img != null && key != null) cache[key] = img;
            return img;
        }
    }

    /// Gör appen till standard för att öppna mappar och enheter, samt Win+E (bara för din användare).
    static class DefaultHandler
    {
        const string Verb = "glassexplorer";
        const string WinE = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}";

        public static bool IsEnabled()
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell");
            return string.Equals(k?.GetValue("") as string, Verb, StringComparison.OrdinalIgnoreCase);
        }

        public static void Enable(string exe)
        {
            foreach (var cls in new[] { "Directory", "Drive" })
            {
                using var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell");
                shell.SetValue("", Verb);
                using var v = shell.CreateSubKey(Verb);
                v.SetValue("", "Open in ModernGlass Explorer");
                v.SetValue("Icon", exe);
                using var c = v.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" \"%1\"");
            }
            using var w = Registry.CurrentUser.CreateSubKey(WinE + @"\shell\opennewwindow\command");
            w.SetValue("", $"\"{exe}\"");
            w.SetValue("DelegateExecute", "");
        }

        public static void Disable()
        {
            foreach (var cls in new[] { "Directory", "Drive" })
            {
                using var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell", true);
                if (shell == null) continue;
                if (string.Equals(shell.GetValue("") as string, Verb, StringComparison.OrdinalIgnoreCase)) shell.DeleteValue("", false);
                shell.DeleteSubKeyTree(Verb, false);
            }
            using (var cmd = Registry.CurrentUser.OpenSubKey(WinE + @"\shell\opennewwindow\command"))
            {
                if (cmd == null || !((cmd.GetValue("") as string) ?? "").Contains("GlassExplorer", StringComparison.OrdinalIgnoreCase)) return;
            }
            Registry.CurrentUser.DeleteSubKeyTree(WinE + @"\shell\opennewwindow", false);
            DeleteIfEmpty(WinE + @"\shell");
            DeleteIfEmpty(WinE);
        }

        static void DeleteIfEmpty(string path)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(path))
                if (k == null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            Registry.CurrentUser.DeleteSubKey(path, false);
        }
    }
}
