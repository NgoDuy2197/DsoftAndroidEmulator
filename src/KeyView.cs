using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Mark = DsoftEmu.KeyMapper.Mark;

namespace DsoftEmu
{
    /// <summary>Vẽ vòng tròn + tên phím của keymap lên một vùng (dùng cho cửa sổ Sửa phím và lớp tên phím trên giả lập).</summary>
    public static class KeyDraw
    {
        public static readonly Color CTap = Color.FromArgb(60, 220, 90), CJoy = Color.FromArgb(0, 200, 255),
                                     CCombo = Color.FromArgb(255, 160, 0), CRepeat = Color.FromArgb(220, 90, 255);

        public static Color ColorOf(string kind)
        {
            return kind == "joy" ? CJoy : kind == "combo" ? CCombo : kind == "repeat" ? CRepeat : CTap;
        }

        /// <summary>Tên phím gọn cho dễ nhìn.</summary>
        public static string K(string k)
        {
            if (k == null) return "";
            switch (k.ToLowerInvariant())
            {
                case "lshiftkey": case "shiftkey": case "rshiftkey": return "Shift";
                case "lcontrolkey": case "controlkey": case "rcontrolkey": return "Ctrl";
                case "lmenu": case "menu": case "rmenu": return "Alt";
                case "escape": return "Esc";
                case "return": case "enter": return "Enter";
                case "oemtilde": return "~";
                case "up": return "↑"; case "down": return "↓"; case "left": return "←"; case "right": return "→";
            }
            if (k.Length == 2 && (k[0] == 'D' || k[0] == 'd') && char.IsDigit(k[1])) return k.Substring(1);
            if (k.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)) return "Num" + k.Substring(6);
            return k;
        }

        static string Lbl(Mark m)
        {
            string lb = m.Label; int dot = lb.IndexOf('·');
            return dot > 0 ? K(lb.Substring(0, dot)) + lb.Substring(dot) : K(lb);
        }

        /// <summary>Bán kính vòng (px) của một điểm khi vẽ trong vùng có cạnh ngắn sh.</summary>
        public static float Radius(Mark m, float sh) { return m.Kind == "joy" ? Math.Max((float)m.R * sh, 20) : Math.Max((float)m.R * sh, 13); }

