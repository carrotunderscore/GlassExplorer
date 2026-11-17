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

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
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
