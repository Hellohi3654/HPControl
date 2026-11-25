using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace HPControl.App
{
    /// <summary>Vendor audio effects UI: DTS presets + 10-band EQ, Fortemedia mic noise reduction + mic mode.</summary>
    static class AudioFx
    {
        static Label _status;
        static RadioList _preset;
        static EqControl _eq;
        static ToggleSwitch _ns, _spkNs, _adv, _eqOn;
        static FlatButton _flat;
        static bool _eqWanted = true;   // EQ on/off the user last chose with DTS on (the driver turns it off with DTS Off)
        static Row _advRow;
        static Segmented _micMode;
        static int _seq;
        static Row _eqRow, _nsRow, _modeRow;
        static Card _card;
        static bool _loading;
        // which driver handles each control: "rtk" (Realtek, used on this laptop) or "fm" (Fortemedia, other HP models)
        static string _spkBackend = "fm", _micNrBackend = "fm", _modeBackend = "fm";

        public static void Build(Card card, Model m)
        {
            _card = card;
            _status = new Lbl("Loading HP audio effects…", Theme.Small.Value, Theme.Sub, Theme.S(24));
            card.Add(_status);
            card.Add(new Lbl("Preset", Theme.BodyBold.Value, Theme.Text, Theme.S(32)));
            _preset = new RadioList();
            _preset.SetItems(new[] { new RadioList.Item { Key = "0", Text = "Music" }, new RadioList.Item { Key = "1", Text = "Movie" }, new RadioList.Item { Key = "2", Text = "Voice" }, new RadioList.Item { Key = "7", Text = "Off (no DTS processing)" } });
            card.Add(_preset);
            // leaving Off: turn the EQ back on if it was on before (the driver switches it off along with DTS)
            _preset.SelectionChanged += (s, e) => { if (!_loading) { ShowEq(_preset.Selected != "7" && _eqWanted, _preset.Selected != "7"); Do("dts-mode", _preset.Selected, _eqWanted ? "1" : "0"); } };
            _eq = new EqControl();
            _eqOn = new ToggleSwitch();
            card.Add(new Row("Equalizer", _eqOn, "Works with the Music, Movie and Voice presets (DTS turns it off with \"Off\")."));
            _eqOn.CheckedChanged += (s, e) => { if (_loading) return; _eqWanted = _eqOn.Checked; ShowEq(_eqOn.Checked, true); Do("dts-geq", _eqOn.Checked ? "1" : "0"); };
            card.Add(_eq);
            _eq.Committed += (s, e) => { if (!_loading) Do("dts-eqall", string.Join(",", _eq.Values)); };
            _flat = new FlatButton("Flat EQ");
            _flat.Click += (s, e) => { if (!_eq.Enabled) return; for (int i = 0; i < 10; i++) _eq.Values[i] = 0; _eq.Invalidate(); Do("dts-eqall", string.Join(",", _eq.Values)); };
            _eqRow = card.Add(new Row("Reset", _flat));
            _spkNs = new ToggleSwitch();
            card.Add(new Row("AI noise removal (speakers)", _spkNs, "Removes background noise from what you hear, e.g. other people's calls."));
            _spkNs.CheckedChanged += (s, e) => { if (!_loading) Do(_spkBackend == "rtk" ? "rtk-spknr" : "fm-spkns", _spkNs.Checked ? "1" : "0"); };
            card.Add(new Lbl("Microphone", Theme.BodyBold.Value, Theme.Text, Theme.S(32)));
            _ns = new ToggleSwitch();
            _nsRow = card.Add(new Row("Noise reduction", _ns, "AI noise suppression on the built-in mic."));
            _ns.CheckedChanged += (s, e) => { if (!_loading) Do(_micNrBackend == "rtk" ? "rtk-micnr" : "fm-ns", _ns.Checked ? "1" : "0"); };
            _micMode = new Segmented(360);
            _micMode.Set(new[] { "private", "conference" }, new[] { "Personal", "Conference" });
            _modeRow = card.Add(new Row("Mic mode", _micMode, "Personal = focuses on your voice. Conference = picks up everyone around the laptop."));
            _micMode.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                if (_modeBackend == "rtk") Do("rtk-mode", _micMode.Selected == "private" ? "personal" : _micMode.Selected);
                else Do("fm-mode", _micMode.Selected);
            };
            _adv = new ToggleSwitch();
            _advRow = card.Add(new Row("Advanced personal mode", _adv, "Stronger voice focus in Personal mode: blocks voices other than yours (HP's \"Advanced personal mode\").") { Separator = false });
            _advRow.Visible = false;
            _adv.CheckedChanged += (s, e) => { if (!_loading) Do("fm-adv", _adv.Checked ? "1" : "0"); };
        }

        static void Do(params string[] args)
        {
            _seq++;
            new Thread(() =>
            {
                var r = AudioFxHost.Call(args);
                _card.BeginInvoke((Action)(() =>
                {
                    if (r.ContainsKey("error")) { _status.Text = "Couldn't apply: " + r.Str("error"); return; }
                    // DTS commands answer with the driver's real state (mode, EQ on/off, bands)
                    if (r.ContainsKey("dtsMode")) { _loading = true; ApplyDts(r); _loading = false; }
                    // toggles: show what the driver actually ended up with
                    if (r.ContainsKey("value"))
                    {
                        bool v = r.Bool("value");
                        var t = args[0] == "fm-ns" || args[0] == "rtk-micnr" ? _ns : args[0] == "fm-spkns" || args[0] == "rtk-spknr" ? _spkNs : args[0] == "fm-adv" ? _adv : null;
                        if (t != null && t.Checked != v) { _loading = true; t.Checked = v; _loading = false; _status.Text = "The audio driver didn't accept that change."; }
                    }
                }));
            }) { IsBackground = true }.Start();
        }

        public static void Refresh(Card card)
        {
            int start = _seq;
            new Thread(() =>
            {
                var r = AudioFxHost.Call("status");
                // Realtek runs in its own helper process, so a problem there can't take DTS down with it
                var rk = AudioFxHost.Call("rtk-status");
                foreach (var kv in rk) if (kv.Key.StartsWith("rtk")) r[kv.Key] = kv.Value;
                if (!rk.ContainsKey("rtk")) { r["rtk"] = false; r["rtkError"] = rk.Str("error", "no answer"); }
                // ignore a status read that started before the user changed something
                card.BeginInvoke((Action)(() => { if (start == _seq) Apply(r); }));
            }) { IsBackground = true }.Start();
        }

        static void Apply(Dictionary<string, object> r)
        {
            _loading = true;
            var msgs = new List<string>();
            bool dts = r.Bool("dts");
            ApplyDts(r);
            if (!dts) msgs.Add("DTS presets/EQ unavailable: " + r.Str("dtsError", r.Str("error", "?")));
            bool fm = r.Bool("fm"), rtk = r.Bool("rtk");
            bool fmSpk = fm && r.Bool("spkAiNsSupported"), fmNr = fm && r.Bool("micAiNsSupported"), fmMode = fm && (r.Bool("privateSupported") || r.Bool("conferenceSupported"));
            bool rtkSpk = rtk && r.Bool("rtkSpkNrVisible"), rtkNr = rtk && r.Bool("rtkMicNrVisible"), rtkMode = rtk && r.Bool("rtkModeVisible");
            _spkBackend = rtkSpk || !fmSpk ? "rtk" : "fm";
            _micNrBackend = rtkNr || !fmNr ? "rtk" : "fm";
            _modeBackend = rtkMode || !fmMode ? "rtk" : "fm";
            _spkNs.Enabled = rtkSpk || fmSpk;
            _ns.Enabled = rtkNr || fmNr;
            _micMode.Enabled = rtkMode || fmMode;
            _advRow.Visible = fm && r.Bool("advSupported");
            _spkNs.Checked = rtkSpk ? r.Bool("rtkSpkNr") : r.Bool("spkAiNs");
            _ns.Checked = rtkNr ? r.Bool("rtkMicNr") : r.Bool("micAiNs");
            _adv.Checked = r.Bool("adv");
            bool studio = rtkMode ? r.Bool("rtkStudioVisible") : r.Bool("studioSupported");
            _micMode.Set(studio ? new[] { "private", "conference", "studio" } : new[] { "private", "conference" }, studio ? new[] { "Personal", "Conference", "Studio" } : new[] { "Personal", "Conference" });
            if (rtkMode) _micMode.Selected = r.Bool("rtkConference") ? "conference" : (studio && r.Bool("rtkStudio")) ? "studio" : "private";
            else _micMode.Selected = r.Bool("conference") ? "conference" : (studio && r.Bool("studio")) ? "studio" : "private";
            _micMode.Invalidate(); _ns.Invalidate(); _spkNs.Invalidate(); _adv.Invalidate();
            _card.PerformLayout();
            if (!fm && !rtk) msgs.Add("Mic and speaker effects unavailable: " + r.Str("rtkError", r.Str("fmError", "?")));
            else
            {
                if (!_ns.Enabled && !_micMode.Enabled) msgs.Add("The audio driver says mic noise reduction and mic modes aren't available right now.");
                if (!_spkNs.Enabled) msgs.Add("Speaker AI noise removal isn't available on this output.");
            }
            _status.Text = msgs.Count == 0 ? "Connected to HP's DTS and Realtek audio drivers." : string.Join("\n", msgs);
            _loading = false;
        }

        /// <summary>Preset, EQ on/off and bands from the driver's answer.</summary>
        static void ApplyDts(Dictionary<string, object> r)
        {
            bool dts = r.Bool("dts");
            _preset.Enabled = dts;
            if (!dts) { ShowEq(false, false); return; }
            int mode = (int)r.Num("dtsMode");
            _preset.Select(mode.ToString()); _preset.Invalidate();
            bool on = r.Num("geqEnable") != 0 && mode != 7;
            if (mode != 7) _eqWanted = on;
            _eq.Min = (int)r.Num("geqMin", -12); _eq.Max = (int)r.Num("geqMax", 12);
            var b = r.Arr("geq");
            if (b != null) for (int i = 0; i < Math.Min(10, b.Count); i++) _eq.Values[i] = Convert.ToInt32(b[i]);
            ShowEq(on, mode != 7);
        }

        /// <summary>EQ shown as on/off; the switch itself only works with a DTS preset (not Off).</summary>
        static void ShowEq(bool on, bool canToggle)
        {
            bool was = _loading; _loading = true;
            _eqOn.Checked = on; _eqOn.Enabled = canToggle; _eqOn.Invalidate();
            _eq.Enabled = on; _flat.Enabled = on; _eq.Invalidate();
            _loading = was;
        }
    }

    /// <summary>10-band graphic EQ like the HP app's.</summary>
    class EqControl : Control
    {
        public int[] Values = new int[10];
        public int Min = -12, Max = 12;
        public event EventHandler Committed;
        static readonly string[] Labels = { "32", "64", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };
        int _drag = -1;

        public EqControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(200); Font = Theme.Small.Value; Cursor = Cursors.Hand;
        }

        Rectangle Plot => new Rectangle(Theme.S(40), Theme.S(10), Width - Theme.S(56), Height - Theme.S(40));
        float Xb(int i) { var p = Plot; return p.Left + p.Width * (i + 0.5f) / 10f; }
        float Yv(double v) { var p = Plot; return p.Bottom - (float)((v - Min) / Math.Max(1, Max - Min)) * p.Height; }
        double LabelScale { get { int m = Math.Max(Math.Abs(Min), Math.Abs(Max)); return m >= 200 ? 100.0 : m >= 20 ? 10.0 : 1.0; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(Theme.Card); Theme.Smooth(g);
            var p = Plot;
            using (var gp = new Pen(Color.FromArgb(40, 255, 255, 255)))
                for (int k = 0; k <= 4; k++)
                {
                    double v = Min + (Max - Min) * k / 4.0; float y = Yv(v);
                    g.DrawLine(gp, p.Left, y, p.Right, y);
                    TextRenderer.DrawText(g, (v / LabelScale).ToString("+0;-0;0") + " dB", Font, new Rectangle(0, (int)y - Theme.S(9), p.Left - Theme.S(6), Theme.S(18)), Theme.Sub, TextFormatFlags.Right);
                }
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                float x = Xb(i);
                using (var pen = new Pen(Color.FromArgb(70, 70, 90), Theme.S(4))) g.DrawLine(pen, x, p.Top, x, p.Bottom);
                using (var pen = new Pen(Enabled ? Theme.Accent : Theme.Line, Theme.S(4))) g.DrawLine(pen, x, Yv(0), x, Yv(Values[i]));
                pts[i] = new PointF(x, Yv(Values[i]));
                TextRenderer.DrawText(g, Labels[i], Font, new Rectangle((int)x - Theme.S(24), p.Bottom + Theme.S(6), Theme.S(48), Theme.S(20)), Theme.Sub, TextFormatFlags.HorizontalCenter);
            }
            using (var b = new SolidBrush(Color.FromArgb(40, Theme.Accent)))
            {
                var poly = new List<PointF> { new PointF(pts[0].X, Yv(Min)) }; poly.AddRange(pts); poly.Add(new PointF(pts[9].X, Yv(Min)));
                g.FillPolygon(b, poly.ToArray());
            }
            foreach (var pt in pts) using (var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Line)) g.FillEllipse(b, pt.X - Theme.S(8), pt.Y - Theme.S(8), Theme.S(16), Theme.S(16));
        }

        int Hit(int x) { for (int i = 0; i < 10; i++) if (Math.Abs(Xb(i) - x) < Plot.Width / 20f) return i; return -1; }
        void SetAt(int i, int y) { var p = Plot; double v = Min + (p.Bottom - y) / (double)p.Height * (Max - Min); Values[i] = (int)Math.Round(Math.Max(Min, Math.Min(Max, v))); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; _drag = Hit(e.X); if (_drag >= 0) { Capture = true; SetAt(_drag, e.Y); } }
        protected override void OnMouseMove(MouseEventArgs e) { if (_drag >= 0) SetAt(_drag, e.Y); }
        protected override void OnMouseUp(MouseEventArgs e) { if (_drag >= 0) { _drag = -1; Capture = false; Committed?.Invoke(this, EventArgs.Empty); } }
    }
}
