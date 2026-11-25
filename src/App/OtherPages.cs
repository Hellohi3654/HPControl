using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HPControl.App
{
    // ===================================================================== BATTERY
    class BatteryPage : Stack, IPage
    {
        readonly Model _m;
        readonly Stat _pct = new Stat("Charge"), _health = new Stat("Battery health"), _power = new Stat("Power"), _temp = new Stat("Battery temperature");
        readonly ToggleSwitch _bhm = new ToggleSwitch(), _abe = new ToggleSwitch();
        readonly FlatButton _topUp = new FlatButton("Charge to 100% once", true);
        readonly Row _topRow;
        readonly Dictionary<string, ValueLabel> _info = new Dictionary<string, ValueLabel>();
        readonly Timer _t = new Timer { Interval = 5000 };
        bool _loading;
        Dictionary<string, object> _last;
        volatile bool _fetching;
        // own connection on a worker thread: the battery query reads WMI and BIOS settings, which can take a moment
        readonly ServiceClient _client = new ServiceClient();

        public BatteryPage(Model m)
        {
            _m = m; AutoScroll = true;
            Add(new Lbl("Battery manager", Theme.H1.Value, Theme.Text, Theme.S(40)));
            var grid = Add(new Grid(4, 96)); grid.Controls.AddRange(new Control[] { _pct, _health, _power, _temp });

            var c = Add(new Card("Charging"));
            c.Add(new Row("Maximize battery health", _bhm, "Charges to about 80 % only (HP firmware setting). Best if you're mostly plugged in."));
            c.Add(new Row("Adaptive battery extender", _abe, "HP firmware learns your usage and limits charge when it helps longevity."));
            _topRow = c.Add(new Row("Need a full battery for a trip?", _topUp, "Temporarily allows 100 %, then turns Maximize battery health back on as soon as you unplug.") { Separator = false });
            // BIOS setting writes can take a few seconds: in the background, then show what the firmware reports
            _bhm.CheckedChanged += (s, e) => { if (_loading) return; _bhm.Enabled = false; _m.CallAsync(this, "setBatteryHealth", Model.A("on", _bhm.Checked), r => { _bhm.Enabled = true; Reload(); }); };
            _abe.CheckedChanged += (s, e) => { if (_loading) return; _abe.Enabled = false; _m.CallAsync(this, "setAdaptiveExtender", Model.A("on", _abe.Checked), r => { _abe.Enabled = true; Reload(); }); };
            _topUp.Click += (s, e) => { bool pending = _last != null && _last.Bool("topUpPending"); _m.CallAsync(this, pending ? "cancelTopUp" : "topUp", null, r => Reload()); };

            var ic = Add(new Card("Battery information"));
            foreach (var (k, label) in new[] { ("state", "State"), ("healthDetail", "Full charge vs design"), ("cycles", "Cycle count"), ("remaining", "Remaining energy"), ("runtime", "Estimated time left"),
                ("voltage", "Voltage / current"), ("cells", "Cell voltages"), ("designV", "Design voltage"), ("manufacturer", "Maker / model"), ("batteryId", "HP battery ID"), ("serial", "Serial / manufactured") })
            {
                var v = new ValueLabel(420); _info[k] = v; ic.Add(new Row(label, v));
            }
            _t.Tick += (s, e) => { if (Visible && FindForm()?.WindowState != FormWindowState.Minimized) Reload(); };
        }

        public void OnShow() { Reload(); _t.Start(); }

        // the 5 s refresh only runs while this page is showing
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (!Visible) _t.Stop(); }

        void Reload()
        {
            if (_fetching) return;
            _fetching = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, object> d = null;
                try { d = _client.Call("getBattery") as Dictionary<string, object>; } catch { }
                try { BeginInvoke((Action)(() => { _fetching = false; if (d != null && !IsDisposed) ShowInfo(d); })); } catch { _fetching = false; }
            });
        }

        void ShowInfo(Dictionary<string, object> d)
        {
            _last = d;
            _loading = true;
            int pct = (int)d.Num("percent");
            bool ac = d.Bool("onAC");
            double cw = d.Num("chargeW"), dw = d.Num("dischargeW");
            _pct.Set(pct + "%", ac ? (cw > 0 ? "charging" : "plugged in, not charging") : "on battery");
            double h = d.Num("healthPct", -1);
            _health.Set(d.Str("healthText", "?"), h >= 0 ? h.ToString("0.0") + " % of design" : "", h >= 80 ? Theme.Good : h >= 60 ? Theme.Warn : Theme.Bad);
            _power.Set(dw > 0 ? dw.ToString("0.0") + " W" : cw > 0 ? "+" + cw.ToString("0.0") + " W" : "0 W", dw > 0 ? "drawing from battery" : cw > 0 ? "charging" : "idle");
            double tc = d.Num("temperatureC", double.NaN);
            _temp.Set(double.IsNaN(tc) ? "–" : tc.ToString("0") + " °C", "", tc >= 45 ? Theme.Warn : Theme.Text);
            _bhm.Checked = d.Str("healthManager") == "on"; _bhm.Enabled = d.Str("healthManager") != "unsupported";
            _abe.Checked = d.Str("adaptiveExtender") == "on"; _abe.Enabled = d.Str("adaptiveExtender") != "unsupported";
            bool pending = d.Bool("topUpPending");
            _topUp.Text = pending ? "Cancel top-up" : "Charge to 100% once";
            _topRow.SetDesc(pending ? "Top-up active: charging to 100 %. Battery health mode comes back on when you unplug." : "Temporarily allows 100 %, then turns Maximize battery health back on as soon as you unplug.");
            _info["state"].Text = pct + "% · " + (ac ? (cw > 0 ? "Charging" : "Plugged in") : "Discharging");
            _info["healthDetail"].Text = d.Num("fullChargeWh").ToString("0.0") + " Wh / " + d.Num("designWh").ToString("0.0") + " Wh";
            _info["cycles"].Text = d.Num("cycles", -1) >= 0 ? d.Num("cycles").ToString("0") : "–";
            _info["remaining"].Text = d.Num("remainingWh").ToString("0.0") + " Wh";
            int rt = (int)d.Num("runtimeMin", -1);
            _info["runtime"].Text = rt > 0 && !ac ? (rt / 60) + " h " + (rt % 60) + " min" : "–";
            _info["voltage"].Text = d.Num("voltageV").ToString("0.00") + " V · " + d.Num("currentA").ToString("0.00") + " A";
            var cells = d.Arr("cellsV");
            _info["cells"].Text = cells == null ? "–" : string.Join("  ", cells.Select(x => Convert.ToDouble(x).ToString("0.000") + " V"));
            _info["designV"].Text = d.Num("designVoltageV").ToString("0.00") + " V";
            _info["manufacturer"].Text = string.IsNullOrEmpty(d.Str("manufacturer", "")) ? "–" : d.Str("manufacturer");
            _info["batteryId"].Text = d.Str("batteryId", "–");
            _info["serial"].Text = d.Str("serial", "–") + " · " + d.Str("manufactureDate", "");
            _loading = false;
        }

        public void OnTelemetry(Telemetry t, List<LogEntry> events) { }
    }

    // ===================================================================== AUDIO
    class AudioPage : Stack, IPage
    {
        readonly Model _m;
        readonly DarkCombo _out = new DarkCombo(340), _in = new DarkCombo(340);
        readonly Slider _outVol = new Slider(340) { Unit = " %" }, _inVol = new Slider(340) { Unit = " %" };
        readonly ToggleSwitch _outMute = new ToggleSwitch(), _inMute = new ToggleSwitch();
        readonly Card _fx;
        bool _loading;

        public AudioPage(Model m)
        {
            _m = m; AutoScroll = true;
            Add(new Lbl("Audio", Theme.H1.Value, Theme.Text, Theme.S(40)));
            var o = Add(new Card("Output"));
            o.Add(new Row("Device", _out)); o.Add(new Row("Volume", _outVol)); o.Add(new Row("Mute", _outMute) { Separator = false });
            var i = Add(new Card("Input"));
            i.Add(new Row("Device", _in)); i.Add(new Row("Volume", _inVol)); i.Add(new Row("Mute", _inMute) { Separator = false });
            _fx = Add(new Card("Sound effects", "Uses the same DTS and Realtek audio components as the HP app."));
            AudioFx.Build(_fx, m);
            var sc = Add(new Card("Shortcuts"));
            var bf = new Flow(40) { Width = Theme.S(440) };
            var bSound = new FlatButton("Windows sound settings"); var bProps = new FlatButton("Classic Sound panel");
            bf.Controls.Add(bSound); bf.Controls.Add(bProps);
            sc.Add(new Row("Windows", bf) { Separator = false });
            bSound.Click += (s, e) => Shell("ms-settings:sound");
            bProps.Click += (s, e) => Shell("mmsys.cpl");

            _out.SelectedIndexChanged += (s, e) => { if (_loading || _out.SelectedItem == null) return; Try(() => CoreAudio.SetDefault(((AudioDeviceInfo)_out.SelectedItem).Id)); LoadLevels(); };
            _in.SelectedIndexChanged += (s, e) => { if (_loading || _in.SelectedItem == null) return; Try(() => CoreAudio.SetDefault(((AudioDeviceInfo)_in.SelectedItem).Id)); LoadLevels(); };
            _outVol.ValueChanged += (s, e) => { if (!_loading) Try(() => CoreAudio.SetVolume(null, EDataFlow.Render, (float)(_outVol.Value / 100))); };
            _inVol.ValueChanged += (s, e) => { if (!_loading) Try(() => CoreAudio.SetVolume(null, EDataFlow.Capture, (float)(_inVol.Value / 100))); };
            _outMute.CheckedChanged += (s, e) => { if (!_loading) Try(() => CoreAudio.SetMute(null, EDataFlow.Render, _outMute.Checked)); };
            _inMute.CheckedChanged += (s, e) => { if (!_loading) Try(() => CoreAudio.SetMute(null, EDataFlow.Capture, _inMute.Checked)); };
        }

        static void Shell(string s) { try { Process.Start(new ProcessStartInfo(s) { UseShellExecute = true }); } catch { } }
        void Try(Action a) { try { a(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Audio"); } }

        volatile bool _listing;
        public void OnShow()
        {
            LoadLevels();
            AudioFx.Refresh(_fx);
            // listing the audio devices can take a moment (drivers waking up): on a worker thread
            if (_listing) return;
            _listing = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                List<AudioDeviceInfo> outs = null, ins = null;
                try { outs = CoreAudio.List(EDataFlow.Render); } catch { }
                try { ins = CoreAudio.List(EDataFlow.Capture); } catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _listing = false;
                        if (IsDisposed) return;
                        _loading = true;
                        foreach (var (cb, list) in new[] { (_out, outs), (_in, ins) })
                        {
                            if (list == null) continue;
                            // only rebuild the list when the devices changed
                            var cur = cb.Items.Cast<AudioDeviceInfo>().Select(d => d.Id + "|" + d.Name);
                            if (!cur.SequenceEqual(list.Select(d => d.Id + "|" + d.Name))) { cb.Items.Clear(); foreach (var d in list) cb.Items.Add(d); }
                            var def = cb.Items.Cast<AudioDeviceInfo>().FirstOrDefault(x => list.Any(d => d.IsDefault && d.Id == x.Id));
                            if (def != null && cb.SelectedItem != def) cb.SelectedItem = def;
                        }
                        _loading = false;
                    }));
                }
                catch { _listing = false; }
            });
        }

        void LoadLevels()
        {
            _loading = true;
            try
            {
                _outVol.Value = Math.Round(CoreAudio.GetVolume(null, EDataFlow.Render) * 100); _outMute.Checked = CoreAudio.GetMute(null, EDataFlow.Render);
                _inVol.Value = Math.Round(CoreAudio.GetVolume(null, EDataFlow.Capture) * 100); _inMute.Checked = CoreAudio.GetMute(null, EDataFlow.Capture);
            }
            catch { }
            _outVol.Invalidate(); _inVol.Invalidate();
            _loading = false;
        }

        public void OnTelemetry(Telemetry t, List<LogEntry> events) { }
    }

    // ===================================================================== SETTINGS
    class SettingsPage : Stack, IPage
    {
        readonly Model _m;
        readonly ToggleSwitch _autostart = new ToggleSwitch(), _tray = new ToggleSwitch(), _notif = new ToggleSwitch(), _hk = new ToggleSwitch(), _restore = new ToggleSwitch();
        readonly DarkText _hotkey = new DarkText(160);
        readonly Slider _pollAc = new Slider(300) { Min = 1000, Max = 10000, Step = 500, Unit = " ms" }, _pollDc = new Slider(300) { Min = 1500, Max = 15000, Step = 500, Unit = " ms" };
        readonly Slider _safety = new Slider(300) { Min = 80, Max = 100, Step = 1, Unit = " °C" };
        readonly Slider _hist = new Slider(300) { Min = 10, Max = 120, Step = 10, Unit = " min" };
        bool _loading;
        readonly ToggleSwitch _adv = new ToggleSwitch(), _autoUpd = new ToggleSwitch();
        readonly FlatButton _updBtn = new FlatButton("Check now");
        readonly Lbl _updStatus = new Lbl("", Theme.Small.Value, Theme.Sub, Theme.S(20)) { Width = Theme.S(360) };
        Updater.Release _update;
        volatile bool _updBusy;
        Card _engine;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public SettingsPage(Model m)
        {
            _m = m; AutoScroll = true;
            Add(new Lbl("Settings", Theme.H1.Value, Theme.Text, Theme.S(40)));
            var g0 = Add(new Card());
            g0.Add(new Row("Show advanced settings", _adv, "Curve editor, timing, temperature sensors, the CPU power limiter and engine settings.") { Separator = false });
            _adv.CheckedChanged += (s, ev) => { if (_loading) return; _m.Settings.General.AdvancedMode = _adv.Checked; _m.MarkDirty(); NativeApp.Frozen(this, () => { _engine.Visible = _adv.Checked; PerformLayout(); }); };
            var c = Add(new Card("Tray helper", "A tiny background helper in your session. It supplies context (foreground app, calls, tablet mode), the hotkey and notifications. Entirely event driven: about 0 % CPU."));
            c.Add(new Row("Start tray helper when I sign in", _autostart));
            c.Add(new Row("Show tray icon", _tray));
            c.Add(new Row("Notifications", _notif));
            c.Add(new Row("Hotkey to cycle profiles / modes", _hk));
            c.Add(new Row("Hotkey", _hotkey, "e.g. Ctrl+Alt+F") { Separator = false });
            var e = _engine = Add(new Card("Engine"));
            e.Add(new Row("Sensor polling on AC", _pollAc, "Lower = reacts faster. Polling is very cheap either way."));
            e.Add(new Row("Sensor polling on battery", _pollDc));
            e.Add(new Row("Safety temperature", _safety, "Above this every HP Control limit is released immediately (the firmware has its own protection too)."));
            e.Add(new Row("Keep history for", _hist));
            e.Add(new Row("Return to Smart sense when the service stops (Auto mode)", _restore) { Separator = false });
            var u = Add(new Card("Updates", "HP Control " + AppInfo.Version + ". New versions come from GitHub (" + "github.com/Hellohi3654/HPControl" + "). Installing always asks first, and your settings are backed up before anything changes."));
            u.Add(new Row("Check for updates automatically", _autoUpd, "Once a day, from the tray helper (one small download). It tells you when there's a new version."));
            var uf = new Flow(40) { Width = Theme.S(560) };
            uf.Controls.AddRange(new Control[] { _updBtn, _updStatus });
            u.Add(new Row("Updates", uf) { Separator = false });
            BindT(_autoUpd, v => _m.Settings.General.AutoUpdate = v);
            _updBtn.Click += (s, ev) => { if (_update != null) InstallUpdate(); else CheckUpdate(); };

            var b = Add(new Card("Backup & maintenance"));
            var f1 = new Flow(40) { Width = Theme.S(560) };
            var exp = new FlatButton("Export settings…"); var imp = new FlatButton("Import settings…"); var restore = new FlatButton("Restore a backup…"); var reset = new FlatButton("Reset to defaults");
            f1.Controls.AddRange(new Control[] { exp, imp, restore, reset });
            b.Add(new Row("Settings file", f1, "Share profiles with friends who have the same laptop. A backup is saved automatically before every update."));
            var logs = new FlatButton("Open data folder");
            b.Add(new Row("Logs and settings", logs, "Send service.log from here when something goes wrong.") { Separator = false });
            restore.Click += (s, ev) =>
            {
                var dir = Path.Combine(AppInfo.DataDir, "backups");
                using (var d = new OpenFileDialog { Filter = "HP Control settings|*.json", InitialDirectory = Directory.Exists(dir) ? dir : AppInfo.DataDir, Title = "Restore settings from a backup" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        try { _m.Call("setSettings", Model.A("settings", Json.Parse(File.ReadAllText(d.FileName)))); _m.Reload(); Fill(); _m.ShowToast("Settings restored"); }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Restore failed"); }
                    }
            };

            _autostart.CheckedChanged += (s, ev) => { if (_loading) return; SetAutostart(_autostart.Checked); _m.Settings.General.AgentAutostart = _autostart.Checked; _m.MarkDirty(); };
            BindT(_tray, v => _m.Settings.General.TrayIcon = v); BindT(_notif, v => _m.Settings.General.Notifications = v); BindT(_hk, v => _m.Settings.General.HotkeyEnabled = v);
            BindT(_restore, v => _m.Settings.General.RestoreSmartSenseOnStop = v);
            _hotkey.Leave += (s, ev) => { if (_loading) return; _m.Settings.General.Hotkey = _hotkey.Text.Trim(); _m.MarkDirty(); };
            BindS(_pollAc, v => _m.Settings.General.PollAcMs = (int)v); BindS(_pollDc, v => _m.Settings.General.PollDcMs = (int)v);
            BindS(_safety, v => _m.Settings.General.SafetyTempC = v); BindS(_hist, v => _m.Settings.General.HistoryMinutes = (int)v);
            exp.Click += (s, ev) =>
            {
                using (var d = new SaveFileDialog { Filter = "HP Control settings|*.json", FileName = "hpcontrol-settings.json" })
                    if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, Json.Serialize(_m.Settings, true));
            };
            imp.Click += (s, ev) =>
            {
                using (var d = new OpenFileDialog { Filter = "HP Control settings|*.json" })
                    if (d.ShowDialog(this) == DialogResult.OK)
                    {
                        try
                        {
                            var parsed = Json.Parse(File.ReadAllText(d.FileName));
                            _m.Call("setSettings", Model.A("settings", parsed));
                            _m.Reload(); Fill();
                        }
                        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import failed"); }
                    }
            };
            reset.Click += (s, ev) => { if (MessageBox.Show(this, "Reset all HP Control settings to defaults?", "HP Control", MessageBoxButtons.YesNo) == DialogResult.Yes) { _m.Call("resetSettings"); _m.Reload(); Fill(); } };
            logs.Click += (s, ev) => { try { Process.Start("explorer.exe", AppInfo.DataDir); } catch { } };
        }

        void BindS(Slider s, Action<double> set) => s.ValueCommitted += (o, e) => { if (_loading) return; set(s.Value); _m.MarkDirty(); };
        void BindT(ToggleSwitch t, Action<bool> set) => t.CheckedChanged += (o, e) => { if (_loading) return; set(t.Checked); _m.MarkDirty(); };

        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("HPControlAgent", "\"" + Application.ExecutablePath + "\" --agent");
                else k.DeleteValue("HPControlAgent", false);
            }
        }

        static bool GetAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k?.GetValue("HPControlAgent") != null;
        }

        public void OnShow()
        {
            Fill();
            _m.ReloadAsync(this, changed => { if (changed && Visible) NativeApp.Frozen(this, Fill); });
        }

        void Fill()
        {
            var g = _m.Settings?.General; if (g == null) return;
            _loading = true;
            _autostart.Checked = GetAutostart(); _tray.Checked = g.TrayIcon; _notif.Checked = g.Notifications; _hk.Checked = g.HotkeyEnabled; _hotkey.Text = g.Hotkey;
            _adv.Checked = g.AdvancedMode; _engine.Visible = g.AdvancedMode; _autoUpd.Checked = g.AutoUpdate;
            _restore.Checked = g.RestoreSmartSenseOnStop; _pollAc.Value = g.PollAcMs; _pollDc.Value = g.PollDcMs; _safety.Value = g.SafetyTempC; _hist.Value = g.HistoryMinutes;
            foreach (Control c in Controls) c.Invalidate(true);
            _loading = false;
            PerformLayout();
        }

        void CheckUpdate()
        {
            if (_updBusy) return;
            _updBusy = true; _updStatus.Text = "Checking…";
            string repo = _m.Settings?.General?.UpdateRepo;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release r = null; string err = null;
                try { r = Updater.Check(repo); Updater.SaveState(DateTime.UtcNow, r?.Tag ?? Updater.LastOffered); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        _updBusy = false; _update = r;
                        if (err != null) _updStatus.Text = "Couldn't check: " + err;
                        else if (r == null) _updStatus.Text = "You have the latest version.";
                        else { _updStatus.Text = "Version " + r.Version + " is available."; _updBtn.Text = "Install " + r.Version; _updBtn.Invalidate(); }
                    }));
                }
                catch { _updBusy = false; }
            });
        }

        void InstallUpdate()
        {
            if (_updBusy || _update == null) return;
            if (MessageBox.Show(this, "Install HP Control " + _update.Version + " now?\n\nHP Control closes while it installs, Windows asks for admin, and it opens again when done. Your settings are kept (and backed up).",
                    "Update HP Control", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            _updBusy = true;
            var r = _update;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try { Updater.DownloadAndInstall(r, pct => { try { BeginInvoke((Action)(() => _updStatus.Text = "Downloading… " + pct + " %")); } catch { } }); }
                catch (Exception ex) { err = ex.Message; }
                try { BeginInvoke((Action)(() => { _updBusy = false; _updStatus.Text = err == null ? "Installing… (approve the admin prompt)" : "Update failed: " + err; })); } catch { }
            });
        }

        public void OnTelemetry(Telemetry t, List<LogEntry> events) { }
    }
}
