using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace GlassExplorer
{
    /// Colour themes shared by GlassBar and ModernGlass Explorer.
    /// GlassBar writes %LOCALAPPDATA%\ModernGlass\theme.json; Explorer reads it and follows changes live.
    /// (Same file in both projects, only the namespace differs.)
    public static class GlassTheme
    {
        public record Preset(string Name, string Accent, string Accent2, string Tint);

        public static readonly Preset[] Presets =
        {
            new("Blue",     "#3D8BFF", "#1F5FE0", "#0B1E4A"),
            new("Violet",   "#9D6BFF", "#6E3FE6", "#1F1050"),
            new("Teal",     "#1FD1C1", "#0E9E92", "#06343A"),
            new("Green",    "#3DDC7A", "#1FA554", "#08331C"),
            new("Amber",    "#FFB020", "#E08600", "#3B2604"),
            new("Red",      "#FF5A5A", "#D93030", "#3D0B0E"),
            new("Rose",     "#FF5C93", "#E03070", "#3F0C26"),
            new("Graphite", "#B8C4D6", "#8592A6", "#1E2228"),
        };

        public static Preset Current { get; private set; } = Presets[0];
        public static double Opacity { get; private set; } = 0.89;

        public static Color Accent => Hex(Current.Accent);
        public static Color Accent2 => Hex(Current.Accent2);
        public static Color Tint => Hex(Current.Tint);
        /// A touch lighter than the tint: menus and tooltips.
        public static Color MenuTint => Add(Tint, 8, 10, 14);

        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernGlass", "theme.json");

        public static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);
        public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
        public static Color Scale(Color c, double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        static Color Add(Color c, int r, int g, int b) => Color.FromRgb((byte)Math.Min(255, c.R + r), (byte)Math.Min(255, c.G + g), (byte)Math.Min(255, c.B + b));

        public static Preset Find(string name) =>
            Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

        public static void Set(string name, double opacity)
        {
            Current = Find(name);
            Opacity = Math.Clamp(opacity <= 0 ? 0.89 : opacity, 0.15, 1);
        }

        class FileData
        {
            public string Theme { get; set; } = "Blue";
            public double Opacity { get; set; } = 0.89;
        }

        public static bool Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var d = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FilePath));
                if (d == null) return false;
                Set(d.Theme, d.Opacity);
                return true;
            }
            catch { return false; }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var json = JsonSerializer.Serialize(new FileData { Theme = Current.Name, Opacity = Math.Round(Opacity, 2) });
                if (File.Exists(FilePath) && File.ReadAllText(FilePath) == json) return;
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        /// Replaces the shared brushes; XAML uses them as DynamicResource so everything follows.
        public static void ApplyBrushes(ResourceDictionary r, double menuAlpha)
        {
            r["Accent"] = Frozen(new SolidColorBrush(Accent));
            r["AccentGradient"] = Frozen(new LinearGradientBrush(Accent, Accent2, new Point(0, 0), new Point(1, 1)));
            r["CardSelected"] = Frozen(new SolidColorBrush(WithAlpha(Accent, 0.18)));
            r["AccentSoft"] = Frozen(new SolidColorBrush(WithAlpha(Accent, 0.18)));
            r["AccentLine"] = Frozen(new SolidColorBrush(WithAlpha(Accent, 0.5)));
            r["MenuBg"] = Frozen(new SolidColorBrush(WithAlpha(MenuTint, menuAlpha)));
        }

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        /// Calls back (on any thread) when the file changes.
        public static FileSystemWatcher Watch(Action changed)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var w = new FileSystemWatcher(Path.GetDirectoryName(FilePath), "theme.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
                };
                w.Changed += (s, e) => changed();
                w.Created += (s, e) => changed();
                w.Renamed += (s, e) => changed();
                w.EnableRaisingEvents = true;
                return w;
            }
            catch { return null; }
        }
    }
}
