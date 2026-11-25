using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HPControl.App
{
    /// <summary>History graph: temperature, fan speed, CPU budget and a strip showing which HP mode was active.</summary>
    class TimeGraph : Control
    {
        public class Pt { public DateTime T; public double Temp, Smooth, PowerW, Cpu; public int Budget, Fan = -1; public string Mode; }
        public List<Pt> Points = new List<Pt>();
        public int Minutes = 10;

        public TimeGraph()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(220);
            Font = Theme.Small.Value;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            Theme.Smooth(g);
            var p = new Rectangle(Theme.S(40), Theme.S(10), Width - Theme.S(80), Height - Theme.S(10) - Theme.S(40));
            using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, p);
            var now = DateTime.Now; var from = now.AddMinutes(-Minutes);
            var pts = Points.Where(q => q.T >= from).OrderBy(q => q.T).ToList();
            bool hasFan = pts.Any(q => q.Fan >= 0);
            double rmax = 6000;
            if (hasFan) { int mx = pts.Max(q => q.Fan); while (rmax < mx) rmax += 1000; }
            Func<double, float> YR = v => p.Bottom - (float)(v / rmax) * p.Height;
            var fanColor = Color.FromArgb(120, 230, 160);
            Func<DateTime, float> X = t => p.Left + (float)((t - from).TotalSeconds / (Minutes * 60.0)) * p.Width;
            double tmin = 30, tmax = 100;
            Func<double, float> YT = v => p.Bottom - (float)((v - tmin) / (tmax - tmin)) * p.Height;
            Func<double, float> YB = v => p.Bottom - (float)(v / 100.0) * p.Height;

            using (var gp = new Pen(Color.FromArgb(35, 255, 255, 255)))
                for (int v = 40; v <= 100; v += 20)
                {
                    g.DrawLine(gp, p.Left, YT(v), p.Right, YT(v));
                    TextRenderer.DrawText(g, v + "°", Font, new Rectangle(0, (int)YT(v) - Theme.S(9), p.Left - Theme.S(4), Theme.S(18)), Theme.Warn, TextFormatFlags.Right | TextFormatFlags.NoPadding);

                }

            // fan axis on the right: round numbers at their own height
            if (hasFan)
                for (int r = 0; r <= rmax; r += rmax > 6000 ? 2000 : 1500)
                    TextRenderer.DrawText(g, r == 0 ? "0" : (r / 1000.0).ToString("0.#") + "k", Font, new Point(p.Right + Theme.S(4), (int)YR(r) - Theme.S(9)), fanColor, TextFormatFlags.NoPadding);
            // mode strip
            int stripY = p.Bottom + Theme.S(6), stripH = Theme.S(8);
            for (int i = 0; i < pts.Count; i++)
            {
                float x0 = X(pts[i].T), x1 = i + 1 < pts.Count ? X(pts[i + 1].T) : X(now);
                using (var b = new SolidBrush(Theme.ModeColor(pts[i].Mode))) g.FillRectangle(b, x0, stripY, Math.Max(1, x1 - x0), stripH);
            }
            TextRenderer.DrawText(g, "mode", Font, new Point(Theme.S(2), stripY - Theme.S(4)), Theme.Sub, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "-" + Minutes + " min", Font, new Point(p.Left, stripY + stripH + 2), Theme.Dim, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "now", Font, new Point(p.Right - Theme.S(24), stripY + stripH + 2), Theme.Dim, TextFormatFlags.NoPadding);

            if (pts.Count >= 2)
            {
                using (var pen = new Pen(Color.FromArgb(110, 160, 160, 255), Theme.S(1.2f)) { DashStyle = DashStyle.Dash })
                    g.DrawLines(pen, pts.Select(q => new PointF(X(q.T), YB(q.Budget))).ToArray());
                var tp = pts.Where(q => !double.IsNaN(q.Temp)).Select(q => new PointF(X(q.T), YT(Math.Max(tmin, Math.Min(tmax, q.Temp))))).ToArray();
                if (tp.Length >= 2) using (var pen = new Pen(Color.FromArgb(120, Theme.Warn), Theme.S(1))) g.DrawLines(pen, tp);
                var sp = pts.Where(q => !double.IsNaN(q.Smooth)).Select(q => new PointF(X(q.T), YT(Math.Max(tmin, Math.Min(tmax, q.Smooth))))).ToArray();
                if (sp.Length >= 2) using (var pen = new Pen(Theme.Warn, Theme.S(2))) g.DrawLines(pen, sp);
                var fp = pts.Where(q => q.Fan >= 0).Select(q => new PointF(X(q.T), YR(Math.Min(rmax, q.Fan)))).ToArray();
                if (fp.Length >= 2) using (var pen = new Pen(fanColor, Theme.S(2))) g.DrawLines(pen, fp);
            }

            // legend
            int lx = p.Left + Theme.S(8), ly = p.Top + Theme.S(6);
            Legend(g, ref lx, ly, Theme.Warn, "Temperature (smoothed)");
            if (hasFan) Legend(g, ref lx, ly, fanColor, "Fan rpm");
            Legend(g, ref lx, ly, Color.FromArgb(160, 160, 255), "CPU budget");
            using (var pen = new Pen(Theme.Line)) g.DrawRectangle(pen, p);
        }

        void Legend(Graphics g, ref int x, int y, Color c, string text)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y + Theme.S(5), Theme.S(10), Theme.S(4));
            TextRenderer.DrawText(g, text, Font, new Point(x + Theme.S(14), y), Theme.Sub, TextFormatFlags.NoPadding);
            x += Theme.S(18) + TextRenderer.MeasureText(text, Font).Width + Theme.S(10);
        }
    }
}
