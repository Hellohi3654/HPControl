using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HPControl.App
{
    /// <summary>
    /// Tray helper (runs in the user session). Everything here is event driven: foreground-window hook,
    /// registry change notifications for mic/camera use, WM_SETTINGCHANGE for tablet mode. The only timer
    /// is a 30 s idle/fullscreen check.
    /// </summary>
    sealed class Agent : ApplicationContext
    {
        readonly NotifyIcon _tray;
        readonly MsgWindow _win;
        readonly SynchronizationContext _ui;
        readonly System.Windows.Forms.Timer _timer;
        NativeApp.WinEventDelegate _hookProc;
        IntPtr _hook;
        PipeChannel _ch;
        volatile bool _exit;
        readonly ServiceClient _client = new ServiceClient();

        // updates: checked at most once a day; installing is always the user's click
        System.Threading.Timer _updTimer;
        volatile Updater.Release _update;
        volatile bool _updating, _balloonIsUpdate;

        // context
        string _app = "";
        bool _fullscreen, _tablet, _mic, _cam, _game;
        int _idleBucket;
        bool _notifications = true;
        bool _hotkeyEnabled;
        string _hotkey = "";

        public Agent()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _win = new MsgWindow(this);
            _tray = new NotifyIcon { Icon = Icons.App(16), Text = "HP Control", Visible = true };
            _tray.DoubleClick += (s, e) => OpenUi();
            _tray.MouseUp += (s, e) => { if (e.Button == MouseButtons.Left) OpenUi(); };
            _tray.ContextMenuStrip = BuildMenu();

            _hookProc = OnForeground;
            _hook = NativeApp.SetWinEventHook(NativeApp.EVENT_SYSTEM_FOREGROUND, NativeApp.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _hookProc, 0, 0, NativeApp.WINEVENT_OUTOFCONTEXT);
            _tablet = NativeApp.GetSystemMetrics(NativeApp.SM_CONVERTIBLESLATEMODE) == 0;
            UpdateForeground(NativeApp.GetForegroundWindow());

            _timer = new System.Windows.Forms.Timer { Interval = 30000 };
            _timer.Tick += (s, e) => PeriodicCheck();
            _timer.Start();

            _tray.BalloonTipClicked += (s, e) => { if (_balloonIsUpdate) InstallUpdate(); };
            _tray.BalloonTipClosed += (s, e) => _balloonIsUpdate = false;
            // first look 3 minutes after sign-in (not during the start-up rush), then every 6 h (checks if a day has passed)
            _updTimer = new System.Threading.Timer(_ => AutoCheck(), null, TimeSpan.FromMinutes(3), TimeSpan.FromHours(6));

            new Thread(PipeLoop) { IsBackground = true, Name = "agent-pipe" }.Start();
            new Thread(PrivacyWatch) { IsBackground = true, Name = "agent-privacy" }.Start();
        }

        // ------------------------------------------------------------ tray menu
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Card, ForeColor = Theme.Text, ShowImageMargin = false, Font = Theme.Body.Value };
            m.Opening += (s, e) => RebuildMenu(m);
            m.Items.Add("Loading…");
            return m;
        }

        void RebuildMenu(ContextMenuStrip m)
        {
            // Items.Clear() doesn't dispose the old items, so do it here (the menu is rebuilt on every open)
            var old = new List<ToolStripItem>(); foreach (ToolStripItem i in m.Items) old.Add(i);
            m.Items.Clear();
            foreach (var i in old) i.Dispose();
            Telemetry tel = null; Settings set = null;
            try
            {
                var st = _client.Call("getState") as Dictionary<string, object>;
                tel = Json.ConvertTo<Telemetry>(st?["tel"]);
                set = Json.ConvertTo<Settings>(_client.Call("getSettings"));
            }
            catch { }
            var open = new ToolStripMenuItem("Open HP Control") { Font = Theme.BodyBold.Value };
            open.Click += (s, e) => OpenUi();
            m.Items.Add(open);
            var upd = _update;
            if (upd != null)
            {
                var ui = new ToolStripMenuItem(_updating ? "Updating…" : "Install update " + upd.Version) { Font = Theme.BodyBold.Value, Enabled = !_updating };
                ui.Click += (s, e) => InstallUpdate();
                m.Items.Add(ui);
            }
            if (tel != null)
            {
                string t = double.IsNaN(tel.ControlTemp) ? "" : tel.ControlTemp.ToString("0") + " °C · ";
                m.Items.Add(new ToolStripMenuItem(t + tel.Status) { Enabled = false });
            }
            else m.Items.Add(new ToolStripMenuItem("Service not running") { Enabled = false });
            m.Items.Add(new ToolStripSeparator());

            var autoIt = new ToolStripMenuItem("Auto (recommended)") { Checked = set != null && set.ControlMode == "Auto" };
            autoIt.Click += (s, e) => Try(() => _client.Call("setControlMode", new Dictionary<string, object> { { "mode", "Auto" } }));
            m.Items.Add(autoIt);
            var modes = new ToolStripMenuItem("HP mode (manual)");
            foreach (var mode in Settings.ModeNames)
            {
                var mm = mode;
                var it = new ToolStripMenuItem(Settings.ModeLabel(mode)) { Checked = set != null && set.ControlMode == "Manual" && set.ManualHpMode == mode };
                it.Click += (s, e) => Try(() => _client.Call("setManualMode", new Dictionary<string, object> { { "mode", mm } }));
                modes.DropDownItems.Add(it);
            }
            m.Items.Add(modes);
            if (set != null)
            {
                var profs = new ToolStripMenuItem("Profile (auto)");
                string cur = tel != null && tel.OnAC ? set.ProfileAC : set.ProfileDC;
                foreach (var p in set.Profiles)
                {
                    var name = p.Name;
                    var it = new ToolStripMenuItem(name) { Checked = set.ControlMode == "Auto" && name == cur };
                    it.Click += (s, e) => Try(() => _client.Call("setProfile", new Dictionary<string, object> { { "name", name }, { "which", tel != null && tel.OnAC ? "ac" : "dc" } }));
                    profs.DropDownItems.Add(it);
                }
                m.Items.Add(profs);
            }
            m.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Close tray helper");
            exit.Click += (s, e) => ExitThread();
            m.Items.Add(exit);
            foreach (ToolStripItem i in m.Items) i.ForeColor = Theme.Text;
        }

        void Try(Action a) { try { a(); } catch (Exception ex) { Notify("HP Control", ex.Message); } }

        void OpenUi()
        {
            try
            {
                var exe = Application.ExecutablePath;
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
            catch { }
        }

        void Notify(string title, string text, bool isUpdate = false)
        {
            if (!_notifications) return;
            _ui.Post(_ => { try { _balloonIsUpdate = isUpdate; _tray.ShowBalloonTip(isUpdate ? 10000 : 3000, title, text, ToolTipIcon.None); } catch { } }, null);
        }

        // ------------------------------------------------------------ updates
        void AutoCheck()
        {
            try
            {
                if (_updating || (DateTime.UtcNow - Updater.LastCheck).TotalHours < 23) return;
                Settings set;
                try { set = Json.ConvertTo<Settings>(_client.Call("getSettings")); } catch { return; }   // service not up yet: next time
                if (set?.General == null || !set.General.AutoUpdate) return;
                var r = Updater.Check(set.General.UpdateRepo);
                string offered = Updater.LastOffered;
                Updater.SaveState(DateTime.UtcNow, r?.Tag ?? offered);
                _update = r;
                // tell once per new version; after that it stays in the tray menu and the Settings page
                if (r != null && r.Tag != offered) Notify("HP Control " + r.Version + " is available", "Click here to install it (Windows will ask for admin).", true);
            }
            catch (Exception ex) { Program.LogError("update check", ex); Updater.SaveState(DateTime.UtcNow, Updater.LastOffered); }
        }

        void InstallUpdate()
        {
            var r = _update;
            if (r == null || _updating) return;
            _updating = true;
            Notify("Updating HP Control", "Downloading version " + r.Version + "…");
            new Thread(() =>
            {
                try { Updater.DownloadAndInstall(r, null); }   // the installer closes this helper and starts the new one
                catch (Exception ex) { _updating = false; Notify("Update failed", ex.Message); Program.LogError("update", ex); }
            }) { IsBackground = true, Name = "update" }.Start();
        }

        // ------------------------------------------------------------ service channel
        void PipeLoop()
        {
            while (!_exit)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        pipe.Connect(3000);
                        _ch = new PipeChannel(pipe);
                        _ch.Send(new Dictionary<string, object> { { "cmd", "agentHello" }, { "args", new Dictionary<string, object> { { "pid", Process.GetCurrentProcess().Id } } } });
                        SendContext();
                        while (!_exit)
                        {
                            var msg = _ch.Receive();
                            if (msg == null) break;
                            Handle(msg);
                        }
                    }
                }
                catch (TimeoutException) { }   // service not running yet
                catch (Exception ex) { Program.LogError("agent pipe", ex); }
                _ch = null;
                if (!_exit) Thread.Sleep(10000);
            }
        }

        void Handle(Dictionary<string, object> msg)
        {
            var a = msg.Obj("args");
            switch (msg.Str("cmd"))
            {
                case "config":
                    _notifications = a.Bool("notifications", true);
                    _hotkeyEnabled = a.Bool("hotkeyEnabled");
                    _hotkey = a.Str("hotkey", "");
                    bool tray = a.Bool("trayIcon", true);
                    _ui.Post(_ => { _tray.Visible = tray; _win.SetHotkey(_hotkeyEnabled ? _hotkey : null); }, null);
                    break;
                case "osd":
                    _ui.Post(_ => Osd.Show(a.Str("title", ""), a.Str("text", "")), null);
                    break;
                case "notify":
                    Notify(a.Str("title", "HP Control"), a.Str("text", ""));
                    break;
            }
        }

        // ------------------------------------------------------------ context
        void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) => UpdateForeground(hwnd);

        void UpdateForeground(IntPtr hwnd)
        {
            string path = "";
            try { path = NativeApp.ProcessPath(hwnd); } catch { }
            string app = System.IO.Path.GetFileName(path ?? "");
            bool fs = IsFullscreen();
            bool game = GameDetect.IsGame(path);
            if (app != _app || fs != _fullscreen || game != _game) { _app = app; _fullscreen = fs; _game = game; SendContext(); }
        }

        static bool IsFullscreen()
        {
            // 3 = D3D fullscreen, 4 = presentation mode. 2 ("busy") also fires for any topmost full-screen overlay
            // window, so for that one the foreground window itself has to cover the monitor.
            try
            {
                if (NativeApp.SHQueryUserNotificationState(out int s) != 0) return false;
                if (s == 3 || s == 4) return true;
                return s == 2 && NativeApp.ForegroundCoversMonitor();
            }
            catch { return false; }
        }

        void PeriodicCheck()
        {
            bool fs = IsFullscreen();
            int idle = (int)(NativeApp.IdleSeconds() / 60) * 60;
            if (fs != _fullscreen || idle != _idleBucket) { _fullscreen = fs; _idleBucket = idle; SendContext(); }
        }

        public void OnSettingChange(string area)
        {
            if (area == "ConvertibleSlateMode")
            {
                bool t = NativeApp.GetSystemMetrics(NativeApp.SM_CONVERTIBLESLATEMODE) == 0;
                if (t != _tablet) { _tablet = t; SendContext(); }
            }
        }

        public void OnHotkey()
        {
            try { _client.Call("cycleProfile", new Dictionary<string, object> { { "dir", 1 } }); } catch { }
        }

        void SendContext()
        {
            var ch = _ch;
            if (ch == null) return;
            try
            {
                ch.Send(new Dictionary<string, object> { { "cmd", "context" }, { "args", new Dictionary<string, object> {
                    { "app", _app }, { "game", _game }, { "fullscreen", _fullscreen }, { "tablet", _tablet }, { "idleSec", _idleBucket }, { "mic", _mic }, { "cam", _cam } } } });
            }
            catch { }
        }

        /// <summary>Watches the privacy "in use" registry state (what drives the Windows mic/camera indicators).</summary>
        void PrivacyWatch()
        {
            const string basePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
            var self = Application.ExecutablePath.Replace('\\', '#');
            while (!_exit)
            {
                try
                {
                    using (var mic = Registry.CurrentUser.OpenSubKey(basePath + "microphone"))
                    using (var cam = Registry.CurrentUser.OpenSubKey(basePath + "webcam"))
                    using (var ev1 = new AutoResetEvent(false))
                    using (var ev2 = new AutoResetEvent(false))
                    {
                        if (mic == null && cam == null) return;
                        var waits = new WaitHandle[] { ev1, ev2 };
                        bool armMic = true, armCam = true;
                        while (!_exit)
                        {
                            // a notification fires once, so only re-arm the key that fired (re-arming both would
                            // pile up pending registrations on the quiet one)
                            if (armMic && mic != null) NativeApp.RegNotifyChangeKeyValue(mic.Handle, true, 0x5, ev1.SafeWaitHandle.DangerousGetHandle(), true);
                            if (armCam && cam != null) NativeApp.RegNotifyChangeKeyValue(cam.Handle, true, 0x5, ev2.SafeWaitHandle.DangerousGetHandle(), true);
                            bool m = InUse(mic, self), c = InUse(cam, self);
                            if (m != _mic || c != _cam) { _mic = m; _cam = c; SendContext(); }
                            int hit = WaitHandle.WaitAny(waits, 600000);
                            armMic = hit == 0; armCam = hit == 1;
                            Thread.Sleep(300);
                        }
                    }
                }
                catch { Thread.Sleep(30000); }
            }
        }

        static bool InUse(RegistryKey root, string selfPath)
        {
            if (root == null) return false;
            bool Check(RegistryKey k)
            {
                var start = k.GetValue("LastUsedTimeStart"); var stop = k.GetValue("LastUsedTimeStop");
                return start is long s && s != 0 && stop is long e && e == 0;
            }
            foreach (var name in root.GetSubKeyNames())
            {
                using (var k = root.OpenSubKey(name))
                {
                    if (k == null) continue;
                    if (name == "NonPackaged")
                    {
                        foreach (var n2 in k.GetSubKeyNames())
                        {
                            if (string.Equals(n2, selfPath, StringComparison.OrdinalIgnoreCase)) continue;
                            using (var k2 = k.OpenSubKey(n2)) if (k2 != null && Check(k2)) return true;
                        }
                    }
                    else if (Check(k)) return true;
                }
            }
            return false;
        }

        protected override void ExitThreadCore()
        {
            _exit = true;
            try { _updTimer?.Dispose(); } catch { }
            try { if (_hook != IntPtr.Zero) NativeApp.UnhookWinEvent(_hook); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            _win.SetHotkey(null);
            _win.DestroyHandle();
            base.ExitThreadCore();
        }

        /// <summary>Hidden top-level window: receives WM_SETTINGCHANGE broadcasts and hotkeys.</summary>
        sealed class MsgWindow : NativeWindow
        {
            readonly Agent _a;
            bool _registered;
            public MsgWindow(Agent a) { _a = a; CreateHandle(new CreateParams { Caption = "HPControlAgent" }); }

            public void SetHotkey(string spec)
            {
                if (_registered) { NativeApp.UnregisterHotKey(Handle, 1); _registered = false; }
                if (string.IsNullOrWhiteSpace(spec) || Handle == IntPtr.Zero) return;
                uint mods = 0x4000; Keys key = Keys.None;
                foreach (var part in spec.Split('+').Select(p => p.Trim()))
                {
                    switch (part.ToLowerInvariant())
                    {
                        case "ctrl": case "control": mods |= 2; break;
                        case "alt": mods |= 1; break;
                        case "shift": mods |= 4; break;
                        case "win": mods |= 8; break;
                        default: Enum.TryParse(part, true, out key); break;
                    }
                }
                if (key != Keys.None) _registered = NativeApp.RegisterHotKey(Handle, 1, mods, (uint)key);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeApp.WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    try { _a.OnSettingChange(System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam)); } catch { }
                }
                else if (m.Msg == NativeApp.WM_HOTKEY) _a.OnHotkey();
                base.WndProc(ref m);
            }
        }
    }

    /// <summary>Small on-screen display used for hotkey feedback.</summary>
    sealed class Osd : Form
    {
        static Osd _cur;
        readonly string _title, _text;
        readonly System.Windows.Forms.Timer _t = new System.Windows.Forms.Timer { Interval = 40 };
        int _age;

        Osd(string title, string text)
        {
            _title = title; _text = text;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card; Size = new Size(Theme.S(300), Theme.S(84)); Opacity = 0.95;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - Theme.S(80));
            using (var p = Theme.Round(new RectangleF(0, 0, Width, Height), Theme.S(18))) Region = new Region(p);
            _t.Tick += (s, e) => { _age += 40; if (_age > 1400) Opacity = Math.Max(0, 0.95 - (_age - 1400) / 400.0); if (_age > 1800) Close(); };
            _t.Start();
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, _title, Theme.Small.Value, new Rectangle(0, Theme.S(12), Width, Theme.S(20)), Theme.Sub, TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(e.Graphics, _text, Theme.H2.Value, new Rectangle(0, Theme.S(34), Width, Theme.S(34)), Theme.Text, TextFormatFlags.HorizontalCenter);
        }

        public static void Show(string title, string text)
        {
            try { _cur?.Close(); } catch { }
            _cur = new Osd(title, text);
            _cur.Show();
        }

        protected override void Dispose(bool disposing) { if (disposing) _t.Dispose(); base.Dispose(disposing); }
    }
}