        /// <summary>hud = vẽ lên giả lập (màu đặc, không khử răng cưa vì nền là màu trong suốt, không chú thích).</summary>
        public static void Draw(Graphics g, RectangleF area, List<Mark> list, bool hud, Mark hot)
        {
            if (list == null) return;
            float w = area.Width, h = area.Height, ox = area.X, oy = area.Y, sh = Math.Min(w, h);
            g.SmoothingMode = hud ? SmoothingMode.None : SmoothingMode.AntiAlias;
            g.TextRenderingHint = hud ? TextRenderingHint.SingleBitPerPixelGridFit : TextRenderingHint.AntiAliasGridFit;
            int a = hud ? 255 : 200;
            using (var font = new Font("Segoe UI", hud ? 9f : 10f, FontStyle.Bold))
            using (var small = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                var legend = new List<string>();
                var used = new List<RectangleF>(); // Vùng nhãn đã vẽ (tránh đè nhau)
                foreach (var m in list)
                {
                    if (m.X < 0) { legend.Add(K(m.Key) + m.Label.Substring(m.Key.Length)); continue; }
                    float cx = ox + (float)m.X * w, cy = oy + (float)m.Y * h;
                    Color col = ColorOf(m.Kind);
                    float pw = m == hot ? 4 : 2;
                    if (m.Kind == "joy")
                    {
                        float r = Radius(m, sh);
                        if (!hud) using (var br = new SolidBrush(Color.FromArgb(50, col))) g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
                        using (var pen = new Pen(col, pw)) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
                        var k = m.Label.Split(' ');
                        float d = r * 0.7f;
                        if (k.Length == 4)
                        {
                            Tag(g, small, sf, col, a, K(k[0]), cx, cy - d); Tag(g, small, sf, col, a, K(k[1]), cx - d, cy);
                            Tag(g, small, sf, col, a, K(k[2]), cx, cy + d); Tag(g, small, sf, col, a, K(k[3]), cx + d, cy);
                        }
                        continue;
                    }
                    if (m.X2 >= 0) // Vuốt: mũi tên
                        using (var pen = new Pen(col, 3) { CustomEndCap = new AdjustableArrowCap(4, 5) })
                            g.DrawLine(pen, cx, cy, ox + (float)m.X2 * w, oy + (float)m.Y2 * h);
                    float rr = Radius(m, sh);
                    using (var pen = new Pen(col, pw)) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                    string lb = Lbl(m);
                    var sz0 = g.MeasureString(lb, font);
                    float ty = cy;
                    for (int tries = 0; tries < 8; tries++)
                    {
                        var rc = new RectangleF(cx - sz0.Width / 2 - 3, ty - sz0.Height / 2, sz0.Width + 6, sz0.Height);
                        if (!used.Exists(u => u.IntersectsWith(rc))) { used.Add(rc); break; }
                        ty += sz0.Height + 1;
                    }
                    if (ty != cy) using (var pen = new Pen(col, 1)) g.DrawLine(pen, cx, cy, cx, ty - sz0.Height / 2);
                    Tag(g, font, sf, col, a, lb, cx, ty);
                }
                if (hud) return;
                if (legend.Count > 0) // Phím không có vị trí: liệt kê ở góc trên trái
                {
                    string t = string.Join("\n", legend);
                    var sz = g.MeasureString(t, small);
                    using (var br = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(br, ox + 6, oy + 6, sz.Width + 10, sz.Height + 6);
                    g.DrawString(t, small, Brushes.White, ox + 11, oy + 9);
                }
                float lx = ox + 6, ly = oy + h - 24;
                foreach (var it in new[] { Tuple.Create("tap", CTap), Tuple.Create("joy", CJoy), Tuple.Create("combo", CCombo), Tuple.Create("repeat", CRepeat) })
                {
                    var sz = g.MeasureString(it.Item1, small);
                    using (var br = new SolidBrush(Color.FromArgb(190, it.Item2))) g.FillRectangle(br, lx, ly, sz.Width + 8, 18);
                    g.DrawString(it.Item1, small, Brushes.Black, lx + 4, ly + 1);
                    lx += sz.Width + 14;
                }
            }
        }

        // Nhãn chữ trên nền màu, căn giữa tại (x, y)
        static void Tag(Graphics g, Font f, StringFormat sf, Color col, int alpha, string text, float x, float y)
        {
            var sz = g.MeasureString(text, f);
            var rc = new RectangleF(x - sz.Width / 2 - 3, y - sz.Height / 2, sz.Width + 6, sz.Height);
            using (var br = new SolidBrush(Color.FromArgb(alpha, col))) g.FillRectangle(br, rc);
            g.DrawString(text, f, Brushes.Black, rc, sf);
        }
    }

