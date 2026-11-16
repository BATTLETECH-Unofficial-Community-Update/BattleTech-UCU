// The launcher's button: one fixed size for every action, lightly styled. A flat warm-grey plate with a thin border and
// a short amber tick in two opposite corners, the banner's accent colour; primary actions (Play, Install) are slate with
// light text. It is still a Win32 BUTTON with its Text unchanged, so keyboard use, BM_CLICK and the window-message tests
// (test/win.ps1) behave as before; only the painting is its own.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReleaseGate
{
    sealed class UcuButton : Button
    {
        public enum Look { Normal, Primary, Compact }

        /// <summary>Every action button's size, at 96 DPI (the form scales it).</summary>
        public static readonly Size Standard = new Size(146, 32);
        /// <summary>Browse buttons beside a path box.</summary>
        public static readonly Size Small = new Size(96, 26);

        static readonly Color Accent = Color.FromArgb(214, 140, 48);
        static readonly Color Face = Color.FromArgb(232, 231, 227), FaceHover = Color.FromArgb(240, 239, 236), FacePressed = Color.FromArgb(220, 218, 213);
        static readonly Color Border = Color.FromArgb(176, 174, 168), Ink = Color.FromArgb(40, 42, 45);
        static readonly Color Slate = Color.FromArgb(58, 63, 68), SlateHover = Color.FromArgb(70, 76, 82), SlatePressed = Color.FromArgb(46, 50, 54);
        static readonly Color SlateInk = Color.FromArgb(244, 244, 242);
        static readonly Color Off = Color.FromArgb(236, 236, 233), OffBorder = Color.FromArgb(206, 205, 201), OffInk = Color.FromArgb(150, 152, 155);

        readonly Look _look;
        bool _hover, _pressed;

        public UcuButton(Look look = Look.Normal)
        {
            _look = look;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Size = look == Look.Compact ? Small : Standard;
            Margin = new Padding(3);
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        // The whole client area is painted in OnPaint.
        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            bool primary = _look == Look.Primary;
            Color fill, edge, ink;
            if (!Enabled) { fill = Off; edge = OffBorder; ink = OffInk; }
            else if (primary) { fill = _pressed ? SlatePressed : _hover ? SlateHover : Slate; edge = SlatePressed; ink = SlateInk; }
            else { fill = _pressed ? FacePressed : _hover ? FaceHover : Face; edge = _hover ? Accent : Border; ink = Ink; }

            float s = DeviceDpi / 96f;
            int w = Width - 1, h = Height - 1;
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, 0, 0, Width, Height);
            using (var p = new Pen(edge)) g.DrawRectangle(p, 0, 0, w, h);
            // A short amber tick in the top-left and bottom-right corners; none while disabled.
            if (Enabled)
            {
                int t = Math.Max(2, (int)Math.Round(2 * s)), len = (int)Math.Round(8 * s);
                using (var a = new SolidBrush(Accent))
                {
                    g.FillRectangle(a, 0, 0, len, t);
                    g.FillRectangle(a, 0, 0, t, len);
                    g.FillRectangle(a, Width - len, Height - t, len, t);
                    g.FillRectangle(a, Width - t, Height - len, t, len);
                }
            }
            if (Focused && ShowFocusCues && Enabled)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -(int)(4 * s), -(int)(4 * s)), ink, fill);
            Rectangle text = new Rectangle((int)(6 * s), 0, Width - (int)(12 * s), Height);
            if (_pressed) text.Offset(0, 1);
            TextRenderer.DrawText(g, Text ?? "", Font, text, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
