using System;  // EventArgs, Math
using System.Drawing;  // Color, Graphics, Rectangle
using System.Drawing.Drawing2D;  // GraphicsPath, SmoothingMode
using System.Windows.Forms;  // Button, TextRenderer

namespace DsoftEmu
{
    /// <summary>
    /// Nút bo góc tự vẽ, mép mịn (khử răng cưa). Dùng như Button thường:
    /// màu nền = BackColor, khi rê/nhấn = FlatAppearance.MouseOverBackColor / MouseDownBackColor.
    /// </summary>
    public class RoundButton : Button
    {
        public int Radius = 10;  // Bán kính góc (px)
        bool hover, down;  // Đang rê chuột / đang nhấn

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override bool ShowFocusCues { get { return false; } }  // Không vẽ khung chấm khi focus

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color parentBg = Parent != null ? Parent.BackColor : SystemColors.Control;
            g.Clear(parentBg);  // Góc ngoài phần bo = màu nền phía sau
            Color bg = BackColor;
            if (Enabled && down && !FlatAppearance.MouseDownBackColor.IsEmpty) bg = FlatAppearance.MouseDownBackColor;
            else if (Enabled && (hover || down) && !FlatAppearance.MouseOverBackColor.IsEmpty) bg = FlatAppearance.MouseOverBackColor;
            if (!Enabled) bg = Blend(bg, parentBg, 0.35);  // Nút tắt: nhạt hơn

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var rc = new RectangleF(0, 0, Width - 1, Height - 1);  // Trừ 1px để nét khử răng cưa không bị cắt
            float d = Math.Min(Radius * 2f, Math.Min(rc.Width, rc.Height));
            if (bg != parentBg)
                using (var p = new GraphicsPath())
                using (var b = new SolidBrush(bg))
                {
                    p.AddArc(rc.X, rc.Y, d, d, 180, 90);
                    p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
                    p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
                    p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
                    p.CloseFigure();
                    g.FillPath(b, p);
                }

            Color fg = Enabled ? ForeColor : Blend(ForeColor, bg, 0.5);
            var tr = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            TextRenderer.DrawText(g, Text, Font, tr, fg, Flags(TextAlign));
        }

        internal static Color Blend(Color a, Color b, double k)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        }

        internal static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var rc = new RectangleF(r.X, r.Y, r.Width - 1, r.Height - 1);
            float d = Math.Min(radius * 2f, Math.Min(rc.Width, rc.Height));
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(c))
            {
                p.AddArc(rc.X, rc.Y, d, d, 180, 90);
                p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
                p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
                p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        internal static TextFormatFlags Flags(ContentAlignment a)
        {
            var f = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            if ((a & (ContentAlignment.TopLeft | ContentAlignment.MiddleLeft | ContentAlignment.BottomLeft)) != 0) f |= TextFormatFlags.Left;
            else if ((a & (ContentAlignment.TopRight | ContentAlignment.MiddleRight | ContentAlignment.BottomRight)) != 0) f |= TextFormatFlags.Right;
            else f |= TextFormatFlags.HorizontalCenter;
            if ((a & (ContentAlignment.TopLeft | ContentAlignment.TopCenter | ContentAlignment.TopRight)) != 0) f |= TextFormatFlags.Top;
            else if ((a & (ContentAlignment.BottomLeft | ContentAlignment.BottomCenter | ContentAlignment.BottomRight)) != 0) f |= TextFormatFlags.Bottom;
            else f |= TextFormatFlags.VerticalCenter;
            return f;
        }
    }

    /// <summary>Nhãn có nền bo góc (vd nhãn trạng thái "● Đã tắt"). Màu nền = BackColor, phần góc lấy màu nền phía sau.</summary>
    public class RoundLabel : Label
    {
        public int Radius = 8;  // Bán kính góc (px)

        public RoundLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color parentBg = Parent != null ? Parent.BackColor : SystemColors.Control;
            g.Clear(parentBg);
            RoundButton.FillRound(g, ClientRectangle, Radius, BackColor);
            var tr = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
            TextRenderer.DrawText(g, Text, Font, tr, ForeColor, RoundButton.Flags(TextAlign) & ~TextFormatFlags.EndEllipsis);
        }
    }
}
