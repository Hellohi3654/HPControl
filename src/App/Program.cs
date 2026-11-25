using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HPControl.App
{
    static class Program
    {
        public const int WM_SHOWME = 0x8000 + 0x4C;

        public static void LogError(string where, Exception ex)
        {
            try
            {
                var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HPControl");
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "ui.log");
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 256 * 1024) { System.IO.File.Copy(path, path + ".1", true); System.IO.File.Delete(path); }
                System.IO.File.AppendAllText(path, DateTime.Now.ToString("s") + " " + where + ": " + ex + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError("unhandled", e.ExceptionObject as Exception ?? new Exception("unknown"));
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                LogError("ui", e.Exception);
                try { MessageBox.Show("HP Control hit an error:\n\n" + e.Exception.Message + "\n\nDetails were written to %LOCALAPPDATA%\\HPControl\\ui.log", "HP Control", MessageBoxButtons.OK, MessageBoxIcon.Warning); } catch { }
            };
            try { Main2(args); }
            catch (Exception ex)
            {
                LogError("startup", ex);
                try { MessageBox.Show("HP Control couldn't start:\n\n" + ex.Message + "\n\nDetails: %LOCALAPPDATA%\\HPControl\\ui.log", "HP Control", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            }
        }

        static void Main2(string[] args)
        {
            if (args.Any(a => a.Equals("--audiofx", StringComparison.OrdinalIgnoreCase))) { Environment.Exit(AudioFxHost.Run(args)); return; }
            try { NativeApp.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;

            bool agent = args.Any(a => a.Equals("--agent", StringComparison.OrdinalIgnoreCase));
            if (agent)
            {
                using (var mtx = new Mutex(true, "HPControl.Agent." + Environment.UserName, out bool created))
                {
                    if (!created) return;
                    Application.Run(new Agent());
                }
                return;
            }

            using (var mtx = new Mutex(true, "HPControl.UI." + Environment.UserName, out bool created))
            {
                if (!created)
                {
                    var h = NativeApp.FindWindow(null, "HP Control");
                    if (h != IntPtr.Zero) { NativeApp.ShowWindow(h, 9); NativeApp.SetForegroundWindow(h); }
                    return;
                }
                EnsureAgent();
                Application.Run(new MainForm());
            }
        }

        /// <summary>Starts the tray helper if it's enabled for autostart but not running yet.</summary>
        static void EnsureAgent()
        {
            try
            {
                if (Mutex.TryOpenExisting("HPControl.Agent." + Environment.UserName, out var m)) { m.Dispose(); return; }
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    if (k?.GetValue("HPControlAgent") == null) return;
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--agent") { UseShellExecute = false })?.Dispose();
            }
            catch { }
        }
    }
}
