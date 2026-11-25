using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HPControl.App
{
    /// <summary>
    /// Multi-curve editor: HP-mode ladder as coloured temperature bands plus any number of draggable curves,
    /// each with its own Y scale. Click to add a point, drag to move, right-click to delete/change.
    /// </summary>
    class CurveEditor : Control
    {
        public class Series
        {
            public string Name;
            public Color Color;
            public Curve Curve;
            public double YMin, YMax;
            public string Unit;
            public string Format = "0";
            public bool Visible = true;
        }

        public double XMin = 30, XMax = 100;
        public string XUnit = "°C";
        /// <summary>Grid/label spacing and drag snapping on the X axis.</summary>
        public double XStep = 10, XSnap = 1;
        public readonly List<Series> Curves = new List<Series>();
        public List<LadderStep> Ladder;
        public double HysteresisC;
        /// <summary>-1 = ladder; otherwise index into Curves.</summary>
        public int Active = -1;
        public double MarkerX = double.NaN;
        /// <summary>Live value drawn as a dot on the active curve when its unit matches MarkerUnit (e.g. current fan rpm).</summary>
        public double MarkerY = double.NaN;
        public string MarkerUnit = "";
        /// <summary>Optional shaded zone from XMin up to this X (e.g. "fan off" below the switch-on temperature).</summary>
        public double ShadeToX = double.NaN;
        public string ShadeLabel = "";
        public event EventHandler Changed;

        int _dragPoint = -1, _dragStep = -1, _hoverPoint = -1, _hoverStep = -1;

        public CurveEditor()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(300);
            Font = Theme.Small.Value;
        }

        Rectangle Plot => new Rectangle(Theme.S(52), Theme.S(26), Width - Theme.S(52) - Theme.S(16), Height - Theme.S(26) - Theme.S(30));

        float X(double v) { var p = Plot; return (float)(p.Left + (v - XMin) / (XMax - XMin) * p.Width); }
        double Xv(float x) { var p = Plot; return XMin + (x - p.Left) / (double)p.Width * (XMax - XMin); }
        float Y(Series s, double v) { var p = Plot; return (float)(p.Bottom - (v - s.YMin) / (s.YMax - s.YMin) * p.Height); }
        double Yv(Series s, float y) { var p = Plot; return s.YMin + (p.Bottom - y) / (double)p.Height * (s.YMax - s.YMin); }

        Series ActiveSeries => Active >= 0 && Active < Curves.Count ? Curves[Active] : null;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            var p = Plot;
            using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, p);

            // ladder bands
            if (Ladder != null && Ladder.Count > 0)
            {
                var steps = Ladder.OrderBy(s => s.FromTempC).ToList();
                for (int i = 0; i < steps.Count; i++)
                {
                    double from = i == 0 ? XMin : steps[i].FromTempC;
                    double to = i + 1 < steps.Count ? steps[i + 1].FromTempC : XMax;
                    float x0 = Math.Max(p.Left, X(from)), x1 = Math.Min(p.Right, X(to));
                    if (x1 <= x0) continue;
                    var c = Theme.ModeColor(steps[i].Mode);
                    using (var b = new SolidBrush(Color.FromArgb(Active == -1 ? 60 : 30, c))) g.FillRectangle(b, x0, p.Top, x1 - x0, p.Height);
                    TextRenderer.DrawText(g, Settings.ModeLabel(steps[i].Mode), Theme.Small.Value, new Rectangle((int)x0 + 4, p.Top + 4, (int)(x1 - x0) - 6, Theme.S(18)), Color.FromArgb(Active == -1 ? 255 : 150, c), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    if (i > 0)
                    {
                        float bx = X(steps[i].FromTempC);
                        if (HysteresisC > 0)
                            using (var hb = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(50, c), Color.Transparent))
                                g.FillRectangle(hb, X(steps[i].FromTempC - HysteresisC), p.Top, bx - X(steps[i].FromTempC - HysteresisC), p.Height);
                        bool hot = Active == -1 && (i == _hoverStep || i == _dragStep);
                        using (var pen = new Pen(Color.FromArgb(Active == -1 ? 230 : 110, c), hot ? Theme.S(3) : Theme.S(1.5f))) g.DrawLine(pen, bx, p.Top, bx, p.Bottom);
                        if (Active == -1)
                            TextRenderer.DrawText(g, steps[i].FromTempC.ToString("0") + "°", Theme.Small.Value, new Point((int)bx - Theme.S(12), p.Bottom - Theme.S(18)), c, TextFormatFlags.NoPadding);
                    }
                }
            }

            if (!double.IsNaN(ShadeToX) && ShadeToX > XMin)
            {
                float sx = Math.Min(p.Right, X(ShadeToX));
                using (var hb = new HatchBrush(HatchStyle.BackwardDiagonal, Color.FromArgb(28, 255, 255, 255), Color.Transparent))
                    g.FillRectangle(hb, p.Left, p.Top, sx - p.Left, p.Height);
                if (!string.IsNullOrEmpty(ShadeLabel))
                    TextRenderer.DrawText(g, ShadeLabel, Theme.Small.Value, new Rectangle(p.Left + Theme.S(6), p.Top + Theme.S(6), (int)(sx - p.Left) - Theme.S(8), Theme.S(18)), Theme.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }

            // grid + x labels
            using (var gp = new Pen(Color.FromArgb(40, 255, 255, 255)))
            {
                for (double v = Math.Ceiling(XMin / XStep) * XStep; v <= XMax; v += XStep)
                {
                    float x = X(v);
                    g.DrawLine(gp, x, p.Top, x, p.Bottom);
                    string xl = XStep >= 1000 ? (v == 0 ? "0" : (v / 1000).ToString("0.#") + "k") : v.ToString("0") + XUnit;
                    TextRenderer.DrawText(g, xl, Font, new Point((int)x - Theme.S(14), p.Bottom + Theme.S(6)), Theme.Sub, TextFormatFlags.NoPadding);
                }
                var a = ActiveSeries;
                if (a != null)
                {
                    for (int i = 0; i <= 4; i++)
                    {
                        double v = a.YMin + (a.YMax - a.YMin) * i / 4.0;
                        float y = Y(a, v);
                        g.DrawLine(gp, p.Left, y, p.Right, y);
                        TextRenderer.DrawText(g, v.ToString(a.Format, CultureInfo.CurrentCulture), Font, new Rectangle(0, (int)y - Theme.S(8), p.Left - Theme.S(6), Theme.S(16)), a == ActiveSeries ? a.Color : Theme.Sub, TextFormatFlags.Right | TextFormatFlags.NoPadding);
                    }
                    TextRenderer.DrawText(g, a.Unit, Font, new Point(Theme.S(4), Theme.S(2)), a.Color, TextFormatFlags.NoPadding);
                }
            }

            // curves: kept inside the plot (a little margin so points on the edge stay whole)
            var clip = Rectangle.Inflate(p, Theme.S(8), Theme.S(8));
            g.SetClip(clip);
            for (int si = 0; si < Curves.Count; si++)
            {
                var s = Curves[si];
                if (!s.Visible || s.Curve == null || s.Curve.Points.Count == 0) continue;
                bool act = si == Active;
                if (!act) continue;
                bool en = s.Curve.Enabled;
                var pts = s.Curve.Points.OrderBy(q => q.X).ToList();
                var line = new List<PointF> { new PointF(p.Left, Y(s, pts[0].Y)) };
                line.AddRange(pts.Select(q => new PointF(X(q.X), Y(s, q.Y))));
                line.Add(new PointF(p.Right, Y(s, pts[pts.Count - 1].Y)));
                int alpha = act ? 255 : 120;
                if (!en) alpha /= 3;
                using (var pen = new Pen(Color.FromArgb(alpha, s.Color), act ? Theme.S(2.5f) : Theme.S(1.5f)))
                {
                    if (!en) pen.DashStyle = DashStyle.Dash;
                    g.DrawLines(pen, line.ToArray());
                }
                if (act)
                {
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var c = new PointF(X(pts[i].X), Y(s, pts[i].Y));
                        float d = (s.Curve.Points.IndexOf(pts[i]) == _hoverPoint || s.Curve.Points.IndexOf(pts[i]) == _dragPoint) ? Theme.S(14) : Theme.S(10);
                        using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, c.X - d / 2, c.Y - d / 2, d, d);
                        using (var pen = new Pen(Theme.Card, Theme.S(2))) g.DrawEllipse(pen, c.X - d / 2, c.Y - d / 2, d, d);
                    }
                    if (_dragPoint >= 0 && _dragPoint < s.Curve.Points.Count)
                    {
                        var q = s.Curve.Points[_dragPoint];
                        var txt = q.X.ToString("0") + XUnit + "  →  " + q.Y.ToString(s.Format) + " " + s.Unit;
                        TextRenderer.DrawText(g, txt, Theme.BodyBold.Value, new Point(p.Left + Theme.S(8), p.Top + Theme.S(22)), s.Color, TextFormatFlags.NoPadding);
                    }
                }
            }

            g.ResetClip();

            // live marker
            if (!double.IsNaN(MarkerX) && MarkerX >= XMin && MarkerX <= XMax)
            {
                float x = X(MarkerX);
                using (var pen = new Pen(Color.White, Theme.S(1.5f)) { DashStyle = DashStyle.Dot }) g.DrawLine(pen, x, p.Top, x, p.Bottom);
                TextRenderer.DrawText(g, "now " + MarkerX.ToString(XStep >= 100 ? "N0" : "0.0") + XUnit, Theme.Small.Value, new Point((int)x + 4, p.Bottom - Theme.S(36)), Color.White, TextFormatFlags.NoPadding);
                var ns = ActiveSeries;
                if (ns != null && ns.Unit == MarkerUnit && !double.IsNaN(MarkerY))
                {
                    float y = Y(ns, Math.Max(ns.YMin, Math.Min(ns.YMax, MarkerY)));
                    using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x - Theme.S(5), y - Theme.S(5), Theme.S(10), Theme.S(10));
                }
            }
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, p);
        }

        int HitPoint(Point m)
        {
            var s = ActiveSeries; if (s == null) return -1;
            for (int i = 0; i < s.Curve.Points.Count; i++)
            {
                var q = s.Curve.Points[i];
                if (Math.Abs(X(q.X) - m.X) <= Theme.S(9) && Math.Abs(Y(s, q.Y) - m.Y) <= Theme.S(9)) return i;
            }
            return -1;
        }

        int HitStep(Point m)
        {
            if (Ladder == null || Active != -1) return -1;
            var steps = Ladder.OrderBy(s => s.FromTempC).ToList();
            for (int i = 1; i < steps.Count; i++) if (Math.Abs(X(steps[i].FromTempC) - m.X) <= Theme.S(8)) return Ladder.IndexOf(steps[i]);
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var s = ActiveSeries;
            if (_dragPoint >= 0 && s != null)
            {
                var q = s.Curve.Points[_dragPoint];
                q.X = Math.Round(Math.Max(XMin, Math.Min(XMax, Xv(e.X))) / XSnap) * XSnap;
                double y = Yv(s, e.Y);
                double step = (s.YMax - s.YMin) > 200 ? 50 : (s.YMax - s.YMin) > 30 ? 1 : 0.5;
                q.Y = Math.Max(s.YMin, Math.Min(s.YMax, Math.Round(y / step) * step));
                Invalidate(); return;
            }
            if (_dragStep >= 0)
            {
                var steps = Ladder.OrderBy(x => x.FromTempC).ToList();
                var st = Ladder[_dragStep];
                int i = steps.IndexOf(st);
                double lo = i > 0 && i - 1 > 0 ? steps[i - 1].FromTempC + 2 : XMin + 2;
                double hi = i + 1 < steps.Count ? steps[i + 1].FromTempC - 2 : XMax - 1;
                st.FromTempC = Math.Round(Math.Max(lo, Math.Min(hi, Xv(e.X))));
                Invalidate(); return;
            }
            int hp = HitPoint(e.Location), hs = HitStep(e.Location);
            if (hp != _hoverPoint || hs != _hoverStep) { _hoverPoint = hp; _hoverStep = hs; Invalidate(); }
            Cursor = hs >= 0 ? Cursors.SizeWE : hp >= 0 ? Cursors.Hand : Cursors.Cross;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            var s = ActiveSeries;
            if (e.Button == MouseButtons.Left)
            {
                int hp = HitPoint(e.Location);
                if (hp >= 0) { _dragPoint = hp; Capture = true; return; }
                int hs = HitStep(e.Location);
                if (hs >= 0) { _dragStep = hs; Capture = true; return; }
                if (s != null && Plot.Contains(e.Location))
                {
                    var q = new CurvePoint(Math.Round(Xv(e.X) / XSnap) * XSnap, Math.Round(Yv(s, e.Y)));
                    s.Curve.Points.Add(q);
                    _dragPoint = s.Curve.Points.Count - 1; Capture = true; Invalidate();
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                int hp = HitPoint(e.Location);
                if (hp >= 0 && s != null && s.Curve.Points.Count > 2) { s.Curve.Points.RemoveAt(hp); Invalidate(); Changed?.Invoke(this, EventArgs.Empty); return; }
                if (Active == -1 && Ladder != null && Plot.Contains(e.Location)) ShowLadderMenu(e.Location);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragPoint >= 0 || _dragStep >= 0)
            {
                _dragPoint = -1; _dragStep = -1; Capture = false;
                ActiveSeries?.Curve.Points.Sort((a, b) => a.X.CompareTo(b.X));
                Invalidate(); Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        void ShowLadderMenu(Point at)
        {
            double t = Math.Round(Xv(at.X));
            var steps = Ladder.OrderBy(s => s.FromTempC).ToList();
            var cur = steps.LastOrDefault(s => s.FromTempC <= t) ?? steps.FirstOrDefault();
            var m = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Card, ForeColor = Theme.Text, ShowImageMargin = false };
            if (cur != null)
            {
                var change = new ToolStripMenuItem("Mode for this band") { ForeColor = Theme.Text };
                foreach (var mode in Settings.ModeNames.Where(x => x != "SmartSense"))
                {
                    var mm = mode;
                    var it = new ToolStripMenuItem(Settings.ModeLabel(mode)) { Checked = cur.Mode == mode, ForeColor = Theme.Text };
                    it.Click += (s, e) => { cur.Mode = mm; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); };
                    change.DropDownItems.Add(it);
                }
                m.Items.Add(change);
            }
            var add = new ToolStripMenuItem("Add step at " + t.ToString("0") + "°C") { ForeColor = Theme.Text };
            add.Click += (s, e) => { Ladder.Add(new LadderStep(t, cur?.Mode == "Quiet" ? "Balanced" : "Cool")); Invalidate(); Changed?.Invoke(this, EventArgs.Empty); };
            m.Items.Add(add);
            if (cur != null && steps.IndexOf(cur) > 0)
            {
                var del = new ToolStripMenuItem("Remove this step") { ForeColor = Theme.Text };
                del.Click += (s, e) => { Ladder.Remove(cur); Invalidate(); Changed?.Invoke(this, EventArgs.Empty); };
                m.Items.Add(del);
            }
            m.Show(this, at);
        }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors()) { }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim; base.OnRenderItemText(e); }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text; base.OnRenderArrow(e); }
        class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Theme.AccentDim;
            public override Color MenuItemBorder => Theme.Accent;
            public override Color MenuBorder => Theme.Line;
            public override Color ToolStripDropDownBackground => Theme.Card;
            public override Color ImageMarginGradientBegin => Theme.Card;
            public override Color ImageMarginGradientMiddle => Theme.Card;
            public override Color ImageMarginGradientEnd => Theme.Card;
            public override Color MenuItemSelectedGradientBegin => Theme.AccentDim;
            public override Color MenuItemSelectedGradientEnd => Theme.AccentDim;
            public override Color MenuItemPressedGradientBegin => Theme.AccentDim;
            public override Color MenuItemPressedGradientEnd => Theme.AccentDim;
            public override Color SeparatorDark => Theme.Line;
            public override Color CheckBackground => Theme.AccentDim;
            public override Color CheckSelectedBackground => Theme.Accent;
        }
    }
}
