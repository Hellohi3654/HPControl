using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace HPControl.App
{
    /// <summary>Vertical stacking container: children get the full width, laid out top to bottom.</summary>
    class Stack : Panel
    {
        public int Gap = Theme.S(12);
        public Padding Inner = new Padding(Theme.S(24));
        public int MaxContentWidth = Theme.S(900);
        public bool AutoHeight;

        public Stack()
        {
            DoubleBuffered = true;
            BackColor = Theme.Bg;
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = ClientSize.Width - Inner.Horizontal;
            int left = Inner.Left;
            if (MaxContentWidth > 0 && w > MaxContentWidth) { left += (w - MaxContentWidth) / 2; w = MaxContentWidth; }
            int y = Inner.Top + AutoScrollPosition.Y;
            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                c.SetBounds(left, y, Math.Max(10, w), c.Height);
                y += c.Height + Gap;
            }
            if (AutoHeight)
            {
                int h = y - AutoScrollPosition.Y - Gap + Inner.Bottom;
                if (Height != h) Height = Math.Max(h, Inner.Vertical);
            }
            else AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y + Inner.Bottom - Gap);
        }

        public T Add<T>(T c) where T : Control { Controls.Add(c); return c; }
    }

    /// <summary>Rounded card that stacks its children; height follows content.</summary>
    class Card : Stack
    {
        public string Title;
        public string Subtitle;
        int _headerH, _subH;

        public Card(string title = null, string subtitle = null)
        {
            Title = title; Subtitle = subtitle;
            BackColor = Theme.Bg;
            AutoHeight = true;
            AutoScroll = false;
            MaxContentWidth = 0;
            Gap = Theme.S(4);
            Inner = new Padding(Theme.S(22), Theme.S(18), Theme.S(22), Theme.S(18));
            UpdateHeader();
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        }

        public void UpdateHeader()
        {
            ComputeHeader();
            PerformLayout(); Invalidate();
        }

        void ComputeHeader()
        {
            _headerH = 0; _subH = 0;
            if (!string.IsNullOrEmpty(Title)) _headerH += Theme.S(30);
            if (!string.IsNullOrEmpty(Subtitle))
            {
                int w = Math.Max(Theme.S(200), Width - Theme.S(44));
                _subH = TextRenderer.MeasureText(Subtitle, Theme.Small.Value, new Size(w, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height;
                _headerH += _subH + Theme.S(8);
            }
            Inner = new Padding(Inner.Left, Theme.S(18) + _headerH, Inner.Right, Inner.Bottom);
        }

        int _lastW = -1;
        protected override void OnLayout(LayoutEventArgs e)
        {
            if (Width != _lastW) { _lastW = Width; ComputeHeader(); Invalidate(); }
            base.OnLayout(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
            Theme.Smooth(e.Graphics);
            using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(16)))
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillPath(b, p);
            float y = Theme.S(16);
            if (!string.IsNullOrEmpty(Title))
            {
                TextRenderer.DrawText(e.Graphics, Title, Theme.H2.Value, new Point(Inner.Left - Theme.S(2), (int)y), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                y += Theme.S(30);
            }
            if (!string.IsNullOrEmpty(Subtitle))
                TextRenderer.DrawText(e.Graphics, Subtitle, Theme.Small.Value, new Rectangle(Inner.Left, (int)y - Theme.S(4), Width - Inner.Horizontal, _subH + Theme.S(4)), Theme.Sub, TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Label + optional description on the left, a control on the right.</summary>
    class Row : Panel
    {
        public readonly Label Caption;
        public readonly Label Desc;
        public Control Trailing;
        public bool Separator = true;

        public Row(string caption, Control right = null, string desc = null, int height = 0)
        {
            DoubleBuffered = true;
            BackColor = Theme.Card;
            Caption = new Label { Text = caption, ForeColor = Theme.Text, Font = Theme.Body.Value, AutoSize = false, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false };
            Controls.Add(Caption);
            if (desc != null)
            {
                Desc = new Label { Text = desc, ForeColor = Theme.Sub, Font = Theme.Small.Value, AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false };
                Controls.Add(Desc);
            }
            if (right != null) { Trailing = right; Controls.Add(right); }
            _fixedH = height;
            Height = height > 0 ? height : (desc == null ? Theme.S(46) : Theme.S(60));
        }
        readonly int _fixedH;

        public void SetDesc(string s) { if (Desc != null && Desc.Text != s) { Desc.Text = s; PerformLayout(); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (Caption == null) return;
            int rw = Trailing != null ? Trailing.Width : 0;
            int textW = Width - rw - Theme.S(16);
            if (Desc == null)
                Caption.SetBounds(0, 0, textW, Height);
            else
            {
                int dh = string.IsNullOrEmpty(Desc.Text) ? Theme.S(18) : TextRenderer.MeasureText(Desc.Text, Desc.Font, new Size(Math.Max(50, textW), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
                if (_fixedH <= 0)
                {
                    // WinForms drops a layout request made from inside OnLayout, so resize and keep going
                    // (returning here left the caption, description and buttons where the first pass put them)
                    int want = Math.Max(Theme.S(60), Theme.S(36) + dh);
                    if (Height != want) Height = want;
                }
                Caption.SetBounds(0, Theme.S(8), textW, Theme.S(24));
                Desc.SetBounds(0, Theme.S(31), textW, Height - Theme.S(33));
            }
            if (Trailing != null) Trailing.Location = new Point(Width - rw, (Height - Trailing.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Separator) using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>Label that wraps and grows to fit its text (height = minimum height, already DPI-scaled).</summary>
    class Lbl : Label
    {
        readonly int _minH;
        public Lbl(string text, Font f = null, Color? c = null, int height = 0)
        {
            Text = text; Font = f ?? Theme.Body.Value; ForeColor = c ?? Theme.Sub; BackColor = Color.Transparent; UseMnemonic = false;
            AutoSize = false; _minH = height > 0 ? height : Theme.S(24); Height = _minH; TextAlign = ContentAlignment.MiddleLeft;
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
        void Fit()
        {
            if (Width < 20 || string.IsNullOrEmpty(Text)) return;
            var sz = TextRenderer.MeasureText(Text, Font, new Size(Width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            int want = Math.Max(_minH, sz.Height + Theme.S(6));
            if (Height != want) Height = want;
        }
    }

    class ValueLabel : Label
    {
        public ValueLabel(int width = 220)
        {
            ForeColor = Theme.Text; Font = Theme.Body.Value; BackColor = Color.Transparent; AutoSize = false; UseMnemonic = false;
            Width = Theme.S(width); Height = Theme.S(26); TextAlign = ContentAlignment.MiddleRight;
        }
    }

    class ToggleSwitch : Control
    {
        bool _on;
        public event EventHandler CheckedChanged;
        public bool Checked { get => _on; set { if (_on != value) { _on = value; Invalidate(); } } }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); SetStyle(ControlStyles.StandardDoubleClick, false);
            Size = new Size(Theme.S(46), Theme.S(24));
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!Enabled) return;
            _on = !_on; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            var col = !Enabled ? Theme.Line : _on ? Theme.Accent : Color.FromArgb(90, 90, 105);
            using (var p = Theme.Round(r, r.Height / 2)) using (var b = new SolidBrush(col)) e.Graphics.FillPath(b, p);
            float d = r.Height - Theme.S(6);
            float x = _on ? r.Right - d - Theme.S(3) : r.X + Theme.S(3);
            using (var b = new SolidBrush(Enabled ? Color.White : Theme.Dim)) e.Graphics.FillEllipse(b, x, r.Y + Theme.S(3), d, d);
        }
    }

    class FlatButton : Control
    {
        public bool Primary;
        bool _hover, _down;

        public FlatButton(string text, bool primary = false, int width = 0)
        {
            Text = text; Primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); SetStyle(ControlStyles.StandardDoubleClick, false);
            Font = Theme.Body.Value;
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            using (var g = CreateGraphics())
            {
                var sz = TextRenderer.MeasureText(g, text, Font);
                Size = new Size(width > 0 ? Theme.S(width) : sz.Width + Theme.S(36), Theme.S(36));
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var p = Theme.Round(r, r.Height / 2))
            {
                if (Primary)
                {
                    var c = !Enabled ? Theme.Line : _down ? Theme.AccentDim : _hover ? Theme.Blend(Theme.Accent, Color.White, 0.15) : Theme.Accent;
                    using (var b = new SolidBrush(c)) e.Graphics.FillPath(b, p);
                }
                else
                {
                    if (_hover && Enabled) using (var b = new SolidBrush(Color.FromArgb(_down ? 50 : 30, 255, 255, 255))) e.Graphics.FillPath(b, p);
                    using (var pen = new Pen(Enabled ? Color.FromArgb(150, 150, 170) : Theme.Line, Theme.S(1))) e.Graphics.DrawPath(pen, p);
                }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Round(r), Enabled ? Theme.Text : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>HP-style list of radio rows.</summary>
    class RadioList : Panel
    {
        public class Item { public string Key, Text, Desc; }
        readonly List<Item> _items = new List<Item>();
        public string Selected;
        public event EventHandler SelectionChanged;
        int _hover = -1;
        int RowH => Theme.S(46);

        public RadioList()
        {
            DoubleBuffered = true; BackColor = Theme.Card; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.StandardDoubleClick, false);
        }

        public void SetItems(IEnumerable<Item> items) { _items.Clear(); _items.AddRange(items); Height = RowH * _items.Count; Invalidate(); }
        public void Select(string key) { if (Selected != key) { Selected = key; Invalidate(); } }

        protected override void OnMouseMove(MouseEventArgs e) { int h = e.Y / RowH; if (h != _hover) { _hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = e.Y / RowH;
            if (i >= 0 && i < _items.Count && Enabled) { Selected = _items[i].Key; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var r = new Rectangle(0, i * RowH, Width, RowH);
                if (i == _hover) using (var b = new SolidBrush(Theme.CardHover)) e.Graphics.FillRectangle(b, r);
                var textR = new Rectangle(Theme.S(4), r.Y, Width - Theme.S(60), RowH);
                if (string.IsNullOrEmpty(it.Desc))
                    TextRenderer.DrawText(e.Graphics, it.Text, Theme.Body.Value, textR, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                else
                {
                    TextRenderer.DrawText(e.Graphics, it.Text, Theme.Body.Value, new Rectangle(textR.X, r.Y + Theme.S(4), textR.Width, Theme.S(22)), Theme.Text, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, it.Desc, Theme.Small.Value, new Rectangle(textR.X, r.Y + Theme.S(24), textR.Width, Theme.S(20)), Theme.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                float d = Theme.S(20), cx = Width - Theme.S(18) - d / 2, cy = r.Y + RowH / 2f;
                bool sel = it.Key == Selected;
                using (var p = new Pen(sel ? Theme.Accent : Color.FromArgb(150, 150, 165), Theme.S(1.6f))) e.Graphics.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillEllipse(b, cx - d / 4 - 1, cy - d / 4 - 1, d / 2 + 2, d / 2 + 2);
                if (i < _items.Count - 1) using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, r.Bottom - 1, Width, r.Bottom - 1);
            }
        }
    }

    class Segmented : Control
    {
        public string[] Keys = new string[0];
        public string[] Labels = new string[0];
        public string Selected;
        public event EventHandler SelectionChanged;

        public Segmented(int width = 260)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); SetStyle(ControlStyles.StandardDoubleClick, false);
            BackColor = Color.Transparent; Size = new Size(Theme.S(width), Theme.S(34)); Font = Theme.Body.Value; Cursor = Cursors.Hand;
        }

        public void Set(string[] keys, string[] labels) { Keys = keys; Labels = labels; Invalidate(); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (Keys.Length == 0 || !Enabled) return;
            int i = Math.Min(Keys.Length - 1, e.X * Keys.Length / Math.Max(1, Width));
            Selected = Keys[i]; Invalidate(); SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var p = Theme.Round(r, r.Height / 2)) using (var b = new SolidBrush(Theme.Input)) e.Graphics.FillPath(b, p);
            if (Keys.Length == 0) return;
            float w = r.Width / Keys.Length;
            for (int i = 0; i < Keys.Length; i++)
            {
                var cr = new RectangleF(r.X + i * w + 2, r.Y + 2, w - 4, r.Height - 4);
                bool sel = Keys[i] == Selected;
                if (sel) using (var p = Theme.Round(cr, cr.Height / 2)) using (var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Line)) e.Graphics.FillPath(b, p);
                TextRenderer.DrawText(e.Graphics, Labels[i], Font, Rectangle.Round(cr), sel ? Color.White : Theme.Sub, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
    }

    class Slider : Control
    {
        public double Min = 0, Max = 100, Value = 50, Step = 1;
        public string Format = "0";
        public string Unit = "";
        /// <summary>Optional custom value text (e.g. "No limit" at 0).</summary>
        public Func<double, string> Display;
        public bool ShowValue = true;
        public event EventHandler ValueChanged;
        public event EventHandler ValueCommitted;
        bool _drag;

        public Slider(int width = 260)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Size = new Size(Theme.S(width), Theme.S(30)); Cursor = Cursors.Hand; Font = Theme.Small.Value;
        }

        int ValueW => ShowValue ? Theme.S(58) : 0;
        float TrackX0 => Theme.S(8);
        float TrackX1 => Width - ValueW - Theme.S(8);

        void SetFromX(int x)
        {
            double t = (x - TrackX0) / Math.Max(1, TrackX1 - TrackX0);
            double v = Min + Math.Max(0, Math.Min(1, t)) * (Max - Min);
            if (Step > 0) v = Math.Round(v / Step) * Step;
            if (Math.Abs(v - Value) > 1e-9) { Value = v; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); }
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (!Enabled) return; _drag = true; Capture = true; SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (_drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { if (_drag) { _drag = false; Capture = false; ValueCommitted?.Invoke(this, EventArgs.Empty); } base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            float cy = Height / 2f;
            float t = (float)((Value - Min) / Math.Max(1e-9, Max - Min));
            float x = TrackX0 + t * (TrackX1 - TrackX0);
            using (var p = new Pen(Color.FromArgb(80, 80, 100), Theme.S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) e.Graphics.DrawLine(p, TrackX0, cy, TrackX1, cy);
            using (var p = new Pen(Enabled ? Theme.Accent : Theme.Line, Theme.S(4)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) e.Graphics.DrawLine(p, TrackX0, cy, x, cy);
            float d = Theme.S(16);
            using (var b = new SolidBrush(Enabled ? Theme.Accent : Theme.Line)) e.Graphics.FillEllipse(b, x - d / 2, cy - d / 2, d, d);
            if (ShowValue)
                TextRenderer.DrawText(e.Graphics, Display != null ? Display(Value) : Value.ToString(Format, CultureInfo.CurrentCulture) + Unit, Font, new Rectangle(Width - ValueW, 0, ValueW, Height), Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    class DarkCombo : ComboBox
    {
        public DarkCombo(int width = 240)
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Input; ForeColor = Theme.Text;
            Font = Theme.Body.Value;
            Width = Theme.S(width);
            ItemHeight = Theme.S(24);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.AccentDim : Theme.Input)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
        }
    }

    class DarkText : TextBox
    {
        public DarkText(int width = 120)
        {
            BorderStyle = BorderStyle.FixedSingle; BackColor = Theme.Input; ForeColor = Theme.Text; Font = Theme.Body.Value; Width = Theme.S(width);
        }

        public double Number(double def)
        {
            return double.TryParse(Text.Replace("°C", "").Replace("dBA", "").Replace("%", "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
                   double.TryParse(Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }
    }

    /// <summary>Horizontal row of controls (left to right).</summary>
    class Flow : Panel
    {
        public int Gap = Theme.S(10);
        public Flow(int height = 40) { Height = Theme.S(height); BackColor = Theme.Card; }
        /// <summary>Sizes the row to exactly fit its controls (so a row of buttons never gets cut off).</summary>
        public Flow Fit()
        {
            var vis = Controls.Cast<Control>().Where(c => c.Visible).ToList();
            Width = vis.Sum(c => c.Width) + Gap * Math.Max(0, vis.Count - 1);
            return this;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = 0;
            foreach (Control c in Controls) { if (!c.Visible) continue; c.Location = new Point(x, (Height - c.Height) / 2); x += c.Width + Gap; }
        }
    }

    /// <summary>Small big-number stat tile.</summary>
    class Stat : Control
    {
        public string Caption = "", Value = "–", Sub = "";
        public Color ValueColor = Theme.Text;
        public Stat(string caption)
        {
            Caption = caption;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(96);
        }
        public void Set(string v, string sub = null, Color? c = null)
        {
            if (v == Value && (sub ?? "") == Sub && (c ?? Theme.Text) == ValueColor) return;
            Value = v; Sub = sub ?? ""; ValueColor = c ?? Theme.Text; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Theme.Bg);
            Theme.Smooth(e.Graphics);
            using (var p = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Theme.S(14))) using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Caption, Theme.Small.Value, new Point(Theme.S(16), Theme.S(12)), Theme.Sub, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, Value, Theme.H1.Value, new Rectangle(Theme.S(14), Theme.S(30), Width - Theme.S(20), Theme.S(38)), ValueColor, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, Sub, Theme.Small.Value, new Rectangle(Theme.S(16), Theme.S(70), Width - Theme.S(24), Theme.S(20)), Theme.Sub, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Row of equally sized children.</summary>
    class Grid : Panel
    {
        public int Gap = Theme.S(12);
        public int Columns;
        public Grid(int columns, int height) { Columns = columns; Height = Theme.S(height); BackColor = Theme.Bg; }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var vis = Controls.Cast<Control>().Where(c => c.Visible).ToList();
            int cols = Math.Max(1, Columns);
            int w = (Width - Gap * (cols - 1)) / cols;
            for (int i = 0; i < vis.Count; i++)
            {
                int row = i / cols, col = i % cols;
                vis[i].SetBounds(col * (w + Gap), row * (vis[i].Height + Gap), w, vis[i].Height);
            }
        }
    }
}
