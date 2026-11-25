using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HPControl.App
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(10, 10, 12);
        public static readonly Color Nav = Color.FromArgb(20, 20, 26);
        public static readonly Color Card = Color.FromArgb(38, 38, 50);
        public static readonly Color CardHover = Color.FromArgb(48, 48, 62);
        public static readonly Color Line = Color.FromArgb(62, 62, 78);
        public static readonly Color Text = Color.FromArgb(240, 240, 245);
        public static readonly Color Sub = Color.FromArgb(165, 165, 185);
        public static readonly Color Dim = Color.FromArgb(110, 110, 130);
        public static readonly Color Accent = Color.FromArgb(108, 124, 255);
        public static readonly Color AccentDim = Color.FromArgb(70, 80, 170);
        public static readonly Color Good = Color.FromArgb(80, 200, 140);
        public static readonly Color Warn = Color.FromArgb(255, 180, 70);
        public static readonly Color Bad = Color.FromArgb(255, 95, 95);
        public static readonly Color Input = Color.FromArgb(28, 28, 36);

        public static float Scale = 1f;
        public static int S(float px) => (int)Math.Round(px * Scale);

        static string _family;
        public static string Family
        {
            get
            {
                if (_family != null) return _family;
                foreach (var f in new[] { "Segoe UI Variable Text", "Segoe UI" })
                {
                    using (var ff = new Font(f, 10)) if (ff.Name == f) { _family = f; return f; }
                }
                return _family = "Segoe UI";
            }
        }

        public static Font F(float size, FontStyle st = FontStyle.Regular) => new Font(Family, size, st, GraphicsUnit.Point);
        public static readonly Lazy<Font> Body = new Lazy<Font>(() => F(10));
        public static readonly Lazy<Font> Small = new Lazy<Font>(() => F(8.5f));
        public static readonly Lazy<Font> BodyBold = new Lazy<Font>(() => F(10, FontStyle.Bold));
        public static readonly Lazy<Font> H2 = new Lazy<Font>(() => F(13, FontStyle.Bold));
        public static readonly Lazy<Font> H1 = new Lazy<Font>(() => F(19, FontStyle.Bold));

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static Color Blend(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color ModeColor(string mode)
        {
            switch (mode)
            {
                case "Performance": return Color.FromArgb(255, 120, 90);
                case "Cool": return Color.FromArgb(90, 190, 255);
                case "Quiet": return Color.FromArgb(150, 130, 255);
                case "PowerSaver": return Color.FromArgb(100, 210, 140);
                case "Balanced": return Color.FromArgb(210, 210, 120);
                case "SmartSense": return Accent;
                default: return Sub;
            }
        }
    }
}
