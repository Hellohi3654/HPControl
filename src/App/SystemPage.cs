using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HPControl.App
{
    class SystemPage : Stack, IPage
    {
        readonly Model _m;
        bool _loading;
        Profile _p;

        // profile editor
        readonly DarkCombo _profile = new DarkCombo(280);
        readonly DarkText _name = new DarkText(280);
        readonly Label _desc = new Lbl("", Theme.Small.Value, Theme.Sub, Theme.S(22));
        readonly ToggleSwitch _handsOff = new ToggleSwitch();
        readonly DarkCombo _fixedMode = new DarkCombo(200);
        readonly Segmented _tabs = new Segmented(680);
        readonly CurveEditor _ed = new CurveEditor();
        readonly CurveEditor _load = new CurveEditor();
        readonly Label _hint = new Lbl("", Theme.Small.Value, Theme.Sub, Theme.S(22));
        readonly ToggleSwitch _curveOn = new ToggleSwitch();
        readonly Row _curveOnRow;
        readonly Slider _maxRpm = new Slider(300) { Min = 0, Max = 6500, Step = 100, Display = v => v <= 0 ? "No limit" : v.ToString("N0") + " rpm" };
        Row _maxRpmRow, _nameRow, _handsRow, _fixedRow, _retryRow;
        Flow _pickFlow; FlatButton _bNew, _bDup, _bDel;
        readonly ToggleSwitch _advanced = new ToggleSwitch();
        Card _sensorCard, _limiterCard, _graphCard;
        readonly Slider _hyst = new Slider(300) { Min = 0, Max = 10, Step = 0.5, Format = "0.0", Unit = " °C" };
        readonly Slider _up = new Slider(300) { Min = 0, Max = 60, Step = 1, Unit = " s" };
        readonly Slider _down = new Slider(300) { Min = 5, Max = 300, Step = 5, Unit = " s" };
        readonly Slider _dwell = new Slider(300) { Min = 0, Max = 600, Step = 10, Display = v => v >= 60 ? (v / 60).ToString("0.#") + " min" : v.ToString("0") + " s" };
        readonly Slider _smooth = new Slider(300) { Min = 0, Max = 30, Step = 1, Unit = " s" };
        readonly ToggleSwitch _limiting = new ToggleSwitch();
        readonly DarkCombo _boostMode = new DarkCombo(300);
        readonly Slider _boostLoad = new Slider(300) { Min = 20, Max = 90, Step = 5, Unit = " %" };
        readonly Slider _boostAfter = new Slider(300) { Min = 2, Max = 60, Step = 1, Unit = " s" };
        readonly ToggleSwitch _boostBatt = new ToggleSwitch();
        readonly Slider _minPerf = new Slider(300) { Min = 20, Max = 100, Step = 5, Unit = " %" };
        readonly Card _engineCard, _rulesCard;
        readonly List<Control> _curveOnly = new List<Control>();

        // rules
        readonly Stack _rulesList = new Stack { AutoHeight = true, MaxContentWidth = 0, Inner = new Padding(0), Gap = 0, BackColor = Theme.Card };

        // fan
        readonly ValueLabel _fanNow = new ValueLabel(520);
        readonly ToggleSwitch _fanAlways = new ToggleSwitch();
        Row _fanAlwaysRow;
        bool _fanQueried;
        readonly ToggleSwitch _fsOn = new ToggleSwitch();
        readonly Slider _fsOnC = new Slider(300) { Min = 40, Max = 80, Step = 1, Unit = " °C" };
        readonly Slider _fsOffC = new Slider(300) { Min = 35, Max = 75, Step = 1, Unit = " °C" };
        readonly Slider _fsUp = new Slider(300) { Min = 20, Max = 500, Step = 10, Unit = " rpm/s" };
        readonly Slider _fsDown = new Slider(300) { Min = 10, Max = 300, Step = 10, Unit = " rpm/s" };
        readonly Slider _fsHbCpu = new Slider(300) { Min = 70, Max = 98, Step = 1, Unit = " °C" };
        readonly Slider _fsHbInt = new Slider(300) { Min = 50, Max = 80, Step = 1, Unit = " °C" };
        readonly ValueLabel _fsInfo = new ValueLabel(520);
        readonly CurveEditor _fsCurveEd = new CurveEditor();
        readonly CurveEditor _fsPowerEd = new CurveEditor();
        readonly CurveEditor _fsIntEd = new CurveEditor();
        readonly List<Row> _fsAdv = new List<Row>();

        // sensors / limiter
        readonly Stack _sensorList = new Stack { AutoHeight = true, MaxContentWidth = 0, Inner = new Padding(0), Gap = 0, BackColor = Theme.Card };
        readonly ToggleSwitch _lEpp = new ToggleSwitch(), _lBoost = new ToggleSwitch(), _lMax = new ToggleSwitch();
        readonly Slider _lBoostOff = new Slider(300) { Min = 40, Max = 95, Step = 5, Unit = " %" };
        readonly Slider _lStep = new Slider(300) { Min = 5, Max = 25, Step = 5, Unit = " %" };
        readonly Slider _lRetry = new Slider(300) { Min = 1, Max = 15, Step = 1, Unit = " min" };
        readonly TimeGraph _graph = new TimeGraph { Height = Theme.S(240) };
        DateTime _lastHist;
        HashSet<string> _sensorKeys = new HashSet<string>();

        public SystemPage(Model m)
        {
            _m = m;
            AutoScroll = true;
            Add(new Lbl("System control", Theme.H1.Value, Theme.Text, Theme.S(40)));
            Add(new Lbl("Pick how the fan and CPU behave. The firmware owns the fan, so HP Control steers it with HP's thermal modes, a fan speed limit and, if needed, the CPU power budget.", Theme.Small.Value, Theme.Sub, Theme.S(22)));
            var vc = Add(new Card());
            vc.Add(new Row("Show advanced settings", _advanced, "Curve editor, timing, temperature sensors and the CPU power limiter.") { Separator = false });
            _advanced.CheckedChanged += (s, e) => { if (_loading || _m.Settings == null) return; _m.Settings.General.AdvancedMode = _advanced.Checked; _m.MarkDirty(); NativeApp.Frozen(this, UpdateVis); };

            // ---------------- profile editor ----------------
            _engineCard = Add(new Card("Profiles", "Pick a profile to see or change it. Which profile is used plugged in and on battery is set on the Home page. Changes save automatically."));
            var pf = _pickFlow = _engineCard.Add(new Flow(46));
            pf.Controls.Add(_profile);
            var bNew = _bNew = new FlatButton("New"); var bDup = _bDup = new FlatButton("Duplicate"); var bDel = _bDel = new FlatButton("Delete");
            pf.Controls.AddRange(new Control[] { bNew, bDup, bDel });
            bNew.Click += (s, e) => AddProfile(new Profile { Name = Unique("Custom"), Ladder = { new LadderStep(0, "Quiet"), new LadderStep(65, "Balanced"), new LadderStep(85, "Performance") } });
            bDup.Click += (s, e) => { if (_p != null) { var c = _p.Clone(); c.Name = Unique(_p.Name + " copy"); c.HandsOff = false; AddProfile(c); } };
            bDel.Click += (s, e) => DeleteProfile();
            _profile.SelectedIndexChanged += (s, e) => { if (!_loading) LoadProfile(_m.Settings.FindProfile(_profile.SelectedItem as string)); };
            _nameRow = _engineCard.Add(new Row("Name", _name));
            _name.Leave += (s, e) => RenameProfile();
            _name.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RenameProfile(); e.SuppressKeyPress = true; } };
            _engineCard.Add(_desc);
            _maxRpmRow = _engineCard.Add(new Row("Max fan speed", _maxRpm, "If the fan goes faster than this, HP Control switches to Quiet mode, then trims CPU power until it's back under. Leave at No limit for full performance."));
            _handsRow = _engineCard.Add(new Row("Hand control to HP firmware", _handsOff, "On = just set the mode below and let HP do the rest (no curves)."));
            _handsOff.CheckedChanged += (s, e) => { if (_loading || _p == null) return; _p.HandsOff = _handsOff.Checked; Changed(); UpdateVis(); };
            var fixedRow = _fixedRow = _engineCard.Add(new Row("HP mode", _fixedMode));
            foreach (var mm in Settings.ModeNames) _fixedMode.Items.Add(Settings.ModeLabel(mm));
            _fixedMode.SelectedIndexChanged += (s, e) => { if (_loading || _p == null || _fixedMode.SelectedIndex < 0) return; _p.FixedMode = Settings.ModeNames[_fixedMode.SelectedIndex]; Changed(); };
            _curveOnly.Add(fixedRow);

            var tf = _engineCard.Add(new Flow(48));
            _tabs.Set(new[] { "ladder", "fan", "budget", "load" }, new[] { "Mode ladder", "Fan limit curve", "CPU budget", "Load boost" });
            _tabs.Selected = "ladder";
            tf.Controls.Add(_tabs);
            _tabs.SelectionChanged += (s, e) => UpdateTab();
            _ed.Height = Theme.S(320);
            _engineCard.Add(_ed);
            _load.Height = Theme.S(260); _load.XMin = 0; _load.XMax = 100; _load.XUnit = "%";
            _engineCard.Add(_load);
            _engineCard.Add(_hint);
            _curveOnRow = _engineCard.Add(new Row("Use this curve", _curveOn));
            _curveOn.CheckedChanged += (s, e) => { if (_loading) return; var c = CurrentCurve(); if (c != null) { c.Enabled = _curveOn.Checked; _ed.Invalidate(); _load.Invalidate(); Changed(); } };
            _ed.Changed += (s, e) => Changed();
            _load.Changed += (s, e) => Changed();

            _engineCard.Add(new Row("Hysteresis", _hyst, "How far the temperature must drop below a step before the ladder steps back down."));
            _engineCard.Add(new Row("Step-up delay", _up, "Temperature must stay above a step this long before switching up."));
            _engineCard.Add(new Row("Step-down delay", _down, "Temperature must stay below (step − hysteresis) this long before switching down. Long = no fan hunting."));
            _engineCard.Add(new Row("Minimum time between switches", _dwell));
            _engineCard.Add(new Row("Temperature smoothing", _smooth, "Averaging time constant. Spikes shorter than this are ignored."));
            _boostMode.Items.Add("(don't switch)"); foreach (var mm in Settings.ModeNames) _boostMode.Items.Add(Settings.ModeLabel(mm));
            _engineCard.Add(new Row("When busy, switch to", _boostMode, "Busy = CPU usage or chip power (CPU + graphics) stays high, e.g. a game, a render or a compile. Overrides the ladder until the load has been gone for a minute."));
            _engineCard.Add(new Row("Busy means load above", _boostLoad, "10-second average. Chip power counts too: ~6 W = 0 %, ~16 W = 100 %."));
            _engineCard.Add(new Row("…for at least", _boostAfter));
            _engineCard.Add(new Row("Also on battery", _boostBatt, "Battery smart uses this to go to Balanced for heavy jobs. For other profiles it's off by default: it mostly costs battery."));
            _boostMode.SelectedIndexChanged += (s, e) => { if (_loading || _p == null || _boostMode.SelectedIndex < 0) return; _p.BoostMode = _boostMode.SelectedIndex == 0 ? "" : Settings.ModeNames[_boostMode.SelectedIndex - 1]; Changed(); };
            Bind(_boostLoad, v => _p.BoostLoadPct = v); Bind(_boostAfter, v => _p.BoostAfterSec = (int)v);
            _boostBatt.CheckedChanged += (s, e) => { if (!_loading && _p != null) { _p.BoostOnBattery = _boostBatt.Checked; Changed(); } };
            _engineCard.Add(new Row("Allow CPU power limiting", _limiting, "Lets the fan limiter trim CPU power after forcing Quiet mode."));
            _engineCard.Add(new Row("Lowest CPU budget", _minPerf) { Separator = false });
            Bind(_maxRpm, v => _p.FanMaxRpm = (int)v); Bind(_hyst, v => _p.HysteresisC = v); Bind(_up, v => _p.UpDelaySec = (int)v);
            Bind(_down, v => _p.DownDelaySec = (int)v); Bind(_dwell, v => _p.MinDwellSec = (int)v); Bind(_smooth, v => _p.SmoothingSec = v); Bind(_minPerf, v => _p.MinPerfPct = (int)v);
            _limiting.CheckedChanged += (s, e) => { if (!_loading && _p != null) { _p.PowerLimiting = _limiting.Checked; Changed(); } };

            // ---------------- rules ----------------
            _rulesCard = Add(new Card("Rules", "Context overrides, checked top to bottom. The first matching rule that names a profile wins. Fan speed and CPU limits from every matching rule combine (the lowest wins)."));
            _rulesCard.Add(_rulesList);
            var rf = _rulesCard.Add(new Flow(50));
            var addRule = new FlatButton("Add rule", true);
            addRule.Click += (s, e) => { var r = new Rule { Name = "New rule" }; if (RuleDialog.Edit(this, r, _m.Settings)) { _m.Settings.Rules.Add(r); _m.MarkDirty(); BuildRules(); } };
            rf.Controls.Add(addRule);

            // ---------------- fan ----------------
            var fc = Add(new Card("Fan", "Fan speed is read straight from the embedded controller, the same reading HP's own diagnostics use."));
            fc.Add(new Row("Fan speed", _fanNow));
            fc.Add(new Row("HP Control drives the fan (quiet curve)", _fsOn, "The fan controller runs the fan whenever the air inside the laptop is above about 33 °C (nearly always) and jumps to loud speeds in Performance. With this on, HP Control drives the fan itself: off while the CPU is cool and the laptop idle, then a smooth curve by temperature, with a gentle start when a heavy job begins, and every change ramped so it never jumps. The HP mode then only sets CPU power, not fan noise. The fan goes back to the fan controller if the CPU gets very hot or anything goes wrong."));
            _fsAdv.Add(fc.Add(new Row("Right now", _fsInfo)));
            _fsAdv.Add(fc.Add(new Row("Fan comes on when the CPU averages", _fsOnC, "30-second average, so short bursts don't wake it. A heavy job also starts it early (chip power curve below).")));
            _fsAdv.Add(fc.Add(new Row("…and goes off again below", _fsOffC, "15-second average, once the load has gone (after at least 30 s on). It then winds down quickly and stops.")));
            _fsCurveEd.Height = Theme.S(240); _fsCurveEd.XMin = 40; _fsCurveEd.XMax = 100; _fsCurveEd.XUnit = "°C";
            _fsPowerEd.Height = Theme.S(200); _fsPowerEd.XMin = 0; _fsPowerEd.XMax = 40; _fsPowerEd.XUnit = "W";
            _fsIntEd.Height = Theme.S(200); _fsIntEd.XMin = 40; _fsIntEd.XMax = 75; _fsIntEd.XUnit = "°C";
            _fsAdv.Add(fc.Add(new Row("Fan speed by CPU temperature", new Lbl("Drag the points. 1,950 rpm is the lowest speed the fan has (other than off).", Theme.Small.Value, Theme.Sub, Theme.S(20)) { Width = Theme.S(520) })));
            fc.Add(_fsCurveEd);
            _fsAdv.Add(fc.Add(new Row("Heads-up from chip power", new Lbl("Fan speed by chip power: starts the fan gently once a heavy job has run for 20 s, before it heats things up.", Theme.Small.Value, Theme.Sub, Theme.S(20)) { Width = Theme.S(520) })));
            fc.Add(_fsPowerEd);
            _fsAdv.Add(fc.Add(new Row("Inside the laptop", new Lbl("Fan speed by the internal sensor (1-minute average) while the fan is on. It only keeps the fan on by itself above 62 °C. Reads ~40 °C at idle.", Theme.Small.Value, Theme.Sub, Theme.S(20)) { Width = Theme.S(520) })));
            fc.Add(_fsIntEd);
            _fsAdv.Add(fc.Add(new Row("Speed up by at most", _fsUp)));
            _fsAdv.Add(fc.Add(new Row("Slow down by at most", _fsDown)));
            _fsAdv.Add(fc.Add(new Row("Give the fan back to the fan controller at CPU", _fsHbCpu, "Safety: the fan controller then runs the fan as usual (full speed when hot).")));
            _fsAdv.Add(fc.Add(new Row("…or internal sensor average", _fsHbInt, "1-minute average of the internal sensor above.")));
            BindT(_fsOn, v => { _m.Settings.FanStop.Enabled = v; UpdateVis(); });
            BindS(_fsOnC, v => { _m.Settings.FanStop.OnAboveC = v; ShadeOff(_m.Settings.FanStop); }); BindS(_fsOffC, v => _m.Settings.FanStop.OffBelowC = v);
            BindS(_fsUp, v => _m.Settings.FanStop.RampUpRpmPerSec = v); BindS(_fsDown, v => _m.Settings.FanStop.RampDownRpmPerSec = v);
            BindS(_fsHbCpu, v => _m.Settings.FanStop.HandBackCpuC = v); BindS(_fsHbInt, v => _m.Settings.FanStop.HandBackInternalC = v);
            _fsCurveEd.Changed += (s, e) => _m.MarkDirty();
            _fsPowerEd.Changed += (s, e) => _m.MarkDirty();
            _fsIntEd.Changed += (s, e) => _m.MarkDirty();
            _fanAlwaysRow = fc.Add(new Row("Fan always on", _fanAlways, "HP BIOS setting. Keeps the fan turning slowly instead of stopping and starting, which avoids the on/off cycling at light loads. Uses slightly more power. It may only take effect after a restart."));
            _fanAlwaysRow.Visible = false;   // shown once the BIOS says it's supported
            _fanAlways.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                bool want = _fanAlways.Checked;
                var r = _m.Call("setFanAlwaysOn", Model.A("on", want)) as Dictionary<string, object>;
                bool now = r != null ? r.Bool("alwaysOn") : !want;
                if (now != want) { _loading = true; _fanAlways.Checked = now; _loading = false; }
                else _m.ShowToast("Fan always on " + (now ? "enabled" : "disabled") + ". If you don't hear a change, restart the laptop.");
            };
            _retryRow = fc.Add(new Row("When the fan limit forces Quiet, retry the louder mode after", _lRetry, "Sooner only if the limit rises enough (it gets hotter) or the load goes away. Stops the fan pumping up and down.") { Separator = false });

            // ---------------- sensors ----------------
            var sc = _sensorCard = Add(new Card("Temperature sensors", "Pick which sensors drive the curves (the hottest selected one is used)."));
            sc.Add(_sensorList);

            // ---------------- limiter ----------------
            var lc = _limiterCard = Add(new Card("CPU power limiter", "Only touches the active power plan while a budget below 100 % is in force, and restores your exact values afterwards (or after a crash/reboot). If you change one of these yourself meanwhile, your value is kept."));
            lc.Add(new Row("Adjust energy-performance preference (EPP)", _lEpp));
            lc.Add(new Row("Adjust turbo boost mode", _lBoost));
            lc.Add(new Row("Adjust maximum processor state", _lMax, "Used only at low budgets."));
            lc.Add(new Row("Turn turbo off below budget", _lBoostOff));
            lc.Add(new Row("Limiter step size", _lStep));
            var bRestore = new FlatButton("Restore power plan values now");
            bRestore.Click += (s, e) => _m.Call("restorePower");
            lc.Add(new Row("Emergency restore", bRestore) { Separator = false });
            BindT(_lEpp, v => _m.Settings.PowerLimit.UseEpp = v); BindT(_lBoost, v => _m.Settings.PowerLimit.UseBoost = v); BindT(_lMax, v => _m.Settings.PowerLimit.UseMaxState = v);
            BindS(_lBoostOff, v => _m.Settings.PowerLimit.BoostOffBelow = (int)v); BindS(_lStep, v => _m.Settings.PowerLimit.StepPct = (int)v); BindS(_lRetry, v => _m.Settings.PowerLimit.RetrySec = (int)v * 60);

            var hc = _graphCard = Add(new Card("Last 30 minutes", "Use the heat test to see how your curves react: it loads the CPU, then you can watch the ladder step up and back down."));
            hc.Add(_graph);
            _heatLen = new Segmented(240); _heatLen.Set(new[] { "60", "180", "300" }, new[] { "1 min", "3 min", "5 min" }); _heatLen.Selected = "180";
            hc.Add(new Row("Heat test length", _heatLen, "On battery or in Quiet the CPU is power-limited, so it can take a few minutes to get hot."));
            _heatBtn = new FlatButton("Start heat test", true, 220);
            _heatBtn.Click += (s, e) => ToggleHeat();
            hc.Add(new Row("Heat test", _heatBtn, "Loads all but one core at low priority, so your apps stay responsive. Press again to stop.") { Separator = false });
            _heatTimer.Tick += (s, e) =>
            {
                int left = (int)(_heatEnd - DateTime.UtcNow).TotalSeconds;
                if (left <= 0) StopHeat(); else _heatBtn.Text = "Stop heat test (" + left + " s)";
            };

            m.SettingsReloaded += () => { if (Visible) NativeApp.Frozen(this, LoadAll); };
        }

        // ------------------------------------------------------------ heat test
        FlatButton _heatBtn; Segmented _heatLen;
        readonly Timer _heatTimer = new Timer { Interval = 1000 };
        DateTime _heatEnd;
        volatile bool _heating;

        void ToggleHeat()
        {
            if (_heating) { StopHeat(); return; }
            int secs; if (!int.TryParse(_heatLen.Selected, out secs)) secs = 180;
            _heating = true; _heatEnd = DateTime.UtcNow.AddSeconds(secs);
            for (int i = 0; i < Math.Max(1, Environment.ProcessorCount - 1); i++)
                new System.Threading.Thread(() => { double x = 0; while (_heating && DateTime.UtcNow < _heatEnd) for (int k = 1; k < 200000; k++) x += Math.Sqrt(k) * Math.Sin(k); })
                { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
            _heatTimer.Start();
            _heatBtn.Text = "Stop heat test (" + secs + " s)";
        }

        void StopHeat() { _heating = false; _heatTimer.Stop(); _heatBtn.Text = "Start heat test"; }

        // ------------------------------------------------------------ binding helpers
        void Bind(Slider s, Action<double> set) => s.ValueCommitted += (o, e) => { if (_loading || _p == null) return; set(s.Value); Changed(); };
        void BindS(Slider s, Action<double> set) => s.ValueCommitted += (o, e) => { if (_loading) return; set(s.Value); _m.MarkDirty(); };
        void BindT(ToggleSwitch t, Action<bool> set) => t.CheckedChanged += (o, e) => { if (_loading) return; set(t.Checked); _m.MarkDirty(); };
        void Changed() { _m.MarkDirty(); }

        string Unique(string b) { var n = b; int i = 2; while (_m.Settings.FindProfile(n) != null) n = b + " " + i++; return n; }

        void AddProfile(Profile p) { _m.Settings.Profiles.Add(p); _m.MarkDirty(); FillProfiles(p.Name); LoadProfile(p); }

        void DeleteProfile()
        {
            if (_p == null || _m.Settings.Profiles.Count <= 1) return;
            if (MessageBox.Show(this, "Delete profile \"" + _p.Name + "\"?", "HP Control", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _m.Settings.Profiles.Remove(_p);
            var first = _m.Settings.Profiles[0].Name;
            if (_m.Settings.ProfileAC == _p.Name) _m.Settings.ProfileAC = first;
            if (_m.Settings.ProfileDC == _p.Name) _m.Settings.ProfileDC = first;
            foreach (var r in _m.Settings.Rules.Where(r => r.Profile == _p.Name)) r.Profile = "";
            _m.MarkDirty(); FillProfiles(first); LoadProfile(_m.Settings.Profiles[0]);
        }

        void RenameProfile()
        {
            if (_loading || _p == null) return;
            var n = _name.Text.Trim();
            if (n.Length == 0 || n == _p.Name || _m.Settings.FindProfile(n) != null) { _name.Text = _p.Name; return; }
            var old = _p.Name; _p.Name = n;
            if (_m.Settings.ProfileAC == old) _m.Settings.ProfileAC = n;
            if (_m.Settings.ProfileDC == old) _m.Settings.ProfileDC = n;
            foreach (var r in _m.Settings.Rules.Where(r => r.Profile == old)) r.Profile = n;
            _m.MarkDirty(); FillProfiles(n);
        }

        void FillProfiles(string select)
        {
            _loading = true;
            _profile.Items.Clear();
            foreach (var p in _m.Settings.Profiles) _profile.Items.Add(p.Name);
            _profile.SelectedItem = select;
            _loading = false;
        }

        /// <summary>Shades the part of the CPU curve where the fan is normally off (below the switch-on temperature).</summary>
        void ShadeOff(FanStopSettings f)
        {
            _fsCurveEd.ShadeToX = f.OnAboveC;
            _fsCurveEd.ShadeLabel = "fan off until the CPU averages " + f.OnAboveC.ToString("0") + " °C";
            _fsCurveEd.Invalidate();
        }

        // ------------------------------------------------------------ load
        // show what's loaded right away (rebuilding only if the settings changed since), then check for fresh
        // settings in the background; a change re-runs LoadAll through SettingsReloaded
        int _shownVersion = -1;
        public void OnShow()
        {
            if (_shownVersion != _m.Version) LoadAll();
            _m.ReloadAsync(this);
            _lastHist = DateTime.MinValue;
        }

        void LoadAll()
        {
            var s = _m.Settings; if (s == null) return;
            _shownVersion = _m.Version;
            _loading = true;
            var active = _p != null ? s.FindProfile(_p.Name) : null;
            if (active == null) active = s.FindProfile(_m.Tel.ActiveProfile) ?? s.FindProfile(s.ProfileAC) ?? s.Profiles.FirstOrDefault();
            FillProfiles(active?.Name);
            _loading = true;
            LoadProfile(active);
            _loading = true;
            BuildRules();
            _advanced.Checked = s.General.AdvancedMode;
            if (!_fanQueried)
            {
                // reads a BIOS setting (slow): in the background
                _fanQueried = true;
                _m.CallAsync(this, "getFan", null, r =>
                {
                    var fan = r as Dictionary<string, object>;
                    bool was = _loading; _loading = true;
                    _fanAlwaysRow.Visible = fan != null && fan.Bool("alwaysOnSupported");
                    if (fan != null) _fanAlways.Checked = fan.Bool("alwaysOn");
                    _loading = was;
                    if (fan == null) _fanQueried = false;   // try again next time
                    PerformLayout();
                });
            }
            var f = s.FanStop ?? (s.FanStop = new FanStopSettings());
            _fsOn.Checked = f.Enabled; _fsOnC.Value = f.OnAboveC; _fsOffC.Value = f.OffBelowC; _fsUp.Value = f.RampUpRpmPerSec; _fsDown.Value = f.RampDownRpmPerSec;
            _fsHbCpu.Value = f.HandBackCpuC; _fsHbInt.Value = f.HandBackInternalC;
            if (f.Curve == null) f.Curve = new FanStopSettings().Curve;
            if (f.PowerRamp == null) f.PowerRamp = new FanStopSettings().PowerRamp;
            _fsCurveEd.Curves.Clear(); _fsCurveEd.Curves.Add(new CurveEditor.Series { Name = "Fan speed", Color = Theme.Accent, Curve = f.Curve, YMin = 0, YMax = 6000, Unit = "rpm", Format = "0" });
            _fsPowerEd.Curves.Clear(); _fsPowerEd.Curves.Add(new CurveEditor.Series { Name = "Heads-up", Color = Theme.Warn, Curve = f.PowerRamp, YMin = 0, YMax = 6000, Unit = "rpm", Format = "0" });
            if (f.InternalCurve == null) f.InternalCurve = new FanStopSettings().InternalCurve;
            _fsIntEd.Curves.Clear(); _fsIntEd.Curves.Add(new CurveEditor.Series { Name = "Fan speed", Color = Theme.Accent, Curve = f.InternalCurve, YMin = 0, YMax = 6000, Unit = "rpm", Format = "0" });
            _fsCurveEd.Active = 0; _fsPowerEd.Active = 0; _fsIntEd.Active = 0;
            ShadeOff(f);   // the editor only draws (and lets you drag) the active curve
            _fsCurveEd.Invalidate(); _fsPowerEd.Invalidate(); _fsIntEd.Invalidate();
            var l = s.PowerLimit;
            _lEpp.Checked = l.UseEpp; _lBoost.Checked = l.UseBoost; _lMax.Checked = l.UseMaxState; _lBoostOff.Value = l.BoostOffBelow; _lStep.Value = l.StepPct; _lRetry.Value = Math.Max(1, l.RetrySec / 60);
            _sensorKeys.Clear(); BuildSensors();
            foreach (Control c in Controls) c.Invalidate(true);
            _loading = false;
            PerformLayout();
        }

        void LoadProfile(Profile p)
        {
            _p = p;
            if (p == null) return;
            _loading = true;
            _name.Text = p.Name;
            _desc.Text = p.Description;
            _handsOff.Checked = p.HandsOff;
            _fixedMode.SelectedIndex = Math.Max(0, Array.IndexOf(Settings.ModeNames, p.FixedMode));
            _ed.Curves.Clear();
            _ed.Curves.Add(new CurveEditor.Series { Name = "Fan limit", Color = Color.FromArgb(120, 230, 160), Curve = p.RpmCap, YMin = 0, YMax = 8000, Unit = "rpm", Format = "N0" });
            _ed.Curves.Add(new CurveEditor.Series { Name = "CPU budget", Color = Color.FromArgb(170, 150, 255), Curve = p.PerfCap, YMin = 0, YMax = 100, Unit = "%" });
            _ed.Ladder = p.Ladder;
            _ed.HysteresisC = p.HysteresisC;
            _load.Curves.Clear();
            _load.Curves.Add(new CurveEditor.Series { Name = "Load boost", Color = Theme.Warn, Curve = p.LoadAnticipation, YMin = 0, YMax = 24, Unit = "+°C", Format = "0" });
            _load.Active = 0;
            _maxRpm.Value = p.FanMaxRpm; _hyst.Value = p.HysteresisC; _up.Value = p.UpDelaySec; _down.Value = p.DownDelaySec; _dwell.Value = p.MinDwellSec;
            _smooth.Value = p.SmoothingSec; _limiting.Checked = p.PowerLimiting; _minPerf.Value = p.MinPerfPct;
            _boostMode.SelectedIndex = string.IsNullOrEmpty(p.BoostMode) ? 0 : Math.Max(0, Array.IndexOf(Settings.ModeNames, p.BoostMode) + 1);
            _boostLoad.Value = p.BoostLoadPct; _boostAfter.Value = p.BoostAfterSec; _boostBatt.Checked = p.BoostOnBattery;
            UpdateTab();
            _loading = false;
            UpdateVis();
            foreach (Control c in _engineCard.Controls) c.Invalidate(true);
        }

        Curve CurrentCurve()
        {
            if (_p == null) return null;
            switch (_tabs.Selected) { case "budget": return _p.PerfCap; case "load": return _p.LoadAnticipation; case "fan": return _p.RpmCap; }
            return null;
        }

        void UpdateTab()
        {
            bool l = _tabs.Selected == "load";
            _ed.Visible = !l; _load.Visible = l;
            _ed.Active = _tabs.Selected == "fan" ? 0 : _tabs.Selected == "budget" ? 1 : -1;
            switch (_tabs.Selected)
            {
                case "ladder": _hint.Text = "Which HP mode to use at each temperature. Drag a boundary to move it. Right-click a band to change its mode, add a step or remove one. The hatched strip is the hysteresis zone."; break;
                case "fan": _hint.Text = "Optional: a fan speed limit that changes with temperature (for example low when cool, higher when hot). It combines with Max fan speed above; the lower one wins. Click to add a point, drag to move, right-click to delete."; break;
                case "budget": _hint.Text = "Pre-emptive CPU power cap by temperature (100 % = untouched). Keeps heat, and with it fan speed, down before the firmware ramps the fan."; break;
                case "load": _hint.Text = "Adds virtual degrees when the laptop is busy, so the ladder reacts to a heavy job before the temperature catches up. X axis = load %: CPU usage or chip power (6 W = 0 %, 16 W = 100 %), whichever is higher, so games that mostly use the graphics count too."; break;
            }
            var c = CurrentCurve();
            _curveOnRow.Visible = c != null;
            if (c != null) { bool was = _loading; _loading = true; _curveOn.Checked = c.Enabled; _loading = was; }
            _ed.Invalidate(); _load.Invalidate();
            _engineCard.PerformLayout(); PerformLayout();
        }

        void UpdateVis()
        {
            bool adv = _m.Settings?.General.AdvancedMode == true;
            bool hands = _p != null && _p.HandsOff;
            foreach (Control c in _engineCard.Controls)
            {
                if (c == _pickFlow || c == _desc) { c.Visible = true; continue; }
                if (c == _maxRpmRow) { c.Visible = !hands; continue; }
                if (!adv) { c.Visible = false; continue; }
                if (c == _nameRow || c == _handsRow) { c.Visible = true; continue; }
                if (c == _fixedRow) { c.Visible = hands; continue; }
                if (c == _load) { c.Visible = !hands && _tabs.Selected == "load"; continue; }
                if (c == _ed) { c.Visible = !hands && _tabs.Selected != "load"; continue; }
                if (c == _curveOnRow) { c.Visible = !hands && CurrentCurve() != null; continue; }
                c.Visible = !hands;
            }
            _bNew.Visible = _bDup.Visible = _bDel.Visible = adv;
            // rules, the graph and the heat test stay in Simple mode; the rest is advanced
            _sensorCard.Visible = _limiterCard.Visible = _retryRow.Visible = adv;
            bool fs = _m.Settings?.FanStop.Enabled == true;
            foreach (var r in _fsAdv) r.Visible = adv && fs;
            _fsCurveEd.Visible = _fsPowerEd.Visible = _fsIntEd.Visible = adv && fs;
            _pickFlow.PerformLayout(); _engineCard.PerformLayout(); PerformLayout();
        }

        void BuildRules()
        {
            _rulesList.SuspendLayout();
            foreach (Control c in _rulesList.Controls.Cast<Control>().ToList()) { _rulesList.Controls.Remove(c); c.Dispose(); }
            var rules = _m.Settings.Rules;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                var t = new ToggleSwitch { Checked = r.Enabled };
                var edit = new FlatButton("Edit", false, 64);
                var up = new FlatButton("↑", false, 40);
                var fl = new Flow(40) { Width = Theme.S(220) };
                fl.Controls.AddRange(new Control[] { t, up, edit });
                var row = new Row(r.Name, fl, Describe(r));
                t.CheckedChanged += (s, e) => { r.Enabled = t.Checked; _m.MarkDirty(); };
                edit.Click += (s, e) =>
                {
                    var copy = Json.Deserialize<Rule>(Json.Serialize(r));
                    var res = RuleDialog.Edit(this, copy, _m.Settings, true);
                    if (res) { int idx = rules.IndexOf(r); if (RuleDialog.LastDeleted) rules.RemoveAt(idx); else rules[idx] = copy; _m.MarkDirty(); BuildRules(); }
                };
                up.Click += (s, e) => { int idx = rules.IndexOf(r); if (idx > 0) { rules.RemoveAt(idx); rules.Insert(idx - 1, r); _m.MarkDirty(); BuildRules(); } };
                up.Enabled = i > 0;
                _rulesList.Controls.Add(row);
            }
            if (rules.Count == 0) _rulesList.Controls.Add(new Lbl("No rules yet.", Theme.Small.Value));
            _rulesList.ResumeLayout(); _rulesList.PerformLayout(); _rulesCard.PerformLayout(); PerformLayout();
        }

        public static string Describe(Rule r)
        {
            string when;
            switch (r.When)
            {
                case "AC": when = "When plugged in"; break;
                case "Battery": when = "When on battery"; break;
                case "BatteryBelow": when = "Battery below " + r.Value + " %"; break;
                case "QuietHours": when = "Between " + r.From + " and " + r.To; break;
                case "Meeting": when = "Microphone or camera in use"; break;
                case "App": when = "When " + (string.IsNullOrWhiteSpace(r.Apps) ? "(no apps set)" : r.Apps) + " is in front"; break;
                case "Game": when = "A game in front and busy" + (string.IsNullOrWhiteSpace(r.Apps) ? "" : " (also " + r.Apps + ")"); break;
                case "Fullscreen": when = "Fullscreen app / game / presentation"; break;
                case "Tablet": when = "Tablet or tent posture"; break;
                case "Idle": when = "Idle for " + r.Value + " min"; break;
                default: when = "Always"; break;
            }
            var then = new List<string>();
            if (!string.IsNullOrEmpty(r.Profile)) then.Add("use \"" + r.Profile + "\"");
            if (r.PerfCapPct > 0) then.Add("CPU ≤ " + r.PerfCapPct + " %");
            if (r.FanRpmMax > 0) then.Add("fan ≤ " + r.FanRpmMax + " rpm");
            if (r.PluggedInOnly) when += ", plugged in";
            return when + " → " + (then.Count == 0 ? "nothing set" : string.Join(", ", then));
        }

        void BuildSensors()
        {
            var t = _m.Tel; if (t?.Temps == null) return;
            var keys = new HashSet<string>(t.Temps.Keys);
            if (keys.SetEquals(_sensorKeys)) return;
            _sensorKeys = keys;
            _sensorList.SuspendLayout();
            foreach (Control c in _sensorList.Controls.Cast<Control>().ToList()) { _sensorList.Controls.Remove(c); c.Dispose(); }
            var sel = (_m.Settings.General.ControlSensors ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (var k in t.Temps.Keys.OrderBy(x => x))
            {
                var key = k;
                var tg = new ToggleSwitch { Checked = sel.Contains(k) };
                string label = _m.Settings.General.SensorLabels != null && _m.Settings.General.SensorLabels.TryGetValue(k, out var l) ? l : k;
                var row = new Row(label + "  (" + k + ")", tg, "") { Tag = k };
                tg.CheckedChanged += (s, e) =>
                {
                    var list = (_m.Settings.General.ControlSensors ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (tg.Checked && !list.Contains(key)) list.Add(key);
                    if (!tg.Checked) list.Remove(key);
                    if (list.Count == 0) { list.Add(key); tg.Checked = true; }
                    _m.Settings.General.ControlSensors = string.Join(",", list); _m.MarkDirty();
                };
                _sensorList.Controls.Add(row);
            }
            _sensorList.ResumeLayout(); _sensorList.PerformLayout(); PerformLayout();
        }

        public void OnTelemetry(Telemetry t, List<LogEntry> events)
        {
            if (t == null) return;
            _ed.MarkerX = t.SmoothedTemp; _ed.MarkerY = t.FanRpm >= 0 ? t.FanRpm : double.NaN; _ed.MarkerUnit = "rpm"; _ed.Invalidate();
            _load.MarkerX = t.LoadPct; _load.Invalidate();
            if (t.FanRpm < 0) _fanNow.Text = "no fan sensor found";
            else _fanNow.Text = (t.FanRpm == 0 ? "stopped" : t.FanRpm.ToString("N0") + " rpm") +
                    (t.FanTargetRpm >= 0 ? " · target " + t.FanTargetRpm.ToString("N0") : "") +
                    (t.FanLimitRpm > 0 ? " · limit " + t.FanLimitRpm.ToString("N0") : "") +
                    (!string.IsNullOrEmpty(t.FanStop) && t.FanStop != "Fan off" && t.FanStop != "Fan on (needed)" ? " · " + t.FanStop : "");
            _fsInfo.Text = string.IsNullOrEmpty(t.FanStopInfo) ? "-" : t.FanStopInfo;
            _fsCurveEd.MarkerX = t.Temps != null && t.Temps.TryGetValue("cpu", out var ct) ? ct : double.NaN; _fsCurveEd.MarkerY = t.FanRpm >= 0 ? t.FanRpm : double.NaN; _fsCurveEd.MarkerUnit = "rpm"; _fsCurveEd.Invalidate();
            _fsPowerEd.MarkerX = t.PackageW; _fsPowerEd.Invalidate();
            _fsIntEd.MarkerX = t.Temps != null && t.Temps.TryGetValue("case", out var it) ? it : double.NaN; _fsIntEd.Invalidate();
            if (_m.Settings != null)
            {
                BuildSensors();
                foreach (Row r in _sensorList.Controls.OfType<Row>())
                    if (r.Tag is string k && t.Temps.TryGetValue(k, out var v)) r.SetDesc(v.ToString("0.0") + " °C");
            }
            if ((DateTime.Now - _lastHist).TotalSeconds >= 5) { _lastHist = DateTime.Now; HomePage.LoadHistory(_m, _graph, 30); }
        }
    }

    /// <summary>Modal editor for one rule.</summary>
    static class RuleDialog
    {
        public static bool LastDeleted;
        static readonly string[] Whens = { "Always", "AC", "Battery", "BatteryBelow", "QuietHours", "Meeting", "App", "Game", "Fullscreen", "Tablet", "Idle" };
        static readonly string[] WhenLabels = { "Always", "Plugged in", "On battery", "Battery below %", "Time window (quiet hours)", "Mic or camera in use", "Specific apps in front", "A game in front (auto-detected)", "Fullscreen app", "Tablet / tent posture", "Idle for N minutes" };

        public static bool Edit(IWin32Window owner, Rule r, Settings s, bool allowDelete = false)
        {
            LastDeleted = false;
            using (var f = new Form { Text = "Rule", BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.Body.Value, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false })
            {
                f.ClientSize = new Size(Theme.S(560), Theme.S(570));
                f.HandleCreated += (o, e) => NativeApp.DarkTitle(f.Handle);
                var st = new Stack { Dock = DockStyle.Fill, BackColor = Theme.Card, Inner = new Padding(Theme.S(20)), Gap = Theme.S(2), MaxContentWidth = 0 };
                f.Controls.Add(st);
                var name = new DarkText(300) { Text = r.Name };
                var when = new DarkCombo(300); when.Items.AddRange(WhenLabels); when.SelectedIndex = Math.Max(0, Array.IndexOf(Whens, r.When));
                var apps = new DarkText(300) { Text = r.Apps };
                var val = new DarkText(80) { Text = r.Value.ToString(CultureInfo.CurrentCulture) };
                var from = new DarkText(80) { Text = r.From }; var to = new DarkText(80) { Text = r.To };
                var prof = new DarkCombo(300); prof.Items.Add("(don't change profile)"); foreach (var p in s.Profiles) prof.Items.Add(p.Name);
                prof.SelectedIndex = string.IsNullOrEmpty(r.Profile) ? 0 : Math.Max(0, prof.Items.IndexOf(r.Profile));
                var cap = new Slider(300) { Min = 0, Max = 100, Step = 5, Unit = " %", Value = r.PerfCapPct };
                var fanMax = new Slider(300) { Min = 0, Max = 6500, Step = 100, Value = r.FanRpmMax, Display = v => v <= 0 ? "No limit" : v.ToString("N0") + " rpm" };
                var acOnly = new ToggleSwitch { Checked = r.PluggedInOnly };
                st.Add(new Row("Name", name));
                st.Add(new Row("When", when));
                var rApps = st.Add(new Row("Apps (process names, comma separated)", apps, "e.g. game.exe, obs64, blender"));
                st.Add(new Row("Only when plugged in", acOnly));
                var rVal = st.Add(new Row("Value", val));
                var tf = new Flow(36) { Width = Theme.S(200) }; tf.Controls.Add(from); tf.Controls.Add(new Lbl("to") { Width = Theme.S(24) }); tf.Controls.Add(to);
                var rTime = st.Add(new Row("From / to (24 h)", tf));
                st.Add(new Row("Use profile", prof));
                st.Add(new Row("CPU budget cap (0 = none)", cap));
                st.Add(new Row("Max fan speed", fanMax));
                Action vis = () =>
                {
                    var w = Whens[Math.Max(0, when.SelectedIndex)];
                    rApps.Visible = w == "App" || w == "Game"; rVal.Visible = w == "BatteryBelow" || w == "Idle"; rTime.Visible = w == "QuietHours";
                    rVal.Caption.Text = w == "Idle" ? "Idle minutes" : "Battery %";
                    rApps.Caption.Text = w == "Game" ? "Extra games the detector misses (optional)" : "Apps (process names, comma separated)";
                    st.PerformLayout();
                };
                when.SelectedIndexChanged += (o, e) => vis(); vis();
                var bf = new Flow(48);
                var ok = new FlatButton("Save", true); var cancel = new FlatButton("Cancel"); var del = new FlatButton("Delete rule");
                bf.Controls.Add(ok); bf.Controls.Add(cancel); if (allowDelete) bf.Controls.Add(del);
                st.Add(bf);
                ok.Click += (o, e) =>
                {
                    r.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "Rule";
                    r.When = Whens[Math.Max(0, when.SelectedIndex)];
                    r.Apps = apps.Text.Trim(); r.Value = val.Number(0); r.From = from.Text.Trim(); r.To = to.Text.Trim();
                    r.Profile = prof.SelectedIndex <= 0 ? "" : prof.SelectedItem.ToString();
                    r.PerfCapPct = (int)cap.Value; r.FanRpmMax = (int)fanMax.Value; r.PluggedInOnly = acOnly.Checked;
                    f.DialogResult = DialogResult.OK;
                };
                cancel.Click += (o, e) => f.DialogResult = DialogResult.Cancel;
                del.Click += (o, e) => { LastDeleted = true; f.DialogResult = DialogResult.OK; };
                return f.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