    /// <summary>Cửa sổ Sửa map phím: ảnh màn hình + vòng tròn phím.
    /// Kéo vòng tròn = dời; click / kéo chỗ trống = thêm phím (chạm / vuốt); chuột phải vòng tròn = đổi phím / xoá.</summary>
    public class KeyViewForm : Form
    {
        readonly Func<byte[]> grab;
        readonly Func<string> getText;
        readonly Action<string> setText;
        readonly PictureBox pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Cross };
        readonly Label info = new Label { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Consolas", 10) };
        List<Mark> marks = new List<Mark>();
        Mark drag; bool moved;          // Đang kéo vòng tròn nào
        PointF? start, cur;             // Đang click/kéo ở chỗ trống

        public KeyViewForm(Func<byte[]> grab, Func<string> getText, Action<string> setText)
        {
            this.grab = grab; this.getText = getText; this.setText = setText;
            Text = "Sửa map phím - kéo vòng tròn: dời | click / kéo chỗ trống: thêm phím | chuột phải vòng tròn: đổi phím, xoá | F5: chụp lại";
            Size = new Size(1100, 680);
            if (Program.AppIcon != null) Icon = Program.AppIcon;
            KeyPreview = true;
            var refresh = new Button { Text = "Chụp lại màn hình (F5)", Dock = DockStyle.Top, Height = 30 };
            refresh.Click += (s, e) => Grab();
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) Grab(); };
            Controls.Add(pb); Controls.Add(refresh); Controls.Add(info);
            pb.Paint += OnPaintPb;
            pb.MouseDown += OnDown;
            pb.MouseMove += OnMove;
            pb.MouseUp += OnUp;
            Shown += (s, e) => { Redraw(); Grab(); };
        }

        /// <summary>Đọc lại keymap và vẽ lại (gọi khi ô soạn đổi).</summary>
        public void Redraw()
        {
            if (IsDisposed || drag != null) return; // Đang kéo thì giữ vị trí tạm
            marks = KeyMapper.Marks(getText());
            pb.Invalidate();
        }

        static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        RectangleF ImgRect()
        {
            var img = pb.Image;
            if (img == null) return RectangleF.Empty;
            float sc = Math.Min((float)pb.ClientSize.Width / img.Width, (float)pb.ClientSize.Height / img.Height);
            float w = img.Width * sc, h = img.Height * sc;
            return new RectangleF((pb.ClientSize.Width - w) / 2, (pb.ClientSize.Height - h) / 2, w, h);
        }

        PointF? Norm(Point p)
        {
            var r = ImgRect();
            if (r.Width <= 0) return null;
            float x = (p.X - r.X) / r.Width, y = (p.Y - r.Y) / r.Height;
            if (x < 0 || y < 0 || x > 1 || y > 1) return null;
            return new PointF(x, y);
        }

        // Vòng tròn dưới chuột: ưu tiên vòng nhỏ (tap/combo) trước joystick
        Mark Hit(Point p)
        {
            var r = ImgRect();
            if (r.Width <= 0) return null;
            float sh = Math.Min(r.Width, r.Height);
            Func<Mark, float> dist = m => (float)Math.Sqrt(Math.Pow(r.X + m.X * r.Width - p.X, 2) + Math.Pow(r.Y + m.Y * r.Height - p.Y, 2));
            for (int i = marks.Count - 1; i >= 0; i--)
                if (marks[i].X >= 0 && marks[i].Kind != "joy" && dist(marks[i]) <= KeyDraw.Radius(marks[i], sh) + 5) return marks[i];
            for (int i = marks.Count - 1; i >= 0; i--)
                if (marks[i].X >= 0 && marks[i].Kind == "joy" && dist(marks[i]) <= KeyDraw.Radius(marks[i], sh)) return marks[i];
            return null;
        }

        void OnPaintPb(object s, PaintEventArgs e)
        {
            var r = ImgRect();
            if (r.Width <= 0) return;
            KeyDraw.Draw(e.Graphics, r, marks, false, drag);
            if (start.HasValue && cur.HasValue) // Đang kéo chỗ trống: xem trước đường vuốt
                using (var pen = new Pen(Color.White, 2) { DashStyle = DashStyle.Dash, CustomEndCap = new AdjustableArrowCap(4, 5) })
                    e.Graphics.DrawLine(pen, r.X + start.Value.X * r.Width, r.Y + start.Value.Y * r.Height, r.X + cur.Value.X * r.Width, r.Y + cur.Value.Y * r.Height);
        }

        void OnDown(object s, MouseEventArgs e)
        {
            var h = Hit(e.Location);
            if (e.Button == MouseButtons.Right) { if (h != null) Menu(h, e.Location); return; }
            if (e.Button != MouseButtons.Left) return;
            if (h != null) { drag = h; moved = false; pb.Cursor = Cursors.SizeAll; }
            else { start = Norm(e.Location); cur = start; }
        }

        void OnMove(object s, MouseEventArgs e)
        {
            var n = Norm(e.Location);
            if (drag != null && n.HasValue)
            {
                double dx = n.Value.X - drag.X, dy = n.Value.Y - drag.Y;
                if (Math.Abs(dx) + Math.Abs(dy) > 0.002) moved = true;
                if (drag.X2 >= 0) { drag.X2 += dx; drag.Y2 += dy; }
                drag.X = n.Value.X; drag.Y = n.Value.Y;
                pb.Invalidate();
            }
            else if (start.HasValue && n.HasValue) { cur = n; pb.Invalidate(); }
            if (n.HasValue)
            {
                var h = drag ?? Hit(e.Location);
                info.Text = "  " + F(n.Value.X) + " " + F(n.Value.Y) + (h != null ? "   [" + KeyDraw.K(h.Key) + " - " + h.Kind + ", dòng " + (h.Line + 1) + "]" : "");
                if (drag == null && !start.HasValue) pb.Cursor = h != null ? Cursors.Hand : Cursors.Cross;
            }
        }

        void OnUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (drag != null)
            {
                var m = drag; drag = null; pb.Cursor = Cursors.Cross;
                if (moved)
                {
                    setText(KeyMapper.Move(getText(), m, m.X, m.Y));
                    info.Text = "  Đã dời " + KeyDraw.K(m.Key) + " tới " + F(m.X) + " " + F(m.Y) + " (đã lưu + áp dụng)";
                }
                Redraw();
                return;
            }
            if (!start.HasValue) return;
            var a = start.Value; var b = Norm(e.Location) ?? cur ?? a;
            start = null; cur = null; pb.Invalidate();
            bool swipe = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) > 0.02;
            Add(a, swipe ? b : (PointF?)null);
        }

        // Thêm phím mới cho thao tác vừa click / kéo
        void Add(PointF a, PointF? b)
        {
            string what = b == null ? "Chạm tại " + F(a.X) + " " + F(a.Y)
                                    : "Vuốt từ " + F(a.X) + " " + F(a.Y) + " tới " + F(b.Value.X) + " " + F(b.Value.Y);
            string[] modes = b == null
                ? new[] { "tap - giữ phím = giữ chạm (nút thường)", "repeat - giữ phím = chạm liên tục (spam)", "combo - bấm = chạm 1 lần" }
                : new[] { "combo - bấm = vuốt 1 lần", "repeat - giữ phím = vuốt liên tục" };
            using (var d = new KeyAssignDialog(what, modes))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string text = getText();
                if (!ResolveConflict(ref text, d.Key, -1)) return;
                string p = F(a.X) + " " + F(a.Y), line;
                if (b == null)
                    line = d.Mode == 0 ? "tap " + d.Key + " " + p
                         : d.Mode == 1 ? "repeat " + d.Key + " tap " + p + " 0.01; wait 60 100"
                         : "combo " + d.Key + " tap " + p;
                else
                {
                    string sw = "swipe " + p + " " + F(b.Value.X) + " " + F(b.Value.Y) + " 150 250";
                    line = d.Mode == 0 ? "combo " + d.Key + " " + sw : "repeat " + d.Key + " " + sw + "; wait 80 150";
                }
                setText(text.TrimEnd('\r', '\n') + "\r\n" + line + "\r\n");
                info.Text = "  Đã thêm: " + line + " (đã lưu + áp dụng)";
                Redraw();
            }
        }

        // Phím đã dùng ở dòng khác: hỏi thay thế. Trả false nếu huỷ.
        bool ResolveConflict(ref string text, string key, int exceptLine)
        {
            var used = KeyMapper.LinesUsing(text, key).Where(i => i != exceptLine).ToList();
            if (used.Count == 0) return true;
            var lines = text.Replace("\r", "").Split('\n');
            bool joy = used.Any(i => lines[i].TrimStart().StartsWith("joy", StringComparison.OrdinalIgnoreCase));
            var r = MessageBox.Show(this, "Phím " + key + " đang được gán ở dòng " + string.Join(", ", used.Select(i => (i + 1).ToString())) + ":\r\n" +
                string.Join("\r\n", used.Select(i => "  " + lines[i].Trim())) + "\r\n\r\n" +
                (joy ? "Phím này thuộc joystick - chỉ gỡ được các dòng khác, joystick giữ nguyên.\r\n\r\n" : "") +
                "Có = gỡ gán cũ rồi gán mới\r\nKhông = vẫn gán thêm (dòng sau đè dòng trước)", "Phím đã được dùng",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes)
                foreach (var i in used.OrderByDescending(x => x))
                    if (!lines[i].TrimStart().StartsWith("joy", StringComparison.OrdinalIgnoreCase)) text = KeyMapper.RemoveLine(text, i);
            return true;
        }

        // Menu chuột phải trên vòng tròn
        void Menu(Mark m, Point at)
        {
            var cm = new ContextMenuStrip();
            if (m.Kind == "joy")
                cm.Items.Add("Joystick: đổi 4 phím trong ô soạn (dòng " + (m.Line + 1) + ")").Enabled = false;
            else
                cm.Items.Add("Đổi phím " + KeyDraw.K(m.Key) + "...", null, (s, e) =>
                {
                    using (var d = new KeyAssignDialog("Đổi phím cho dòng " + (m.Line + 1) + " (đang là " + m.Key + ")", null))
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        string text = getText();
                        var old = text.Replace("\r", "").Split('\n');
                        // Gỡ dòng trùng phím ở phía trên thì số dòng bị dịch -> tính lại theo số dòng bị gỡ phía trên
                        if (!ResolveConflict(ref text, d.Key, m.Line)) return;
                        var now = text.Replace("\r", "").Split('\n');
                        int line = m.Line - (old.Length - now.Length);
                        while (line < now.Length - 1 && now[line] != old[m.Line]) line++;
                        if (line < 0 || line >= now.Length || now[line] != old[m.Line]) return;
                        setText(KeyMapper.Rekey(text, line, d.Key));
                        info.Text = "  Đã đổi " + m.Key + " → " + d.Key;
                        Redraw();
                    }
                });
            cm.Items.Add("Chép toạ độ " + F(m.X) + " " + F(m.Y), null, (s, e) => { try { Clipboard.SetText(F(m.X) + " " + F(m.Y)); } catch { } });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Xoá dòng " + (m.Line + 1) + (m.Step >= 0 ? " (cả combo)" : ""), null, (s, e) =>
            {
                if (MessageBox.Show(this, "Xoá gán phím " + m.Key + " (dòng " + (m.Line + 1) + ")?", "Xoá", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                setText(KeyMapper.RemoveLine(getText(), m.Line));
                Redraw();
            });
            cm.Show(pb, at);
        }

        void Grab()
        {
            info.Text = "  Đang chụp màn hình...";
            Task.Run(() =>
            {
                byte[] png = null; string err = null;
                try { png = grab(); } catch (Exception ex) { err = ex.Message; }
                if (IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (png == null || png.Length < 100) { info.Text = "  Chụp lỗi: " + (err ?? "không nhận được ảnh") + " - bấm F5 thử lại"; return; }
                        using (var ms = new MemoryStream(png))
                        using (var im = Image.FromStream(ms))
                        {
                            var old = pb.Image;
                            pb.Image = new Bitmap(im);
                            if (old != null) old.Dispose();
                        }
                        info.Text = "  " + pb.Image.Width + "x" + pb.Image.Height + " - kéo vòng tròn để dời, click/kéo chỗ trống để thêm phím";
                        Redraw();
                    }));
                }
                catch (InvalidOperationException) { } // Cửa sổ đã đóng
            });
        }
    }

    /// <summary>Hộp thoại bấm phím cần gán (+ chọn kiểu hành động nếu có).</summary>
    public class KeyAssignDialog : Form
    {
        public string Key;  // Tên phím (theo enum Keys)
        public int Mode;    // Kiểu hành động đã chọn
        readonly Label keyLbl = new Label { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = Color.FromArgb(37, 99, 235), Margin = new Padding(0, 8, 0, 8) };
        readonly Button ok = new Button { Text = "Gán", DialogResult = DialogResult.OK, Enabled = false, Width = 90, Height = 30 };
        readonly List<RadioButton> radios = new List<RadioButton>();
        bool capturing = true;

        public KeyAssignDialog(string what, string[] modes)
        {
            Text = "Gán phím";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9.5f); Padding = new Padding(14);
            var box = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            box.Controls.Add(new Label { Text = what, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold) });
            box.Controls.Add(keyLbl);
            if (modes != null)
                for (int i = 0; i < modes.Length; i++)
                {
                    int k = i;
                    var rb = new RadioButton { Text = modes[i], AutoSize = true, Checked = i == 0, TabStop = false };
                    rb.CheckedChanged += (s, e) => { if (rb.Checked) Mode = k; };
                    radios.Add(rb); box.Controls.Add(rb);
                }
            var bar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            var again = new Button { Text = "Bấm lại phím", Width = 110, Height = 30 };
            again.Click += (s, e) => { capturing = true; keyLbl.Text = "Bấm phím cần gán..."; ok.Enabled = false; Key = null; };
            var cancel = new Button { Text = "Huỷ", DialogResult = DialogResult.Cancel, Width = 80, Height = 30 };
            bar.Controls.Add(ok); bar.Controls.Add(again); bar.Controls.Add(cancel);
            box.Controls.Add(bar);
            box.Controls.Add(new Label { AutoSize = true, ForeColor = Color.Gray, Text = "Phím bất kỳ (kể cả Space, Enter, Esc, Shift...). Bấm xong chọn kiểu rồi bấm Gán." });
            Controls.Add(box);
            keyLbl.Text = "Bấm phím cần gán...";
            CancelButton = null; // Esc cũng là phím gán được
        }

        // Bắt mọi phím (kể cả Tab/Enter/Esc/Alt) khi đang chờ phím
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
            var k = keyData & Keys.KeyCode;
            if (k == Keys.None) return true;
            if (k == Keys.ShiftKey) k = Keys.LShiftKey;
            else if (k == Keys.ControlKey) k = Keys.LControlKey;
            else if (k == Keys.Menu) k = Keys.LMenu;
            Key = k.ToString();
            keyLbl.Text = "Phím: " + Key;
            capturing = false; ok.Enabled = true;
            ok.Focus();
            return true;
        }
    }

    /// <summary>Lớp tên phím đè lên cửa sổ giả lập (chuột/phím xuyên qua, không đổi gì trong Android).</summary>
    public sealed class KeyHud : Form
    {
        static readonly Dictionary<int, KeyHud> on = new Dictionary<int, KeyHud>();
        readonly Func<IntPtr> target;
        readonly Func<List<Mark>> source;
        readonly Timer tick = new Timer { Interval = 80 };
        List<Mark> cache = new List<Mark>();
        static readonly Color Key = Color.FromArgb(255, 0, 255); // Màu trong suốt

        public static bool IsOn(int port) { return on.ContainsKey(port); }

        /// <summary>Bật/tắt cho máy có cổng port; trả trạng thái sau khi bấm. Gọi ở luồng giao diện.</summary>
        public static bool Toggle(int port, Func<IntPtr> target, Func<List<Mark>> source)
        {
            KeyHud f;
            if (on.TryGetValue(port, out f)) { f.Close(); return false; }
            f = new KeyHud(target, source);
            on[port] = f;
            f.FormClosed += (s, e) => on.Remove(port);
            IntPtr old = PerMonitor();
            try { f.Show(); } finally { Restore(old); }
            return true;
        }

        /// <summary>Tắt của một máy hoặc tất cả (port &lt; 0).</summary>
        public static void Off(int port)
        {
            foreach (var kv in new List<KeyValuePair<int, KeyHud>>(on))
                if (port < 0 || kv.Key == port) kv.Value.Close();
        }

        /// <summary>Keymap đổi -> vẽ lại mọi lớp.</summary>
        public static void RefreshAll() { foreach (var f in on.Values) f.Reload(); }

        KeyHud(Func<IntPtr> target, Func<List<Mark>> source)
        {
            this.target = target; this.source = source;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Key; DoubleBuffered = true;
            Bounds = new Rectangle(-32000, -32000, 1, 1);
            tick.Tick += (s, e) => { IntPtr old = PerMonitor(); try { Follow(); } finally { Restore(old); } };
        }

        void Reload() { try { cache = source(); } catch { cache = new List<Mark>(); } Invalidate(); }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000; return cp; } // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0x00FF00FF, 235, 0x1 | 0x2); // LWA_COLORKEY (hồng = trong suốt) | LWA_ALPHA
            Reload(); Follow(); tick.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Key);
            KeyDraw.Draw(e.Graphics, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), cache, true, null);
        }

        // Bám vùng hiển thị (client) của cửa sổ giả lập
        void Follow()
        {
            IntPtr h = target();
            RECT rc; POINT p = new POINT();
            if (h == IntPtr.Zero || IsIconic(h) || !IsWindowVisible(h) || !GetClientRect(h, out rc) || rc.R < 10 || !ClientToScreen(h, ref p))
            {
                if (Left > -30000) SetWindowPos(Handle, IntPtr.Zero, -32000, -32000, 1, 1, 0x10 | 0x4);
                return;
            }
            KeepAbove(h);
            RECT me; GetWindowRect(Handle, out me);
            if (me.L != p.X || me.T != p.Y || me.R - me.L != rc.R || me.B - me.T != rc.B)
            {
                SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, rc.R, rc.B, 0x10 | 0x4);
                Invalidate();
            }
        }

        // Nằm ngay trên cửa sổ giả lập (và trên lớp phủ tối nếu có)
        void KeepAbove(IntPtr h)
        {
            IntPtr below = h, prev = GetWindow(h, 3);
            while (prev != IntPtr.Zero && prev != Handle && ScreenFilter.IsFilterWindow(prev)) { below = prev; prev = GetWindow(prev, 3); }
            if (prev == Handle) return;
            bool emuTop = (GetWindowLong(h, -20) & 0x8) != 0, meTop = (GetWindowLong(Handle, -20) & 0x8) != 0;
            const uint F = 0x1 | 0x2 | 0x10;
            if (emuTop != meTop) SetWindowPos(Handle, emuTop ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, F);
            bool prevTop = prev != IntPtr.Zero && (GetWindowLong(prev, -20) & 0x8) != 0;
            IntPtr after = prev == IntPtr.Zero || prevTop != emuTop ? (emuTop ? new IntPtr(-1) : IntPtr.Zero) : prev;
            SetWindowPos(Handle, after, 0, 0, 0, 0, F);
        }

        protected override void OnFormClosed(FormClosedEventArgs e) { tick.Stop(); tick.Dispose(); base.OnFormClosed(e); }

        static IntPtr PerMonitor() { try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { return IntPtr.Zero; } }
        static void Restore(IntPtr old) { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    }
}
