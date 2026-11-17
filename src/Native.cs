using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GlassExplorer
{
    static class Native
    {
        // ---------- Blur bakom fönstret (Windows 10) ----------
        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        public static void EnableBlur(IntPtr hwnd)
        {
            var accent = new AccentPolicy { AccentState = 3 /* BLURBEHIND */ };
            int size = Marshal.SizeOf(accent);
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, p, false);
                var data = new WindowCompositionAttributeData { Attribute = 19, Data = p, SizeOfData = size };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------- Maximize to the work area (don't cover the taskbar) ----------
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

        public static IntPtr MaximizeToWorkAreaHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            var mon = MonitorFromWindow(hwnd, 2 /* NEAREST */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var work = mi.rcWork;
            // Auto-hide taskbar: leave 1 px free at the bottom so it can still pop up
            if (work.Bottom == mi.rcMonitor.Bottom && work.Top == mi.rcMonitor.Top &&
                work.Left == mi.rcMonitor.Left && work.Right == mi.rcMonitor.Right) work.Bottom -= 1;
            mmi.ptMaxPosition.X = work.Left - mi.rcMonitor.Left;
            mmi.ptMaxPosition.Y = work.Top - mi.rcMonitor.Top;
            mmi.ptMaxSize.X = work.Right - work.Left;
            mmi.ptMaxSize.Y = work.Bottom - work.Top;
            mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X);
            mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y);
            Marshal.StructureToPtr(mmi, lParam, true);
            return IntPtr.Zero; // let WPF/WindowChrome add min-size limits on top
        }

        // ---------- Miniatyrer / ikoner ----------
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint b0, b1, b2; public IntPtr dshSection; public uint dsOffset; }

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, out DIBSECTION ds);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

        public static BitmapSource GetImage(string path, int size, bool iconOnly)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                int flags = iconOnly ? 0x4 : 0x0;
                if (f.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
                return ToSource(hbm);
            }
            catch { return null; }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }

        static BitmapSource ToSource(IntPtr hbm)
        {
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();
                return s;
            }
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = w * 4;
            var buf = new byte[stride * h];
            // Be GDI om pixlarna uppifrån-och-ner (negativ höjd) så orienteringen alltid blir rätt.
            var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, hbm, 0, (uint)h, buf, ref bi, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }

        // ---------- Filinfo ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

        public static string TypeName(string ext)
        {
            var i = new SHFILEINFO();
            SHGetFileInfo(string.IsNullOrEmpty(ext) ? "file" : "file" + ext, 0x80, ref i, (uint)Marshal.SizeOf(i), 0x400 | 0x10);
            return string.IsNullOrEmpty(i.szTypeName) ? (ext.TrimStart('.').ToUpperInvariant() + "-fil") : i.szTypeName;
        }

        public static string DisplayName(string path)
        {
            var i = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref i, (uint)Marshal.SizeOf(i), 0x200);
            return string.IsNullOrEmpty(i.szDisplayName) ? path : i.szDisplayName;
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        public static extern int StrCmpLogicalW(string a, string b);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string DownloadsFolder()
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return s;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // ---------- Shell-verb (egenskaper, utforska) ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize; public uint fMask; public IntPtr hwnd;
            public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
            public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO i);

        public static bool ShellVerb(string path, string verb, IntPtr hwnd)
        {
            var i = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = 0xC, hwnd = hwnd,
                lpVerb = verb, lpFile = path, nShow = 5
            };
            return ShellExecuteEx(ref i);
        }

        // ---------- Filoperationer via Windows (förlopp, ångra, papperskorg) ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200, FOF_WANTNUKEWARNING = 0x4000;

        static string Multi(string[] paths) => string.Join("\0", paths) + "\0";

        public static void Transfer(IntPtr hwnd, string[] src, string dest, bool move, Action done)
        {
            bool sameDir = src.All(p => string.Equals(Path.GetDirectoryName(p.TrimEnd('\\')), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            RunSta(() =>
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = hwnd, wFunc = move ? 1u : 2u, pFrom = Multi(src), pTo = dest + "\0",
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | (sameDir ? FOF_RENAMEONCOLLISION : 0))
                };
                SHFileOperation(ref op);
            }, done);
        }

        public static void Delete(IntPtr hwnd, string[] paths, bool permanent, Action done)
        {
            RunSta(() =>
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = hwnd, wFunc = 3, pFrom = Multi(paths),
                    fFlags = permanent ? (ushort)0 : (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING)
                };
                SHFileOperation(ref op);
            }, done);
        }

        public static bool Rename(IntPtr hwnd, string from, string to)
        {
            var op = new SHFILEOPSTRUCT { hwnd = hwnd, wFunc = 4, pFrom = from + "\0", pTo = to + "\0", fFlags = FOF_ALLOWUNDO };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }

        static void RunSta(Action a, Action done)
        {
            var disp = Application.Current.Dispatcher;
            var t = new Thread(() =>
            {
                try { a(); } catch { }
                disp.BeginInvoke(done);
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }
    }
}
