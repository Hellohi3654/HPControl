using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HPControl.App
{
    interface IPage
    {
        void OnShow();
        void OnTelemetry(Telemetry t, List<LogEntry> events);
    }

    /// <summary>Shared state between pages: service connection + editable settings with debounced save.</summary>
    class Model
    {
        public readonly ServiceClient Client = new ServiceClient();
        public Settings Settings;
        public Telemetry Tel = new Telemetry();
        public event Action SettingsReloaded;
        public event Action<string> Toast;
        public void ShowToast(string msg) => Toast?.Invoke(msg);
        readonly Timer _save = new Timer { Interval = 700 };
        bool _dirty;

        public Model()
        {
            _save.Tick += (s, e) => { _save.Stop(); SaveNow(); };
        }

        /// <summary>Bumped whenever Settings is replaced by a fresh copy from the service (pages compare it to know
        /// whether they need to rebuild).</summary>
        public int Version { get; private set; }
        string _lastJson;
        // background calls (opening a page, slow reads) use their own connection on a worker thread, so the window
        // never waits on the service
        readonly ServiceClient _bg = new ServiceClient();

        public bool Reload()
        {
            try
            {
                Settings = Json.ConvertTo<Settings>(Client.Call("getSettings"));
                _lastJson = Json.Serialize(Settings);
                _dirty = false;
                Version++;
                SettingsReloaded?.Invoke();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Fetches the settings on a worker thread. Only replaces them (and raises SettingsReloaded) when they
        /// changed and there are no unsaved edits. done(changed) runs on the UI thread.</summary>
        public void ReloadAsync(Control owner, Action<bool> done = null)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Settings fresh = null; string json = null;
                try { fresh = Json.ConvertTo<Settings>(_bg.Call("getSettings")); json = Json.Serialize(fresh); } catch { }
                try
                {
                    owner.BeginInvoke((Action)(() =>
                    {
                        if (owner.IsDisposed) return;
                        bool changed = fresh != null && !_dirty && json != _lastJson;
                        if (changed) { Settings = fresh; _lastJson = json; Version++; SettingsReloaded?.Invoke(); }
                        done?.Invoke(changed);
                    }));
                }
                catch { }
            });
        }

        /// <summary>A service call on a worker thread; done(result or null) runs on the UI thread.</summary>
        public void CallAsync(Control owner, string cmd, object args, Action<object> done)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                object r = null;
                try { r = _bg.Call(cmd, args); } catch { }
                try { owner.BeginInvoke((Action)(() => { if (!owner.IsDisposed) done(r); })); } catch { }
            });
        }

        public void MarkDirty() { _dirty = true; _save.Stop(); _save.Start(); }

        public void SaveNow()
        {
            if (!_dirty || Settings == null) return;
            try
            {
                // runtime fields are owned by the service (tray, hotkey, HP app can change them) — don't overwrite them with stale copies
                var cur = Json.ConvertTo<Settings>(Client.Call("getSettings"));
                Settings.ControlMode = cur.ControlMode;
                Settings.ManualHpMode = cur.ManualHpMode;
                Settings.ProfileAC = cur.ProfileAC;
                Settings.ProfileDC = cur.ProfileDC;
                Settings.Battery = cur.Battery;
                var json = Json.Serialize(Settings);
                Client.Call("setSettings", new Dictionary<string, object> { { "settings", Json.Parse(json) } });
                _lastJson = json;
                _dirty = false;
                Toast?.Invoke("Saved");
            }
            catch (Exception ex) { Toast?.Invoke("Save failed: " + ex.Message); }
        }

        public object Call(string cmd, object args = null)
        {
            try { return Client.Call(cmd, args); }
            catch (Exception ex) { Toast?.Invoke(ex.Message); return null; }
        }

        public static Dictionary<string, object> A(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    class MainForm : Form
    {
        readonly Model _m = new Model();
        readonly Panel _nav = new Panel();
        readonly Panel _host = new Panel();
        readonly Label _status = new Label();
        readonly Label _toast = new Label();
        readonly Timer _poll = new Timer { Interval = 1000 };
        readonly Timer _toastTimer = new Timer { Interval = 2200 };
        readonly List<NavButton> _buttons = new List<NavButton>();
        readonly Dictionary<string, Control> _pages = new Dictionary<string, Control>();
        Control _current;
        bool _connected;
        int _failCount;

        public MainForm()
        {
            Text = "HP Control";
            Icon = Icons.App(32);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Body.Value;
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Theme.S(1240), wa.Width - 40), Math.Min(Theme.S(860), wa.Height - 40));
            MinimumSize = new Size(Theme.S(900), Theme.S(600));
            DoubleBuffered = true;

            _nav.Dock = DockStyle.Left; _nav.Width = Theme.S(232); _nav.BackColor = Theme.Nav;
            _host.Dock = DockStyle.Fill; _host.BackColor = Theme.Bg;
            Controls.Add(_host); Controls.Add(_nav);

            var logo = new PictureBox { Image = Icons.Glyph(Theme.S(34)), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.S(22), Theme.S(24)), BackColor = Color.Transparent };
            var title = new Label { Text = "HP Control", Font = Theme.H2.Value, ForeColor = Theme.Text, AutoSize = true, Location = new Point(Theme.S(66), Theme.S(28)), BackColor = Color.Transparent };
            _nav.Controls.Add(logo); _nav.Controls.Add(title);

            int y = Theme.S(96);
            foreach (var (key, text) in new[] { ("home", "Home"), ("system", "System control"), ("battery", "Battery manager"), ("audio", "Audio"), ("settings", "Settings") })
            {
                var b = new NavButton(text) { Tag = key, Location = new Point(Theme.S(12), y), Width = Theme.S(208) };
                b.Click += (s, e) => Show((string)((Control)s).Tag);
                _nav.Controls.Add(b); _buttons.Add(b);
                y += b.Height + Theme.S(4);
            }
            _status.Font = Theme.Small.Value; _status.ForeColor = Theme.Sub; _status.BackColor = Color.Transparent;
            _status.AutoSize = false; _status.Size = new Size(Theme.S(208), Theme.S(60));
            _status.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            _status.Location = new Point(Theme.S(18), _nav.Height - Theme.S(70));
            _nav.Controls.Add(_status);
            _nav.Resize += (s, e) => _status.Location = new Point(Theme.S(18), _nav.Height - Theme.S(70));

            _toast.AutoSize = true; _toast.BackColor = Theme.AccentDim; _toast.ForeColor = Color.White; _toast.Padding = new Padding(Theme.S(12), Theme.S(6), Theme.S(12), Theme.S(6));
            _toast.Visible = false; _toast.Font = Theme.Body.Value;
            Controls.Add(_toast); _toast.BringToFront();
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); _toast.Visible = false; };
            _m.Toast += msg => BeginInvoke((Action)(() => ShowToast(msg)));

            _pages["home"] = new HomePage(_m, this);
            _pages["system"] = new SystemPage(_m);
            _pages["battery"] = new BatteryPage(_m);
            _pages["audio"] = new AudioPage(_m);
            _pages["settings"] = new SettingsPage(_m);
            foreach (var p in _pages.Values) { p.Dock = DockStyle.Fill; p.Visible = false; _host.Controls.Add(p); }

            _poll.Tick += (s, e) => Poll();
            // back to 1 s polling as soon as the window is restored
            Resize += (s, e) => { if (WindowState != FormWindowState.Minimized && _poll.Interval != 1000) { _poll.Interval = 1000; Poll(); } };
            Load += (s, e) => NativeApp.DarkTitle(Handle);
            Shown += (s, e) =>
            {
                Show("home");
                // connect after the window is on screen so a slow/missing service can never block it
                BeginInvoke((Action)(() => { Connect(); if (_connected && _current != null) { ((IPage)_current).OnShow(); } _poll.Start(); }));
            };
            FormClosing += (s, e) => { _m.SaveNow(); _poll.Stop(); };
        }

        void ShowToast(string msg)
        {
            _toast.Text = msg;
            _toast.Location = new Point(ClientSize.Width - _toast.PreferredWidth - Theme.S(24), ClientSize.Height - _toast.PreferredHeight - Theme.S(20));
            _toast.Visible = true; _toast.BringToFront();
            _toastTimer.Stop(); _toastTimer.Start();
        }

        void Connect()
        {
            _connected = _m.Client.TryConnect(800) && _m.Reload();
            if (_connected) _pollClient.TryConnect(800);
            UpdateStatus();
        }

        void UpdateStatus()
        {
            if (!_connected) { _status.Text = "● Service not running\nRun install.cmd (or restart the service)"; _status.ForeColor = Theme.Bad; return; }
            var t = _m.Tel;
            _status.ForeColor = Theme.Sub;
            _status.Text = (t.BiosOk ? "● Connected" : "● Connected (BIOS interface unavailable)") + "\n" + (t.AgentConnected ? "Tray helper running" : "Tray helper not running");
        }

        public void Show(string key)
        {
            if (!_pages.TryGetValue(key, out var p)) return;
            _m.SaveNow();
            foreach (var b in _buttons) b.Selected = (string)b.Tag == key;
            // switch with drawing paused and paint the new page once. Pages show what they already have straight away
            // and fetch anything fresh in the background, so opening a page never waits on the service.
            NativeApp.Frozen(_host, () =>
            {
                if (_current != null) _current.Visible = false;
                _current = p; p.Visible = true; p.BringToFront();
                ((IPage)p).OnShow();
                ((IPage)p).OnTelemetry(_m.Tel, new List<LogEntry>());
                p.PerformLayout();
            });
        }

        int _busy;
        volatile bool _polling;
        // status polling uses its own connection on a worker thread, so a slow service never freezes the window
        readonly ServiceClient _pollClient = new ServiceClient();

        void Poll()
        {
            // minimised: refresh every 10 s instead of every second (the timer itself slows down, see Resize)
            int want = WindowState == FormWindowState.Minimized ? 10000 : 1000;
            if (_poll.Interval != want) _poll.Interval = want;
            if (!_connected)
            {
                if (++_failCount % 3 == 0) { Connect(); if (_connected && _current != null) ((IPage)_current).OnShow(); }
                return;
            }
            if (_polling) return;
            _polling = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Telemetry tel = null; List<LogEntry> ev = null; Exception err = null;
                try
                {
                    var st = _pollClient.Call("getState", Model.A("events", 12)) as Dictionary<string, object>;
                    tel = Json.ConvertTo<Telemetry>(st["tel"]);
                    ev = Json.ConvertTo<List<LogEntry>>(st["events"]);
                }
                catch (Exception ex) { err = ex; }
                try { BeginInvoke((Action)(() => { _polling = false; OnPolled(tel, ev, err); })); }
                catch { _polling = false; }
            });
        }

        void OnPolled(Telemetry tel, List<LogEntry> ev, Exception err)
        {
            if (IsDisposed) return;
            if (err == null && tel != null)
            {
                _busy = 0;
                _m.Tel = tel;
                try { (_current as IPage)?.OnTelemetry(tel, ev ?? new List<LogEntry>()); } catch (Exception ex) { Program.LogError("page update", ex); }
                UpdateStatus();
                return;
            }
            // a slow answer (CPU flat out) isn't a dead service: say "busy" and keep trying before giving up
            bool slow = err is System.IO.IOException && err.InnerException != null;
            if (slow && ++_busy < 5)
            {
                _status.Text = "● Service busy, waiting…\n" + (_m.Tel.AgentConnected ? "Tray helper running" : "Tray helper not running");
                _status.ForeColor = Theme.Sub;
                return;
            }
            Program.LogError("status poll", err);
            _busy = 0; _connected = false; UpdateStatus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WM_SHOWME) { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); }
            base.WndProc(ref m);
        }
    }

    class NavButton : Control
    {
        bool _sel, _hover;
        public bool Selected { get => _sel; set { _sel = value; Invalidate(); } }
        public NavButton(string text)
        {
            Text = text; Height = Theme.S(44); Font = Theme.Body.Value; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); SetStyle(ControlStyles.StandardDoubleClick, false);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Nav);
            Theme.Smooth(e.Graphics);
            if (_sel || _hover)
                using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(10)))
                using (var b = new SolidBrush(_sel ? Color.FromArgb(46, 50, 80) : Color.FromArgb(32, 32, 42))) e.Graphics.FillPath(b, p);
            if (_sel) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, 0, Theme.S(12), Theme.S(3), Height - Theme.S(24));
            TextRenderer.DrawText(e.Graphics, Text, _sel ? Theme.BodyBold.Value : Font, new Rectangle(Theme.S(16), 0, Width - Theme.S(16), Height), _sel ? Theme.Text : Theme.Sub, TextFormatFlags.VerticalCenter);
        }
    }

    // ===================================================================== HOME
    class HomePage : Stack, IPage
    {
        readonly Model _m;
        readonly Stat _temp = new Stat("Temperature"), _mode = new Stat("HP mode"), _noise = new Stat("Fan"), _batt = new Stat("Battery");
        readonly Segmented _ctl = new Segmented(300);
        readonly RadioList _modes = new RadioList();
        readonly DarkCombo _profAc = new DarkCombo(260), _profDc = new DarkCombo(260);
        readonly Card _quick;
        readonly Row _rowAc, _rowDc, _rowRule;
        readonly ValueLabel _rule = new ValueLabel(420);
        readonly TimeGraph _graph = new TimeGraph();
        readonly Label _events = new Lbl("", Theme.Small.Value, Theme.Sub, Theme.S(40));
        readonly Lbl _header, _sub;
        bool _loading;
        DateTime _lastHist;

        public HomePage(Model m, MainForm f)
        {
            _m = m;
            AutoScroll = true;
            _header = Add(new Lbl("HP OmniBook X Flip 16", Theme.H1.Value, Theme.Text, Theme.S(40)));
            _sub = Add(new Lbl("", Theme.Body.Value, Theme.Accent, Theme.S(24)));
            var grid = Add(new Grid(4, 96));
            grid.Controls.AddRange(new Control[] { _temp, _mode, _noise, _batt });

            _quick = Add(new Card("Fan & performance control", "Auto = HP Control keeps the fan off or quiet, boosts power for heavy loads and games (plugged in) and saves battery. Manual = you pick an HP mode."));
            var fl = _quick.Add(new Flow(46));
            _ctl.Set(new[] { "Auto", "Manual" }, new[] { "Auto (recommended)", "Manual HP mode" });
            fl.Controls.Add(_ctl);
            _ctl.SelectionChanged += (s, e) => { _m.Call("setControlMode", Model.A("mode", _ctl.Selected)); _m.Reload(); Refresh2(); };
            _modes.SetItems(Settings.ModeNames.Select(k => new RadioList.Item { Key = k, Text = Settings.ModeLabel(k), Desc = ModeDesc(k) }));
            _modes.Height = _modes.Height; _quick.Add(_modes);
            _modes.SelectionChanged += (s, e) => { _m.Call("setManualMode", Model.A("mode", _modes.Selected)); _m.Reload(); };
            _rowAc = _quick.Add(new Row("Profile when plugged in", _profAc));
            _rowDc = _quick.Add(new Row("Profile on battery", _profDc));
            _rowRule = _quick.Add(new Row("Active rules / context", _rule) { Separator = false });
            _profAc.SelectedIndexChanged += (s, e) => { if (!_loading && _profAc.SelectedItem != null) { _m.Call("setProfile", Model.A("name", _profAc.SelectedItem.ToString(), "which", "ac")); _m.Reload(); } };
            _profDc.SelectedIndexChanged += (s, e) => { if (!_loading && _profDc.SelectedItem != null) { _m.Call("setProfile", Model.A("name", _profDc.SelectedItem.ToString(), "which", "dc")); _m.Reload(); } };

            var gc = Add(new Card("Last 10 minutes"));
            gc.Add(_graph);
            var ec = Add(new Card("Recent events"));
            ec.Add(_events);
            m.SettingsReloaded += Refresh2;
        }

        static string ModeDesc(string k)
        {
            switch (k)
            {
                case "SmartSense": return "HP decides automatically (the HP app default)";
                case "Balanced": return "Default HP fan and power behaviour";
                case "Cool": return "Fan runs earlier to keep the chassis cool";
                case "Quiet": return "Fan kept low; slightly less sustained power";
                case "PowerSaver": return "Lowest power and fan; best battery life";
                case "Performance": return "Highest power; fan ramps when needed";
            }
            return "";
        }

        public void OnShow() { Refresh2(); _m.ReloadAsync(this); _lastHist = DateTime.MinValue; }   // a change re-runs Refresh2 via SettingsReloaded

        void Refresh2()
        {
            var s = _m.Settings; if (s == null) return;
            _loading = true;
            _ctl.Selected = s.ControlMode; _ctl.Invalidate();
            bool manual = s.ControlMode != "Auto";
            _modes.Visible = manual; _rowAc.Visible = !manual; _rowDc.Visible = !manual; _rowRule.Visible = !manual;
            _modes.Select(s.ManualHpMode);
            foreach (var (cb, cur) in new[] { (_profAc, s.ProfileAC), (_profDc, s.ProfileDC) })
            {
                cb.Items.Clear();
                foreach (var p in s.Profiles) cb.Items.Add(p.Name);
                cb.SelectedItem = cur;
            }
            _loading = false;
            _quick.PerformLayout(); PerformLayout();
        }

        public void OnTelemetry(Telemetry t, List<LogEntry> events)
        {
            if (t == null) return;
            _sub.Text = t.Status;
            // compact: the three most useful sensors with short names
            var order = new[] { "cpu", "case", "tz1", "tz0", "tz3", "bat" };
            var shortName = new Dictionary<string, string> { { "cpu", "CPU" }, { "case", "Internal" }, { "tz0", "SoC" }, { "tz1", "Board" }, { "tz3", "Rear" }, { "bat", "Battery" } };
            var sens = string.Join(" · ", t.Temps.OrderBy(kv => { int i = Array.IndexOf(order, kv.Key); return i < 0 ? 99 : i; }).Take(3)
                .Select(kv => (shortName.TryGetValue(kv.Key, out var l) ? l : kv.Key) + " " + kv.Value.ToString("0") + "°"));
            _temp.Set(Fmt.Temp(t.ControlTemp), sens, t.ControlTemp >= 85 ? Theme.Bad : t.ControlTemp >= 70 ? Theme.Warn : Theme.Text);
            _mode.Set(Settings.ModeLabel(t.HpMode), (t.ControlMode == "Auto" ? (t.ActiveProfile + (t.PerfBudget < 100 ? " · CPU " + t.PerfBudget + "%" : "")) : "Manual") + (t.PackageW >= 0 ? " · chip " + t.PackageW.ToString("0") + " W" : ""), Theme.ModeColor(t.HpMode));
            if (t.FanRpm >= 0)
            {
                var sub = new List<string>();
                if (t.FanTargetRpm > 0 && Math.Abs(t.FanTargetRpm - t.FanRpm) > 150) sub.Add("target " + t.FanTargetRpm.ToString("N0"));
                if (t.FanLimitRpm > 0) sub.Add("limit " + t.FanLimitRpm.ToString("N0"));
                bool over = t.FanLimitRpm > 0 && t.FanRpm > t.FanLimitRpm;
                if (t.FanHeld) sub.Add(t.FanCommandRpm > 0 ? "HP Control curve" : "kept off by HP Control");
                if (sub.Count == 0) sub.Add(t.FanRpm == 0 ? "fan stopped" : "fan speed");
                _noise.Set(t.FanRpm == 0 ? "Off" : t.FanRpm.ToString("N0") + " rpm", string.Join(" · ", sub), over ? Theme.Warn : Theme.Text);
            }
            else _noise.Set("–", "no fan sensor");
            _batt.Set(t.BatteryPct + "%", (t.OnAC ? (t.Charging ? "charging" : "plugged in") : "on battery") + (Math.Abs(t.PowerW) > 0.05 ? " · " + Math.Abs(t.PowerW).ToString("0.0") + " W" : ""));
            if (t.ControlMode == "Auto") _rule.Text = (string.IsNullOrEmpty(t.ActiveRule) ? "none" : t.ActiveRule) + (string.IsNullOrEmpty(t.Context) ? "" : "  ·  " + t.Context);
            if (t.ControlMode == "Manual" && _modes.Selected != t.TargetMode && !string.IsNullOrEmpty(t.TargetMode)) _modes.Select(t.TargetMode);
            if (events != null && events.Count > 0)
                _events.Text = string.Join("\n", events.AsEnumerable().Reverse().Take(8).Select(e => e.Time.ToString("HH:mm:ss") + "   " + e.Text));
            if ((DateTime.Now - _lastHist).TotalSeconds >= 5) { _lastHist = DateTime.Now; LoadHistory(_m, _graph, 10); }
        }

        /// <summary>Fetches the temperature history in the background and redraws the graph when it arrives.</summary>
        public static void LoadHistory(Model m, TimeGraph g, int minutes)
        {
            if (Equals(g.Tag, "busy")) return;
            g.Tag = "busy";
            m.CallAsync(g, "getHistory", Model.A("minutes", minutes), r =>
            {
                g.Tag = null;
                try
                {
                    var h = r as List<object>;
                    if (h == null) return;
                    var codes = m.Settings?.ModeCodes ?? new Dictionary<string, int>();
                    g.Minutes = minutes;
                    g.Points = h.OfType<Dictionary<string, object>>().Select(d => new TimeGraph.Pt
                    {
                        T = DateTime.Parse(d.Str("T")),
                        Temp = d.Num("Temp", double.NaN),
                        Smooth = d.Num("Smooth", double.NaN),
                        Budget = (int)d.Num("Budget", 100),
                        PowerW = d.Num("PowerW", 0),
                        Cpu = d.Num("Cpu", 0),
                        Fan = (int)d.Num("Fan", -1),
                        Mode = codes.FirstOrDefault(kv => kv.Value == (int)d.Num("Mode", -1)).Key ?? ""
                    }).ToList();
                    g.Invalidate();
                }
                catch { }
            });
        }
    }
}
