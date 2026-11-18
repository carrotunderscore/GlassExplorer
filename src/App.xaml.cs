using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace GlassExplorer
{
    public partial class App : Application
    {
        static Mutex instanceMutex;
        static string PipeName => "GlassExplorer-" + Environment.UserName + "-" + Environment.UserDomainName;

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        System.IO.FileSystemWatcher themeWatch;
        System.Windows.Threading.DispatcherTimer themeDelay;

        /// Colour and opacity come from GlassBar (%LOCALAPPDATA%\ModernGlass\theme.json) and follow it live.
        /// Explorer's own opacity (⋯ menu), separate from the bar's.
        public static double WindowOpacity = 0.89;

        public void ApplyTheme()
        {
            GlassTheme.Load();
            double op = System.Math.Clamp(WindowOpacity, 0.3, 1);
            GlassTheme.ApplyBrushes(Resources, System.Math.Min(1, op * 1.09));
            var t = GlassTheme.Tint;
            Resources["WindowTint"] = Frozen(new System.Windows.Media.SolidColorBrush(GlassTheme.WithAlpha(t, System.Math.Min(1, op * 0.93))));
            Resources["SidebarTint"] = Frozen(new System.Windows.Media.SolidColorBrush(GlassTheme.WithAlpha(GlassTheme.Scale(t, 0.45), 0.23)));
        }

        static T Frozen<T>(T f) where T : System.Windows.Freezable { f.Freeze(); return f; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            WindowOpacity = Settings.Load().Opacity;
            ApplyTheme();
            themeDelay = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(120) };
            themeDelay.Tick += (s, a) => { themeDelay.Stop(); ApplyTheme(); };
            themeWatch = GlassTheme.Watch(() => Dispatcher.BeginInvoke(() => { themeDelay.Stop(); themeDelay.Start(); }));
            var en = new System.Globalization.CultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = en;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = en;
            System.Threading.Thread.CurrentThread.CurrentCulture = en;
            System.Threading.Thread.CurrentThread.CurrentUICulture = en;
            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                Dialog.Info(MainWindow, "Something went wrong", ex.Exception.Message);
            };

            string path = null, snapshot = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length) snapshot = e.Args[++i];
                else path = e.Args[i];
            }

            // One app, many tabs: if it's already running, hand the folder over and exit.
            if (snapshot == null)
            {
                instanceMutex = new Mutex(true, @"Local\GlassExplorer-SingleInstance", out bool first);
                if (!first && SendToRunning(path)) { Shutdown(); return; }
                if (first) _ = Task.Run(PipeServer);
            }

            var w = new MainWindow(path, snapshot);
            MainWindow = w;
            w.Show();
        }

        static bool SendToRunning(string path)
        {
            try
            {
                AllowSetForegroundWindow(-1); // let the running window come to the front
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(2000);
                var bytes = Encoding.UTF8.GetBytes(path ?? "");
                c.Write(bytes, 0, bytes.Length);
                c.Flush();
                return true;
            }
            catch { return false; }
        }

        async Task PipeServer()
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    var path = Encoding.UTF8.GetString(ms.ToArray());
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        var target = GlassExplorer.MainWindow.LastActive;
                        if (target == null || !target.IsLoaded)
                        {
                            var w = new MainWindow(path, null);
                            w.Show();
                        }
                        else target.OpenFromOtherInstance(path);
                    });
                }
                catch { await Task.Delay(200); }
            }
        }
    }
}
