using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace HPControl.App
{
    static class Icons
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        /// <summary>Draws the app glyph (a stylised 4-blade fan in a rounded square).</summary>
        public static Bitmap Glyph(int size, Color? bg = null)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size;
                using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, s - 1, s - 1), s * 0.24f))
                using (var b = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), bg ?? Color.FromArgb(120, 136, 255), Color.FromArgb(70, 84, 220)))
                    g.FillPath(b, p);
                float cx = s / 2, cy = s / 2, r = s * 0.36f;
                using (var white = new SolidBrush(Color.White))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var st = g.Save();
                        g.TranslateTransform(cx, cy);
                        g.RotateTransform(i * 90 + 20);
                        using (var blade = new GraphicsPath())
                        {
                            blade.AddBezier(0, 0, r * 0.25f, -r * 0.55f, r * 0.95f, -r * 0.45f, r, 0);
                            blade.AddBezier(r, 0, r * 0.6f, r * 0.12f, r * 0.25f, r * 0.1f, 0, 0);
                            g.FillPath(white, blade);
                        }
                        g.Restore(st);
                    }
                    float hub = s * 0.13f;
                    using (var hb = new SolidBrush(Color.FromArgb(80, 96, 230))) g.FillEllipse(hb, cx - hub, cy - hub, hub * 2, hub * 2);
                    g.FillEllipse(white, cx - hub * 0.5f, cy - hub * 0.5f, hub, hub);
                }
            }
            return bmp;
        }

        public static Icon App(int size)
        {
            using (var bmp = Glyph(size))
            {
                IntPtr h = bmp.GetHicon();
                var ic = (Icon)Icon.FromHandle(h).Clone();
                try { DestroyIcon(h); } catch { }
                return ic;
            }
        }
    }
}
