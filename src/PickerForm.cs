using System; // Kiểu cơ bản: Func, Action, Math
using System.Drawing; // Point, PointF, Size, Color, Font, Image, Bitmap
using System.Globalization; // CultureInfo để in số dùng dấu chấm
using System.IO; // MemoryStream
using System.Threading.Tasks; // Task.Run chạy nền
using System.Windows.Forms; // Form, PictureBox, Label, Button, Clipboard

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Chụp màn hình Android, click để lấy toạ độ chuẩn hoá, kéo để lấy toạ độ vuốt.</summary>
    public class PickerForm : Form
    {
        readonly Func<byte[]> grab; // Hàm chụp màn hình, trả ảnh PNG dạng byte
        readonly Action<string> insert; // Hàm chèn chuỗi toạ độ vào nơi gọi (vd ô soạn macro)
        readonly PictureBox pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Cross }; // Khung hiện ảnh: lấp đầy, co giãn giữ tỉ lệ, nền đen, con trỏ chữ thập
        readonly Label info = new Label { Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Consolas", 10) }; // Dòng trạng thái ở đáy cửa sổ
        PointF? start; // Điểm bắt đầu khi nhấn chuột (null nếu không đang kéo)
        readonly Func<System.Collections.Generic.List<KeyMapper.Mark>> marks; // Điểm map phím cần vẽ (null = chỉ lấy toạ độ)

        /// <summary>Tạo cửa sổ, gắn các sự kiện chuột/phím và chụp ảnh khi hiện lên.
        /// marks != null: vẽ vòng tròn + tên phím của keymap lên ảnh (vẫn click được để lấy toạ độ).</summary>
        public PickerForm(Func<byte[]> grab, Action<string> insert, Func<System.Collections.Generic.List<KeyMapper.Mark>> marks = null)
        {
            this.grab = grab; // Lưu hàm chụp màn hình
            this.insert = insert; // Lưu hàm chèn kết quả
            this.marks = marks;
            Text = marks != null ? "Xem map phím - sửa keymap là vẽ lại ngay | click: lấy điểm | kéo: vuốt | F5: chụp lại"
                                 : "Lấy toạ độ - click: điểm | kéo: vuốt | F5: chụp lại"; // Tiêu đề cửa sổ kèm hướng dẫn
            if (marks != null) pb.Paint += (s, e) => DrawMarks(e.Graphics);
            Size = new Size(1000, 620); // Kích thước cửa sổ ban đầu
            if (Program.AppIcon != null) Icon = Program.AppIcon; // Dùng icon của app nếu có
            KeyPreview = true; // Form nhận phím trước control con (để bắt F5)
            var refresh = new Button { Text = "Chụp lại (F5)", Dock = DockStyle.Top, Height = 30 }; // Nút chụp lại ở trên cùng
            refresh.Click += (s, e) => Grab(); // Bấm nút thì chụp lại
            KeyDown += (s, e) => { if (e.KeyCode == Keys.F5) Grab(); }; // Nhấn F5 thì chụp lại
            Controls.Add(pb); // Thêm khung ảnh vào form
            Controls.Add(refresh); // Thêm nút chụp lại
            Controls.Add(info); // Thêm dòng trạng thái
            pb.MouseDown += (s, e) => { start = Norm(e.Location); }; // Nhấn chuột: ghi nhớ điểm bắt đầu (toạ độ chuẩn hoá)
            pb.MouseMove += (s, e) => // Di chuột: hiện toạ độ hiện tại
            {
                var p = Norm(e.Location); // Đổi vị trí chuột sang toạ độ 0..1
                if (p.HasValue) info.Text = "  " + F(p.Value.X) + " " + F(p.Value.Y) + (start.HasValue ? "   (đang kéo)" : ""); // Nằm trong ảnh thì hiện x y, kèm "(đang kéo)" nếu đang giữ chuột
            };
            pb.MouseUp += (s, e) => // Thả chuột: tạo kết quả điểm hoặc vuốt
            {
                var p = Norm(e.Location); // Toạ độ chuẩn hoá lúc thả
                if (!start.HasValue || !p.HasValue) { start = null; return; } // Bắt đầu hoặc kết thúc ngoài ảnh thì huỷ
                var a = start.Value; start = null; // Lấy điểm bắt đầu rồi xoá trạng thái kéo
                bool drag = Math.Abs(a.X - p.Value.X) + Math.Abs(a.Y - p.Value.Y) > 0.02; // Di chuyển quá 0.02 thì coi là vuốt
                string t = drag ? F(a.X) + " " + F(a.Y) + " " + F(p.Value.X) + " " + F(p.Value.Y) : F(p.Value.X) + " " + F(p.Value.Y); // Vuốt: "x1 y1 x2 y2", điểm: "x y"
                try { Clipboard.SetText(t); } catch { } // Copy vào clipboard, lỗi thì bỏ qua
                info.Text = "  Đã chèn + copy: " + t; // Báo kết quả trên dòng trạng thái
                if (insert != null) insert(t); // Gọi hàm chèn nếu có
            };
            Shown += (s, e) => Grab(); // Cửa sổ vừa hiện thì chụp ngay
        }

        /// <summary>Vẽ lại lớp phím (gọi khi nội dung keymap đổi).</summary>
        public void Redraw() { if (marks != null && !IsDisposed) pb.Invalidate(); }

        // Tên phím gọn cho dễ nhìn
        static string K(string k)
        {
            switch (k.ToLowerInvariant())
            {
                case "lshiftkey": case "shiftkey": case "rshiftkey": return "Shift";
                case "lcontrolkey": case "controlkey": case "rcontrolkey": return "Ctrl";
                case "lmenu": case "menu": case "rmenu": return "Alt";
                case "escape": return "Esc";
                case "oemtilde": return "~";
                case "up": return "↑"; case "down": return "↓"; case "left": return "←"; case "right": return "→";
            }
            if (k.Length == 2 && (k[0] == 'D' || k[0] == 'd') && char.IsDigit(k[1])) return k.Substring(1);
            if (k.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)) return "Num" + k.Substring(6);
            return k;
        }

        // Vẽ vòng tròn + nhãn phím của keymap lên vùng ảnh
        void DrawMarks(Graphics g)
        {
            var img = pb.Image; if (img == null) return;
            System.Collections.Generic.List<KeyMapper.Mark> list;
            try { list = marks(); } catch { return; }
            float sc = Math.Min((float)pb.ClientSize.Width / img.Width, (float)pb.ClientSize.Height / img.Height);
            float w = img.Width * sc, h = img.Height * sc, ox = (pb.ClientSize.Width - w) / 2, oy = (pb.ClientSize.Height - h) / 2;
            float sh = Math.Min(w, h); // Bán kính trong keymap tính theo cạnh ngắn
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var font = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var small = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                var legend = new System.Collections.Generic.List<string>();
                var used = new System.Collections.Generic.List<RectangleF>(); // Vùng nhãn đã vẽ
                foreach (var m in list)
                {
                    if (m.X < 0) { legend.Add(m.Label); continue; }
                    float cx = ox + (float)m.X * w, cy = oy + (float)m.Y * h;
                    Color col = m.Kind == "joy" ? Color.FromArgb(0, 200, 255) : m.Kind == "combo" ? Color.FromArgb(255, 160, 0)
                              : m.Kind == "repeat" ? Color.FromArgb(220, 90, 255) : Color.FromArgb(60, 220, 90);
                    if (m.Kind == "joy")
                    {
                        float r = Math.Max((float)m.R * sh, 20);
                        using (var br = new SolidBrush(Color.FromArgb(50, col))) g.FillEllipse(br, cx - r, cy - r, 2 * r, 2 * r);
                        using (var pen = new Pen(col, 2)) g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
                        var k = m.Label.Split(' '); // Lên Trái Xuống Phải
                        float d = r * 0.7f;
                        Tag(g, small, sf, col, K(k[0]), cx, cy - d); Tag(g, small, sf, col, K(k[1]), cx - d, cy);
                        Tag(g, small, sf, col, K(k[2]), cx, cy + d); Tag(g, small, sf, col, K(k[3]), cx + d, cy);
                        continue;
                    }
                    if (m.X2 >= 0) // Vuốt: mũi tên
                    {
                        using (var pen = new Pen(col, 3) { CustomEndCap = new System.Drawing.Drawing2D.AdjustableArrowCap(4, 5) })
                            g.DrawLine(pen, cx, cy, ox + (float)m.X2 * w, oy + (float)m.Y2 * h);
                    }
                    float rr = Math.Max((float)m.R * sh, 13); // Vòng tối thiểu 13px cho dễ thấy
                    using (var pen = new Pen(col, 2)) g.DrawEllipse(pen, cx - rr, cy - rr, 2 * rr, 2 * rr);
                    string lb = m.Label; int dot = lb.IndexOf('·');
                    lb = dot > 0 ? K(lb.Substring(0, dot)) + lb.Substring(dot) : K(lb);
                    // Nhãn trùng chỗ nhãn khác (vd combo bấm lại nút Q) -> xếp xuống dưới
                    var sz0 = g.MeasureString(lb, font);
                    float ty = cy;
                    for (int tries = 0; tries < 8; tries++)
                    {
                        var rc = new RectangleF(cx - sz0.Width / 2 - 3, ty - sz0.Height / 2, sz0.Width + 6, sz0.Height);
                        if (!used.Exists(u => u.IntersectsWith(rc))) { used.Add(rc); break; }
                        ty += sz0.Height + 1;
                    }
                    if (ty != cy) using (var pen = new Pen(Color.FromArgb(150, col), 1)) g.DrawLine(pen, cx, cy, cx, ty - sz0.Height / 2);
                    Tag(g, font, sf, col, lb, cx, ty);
                }
                if (legend.Count > 0) // Phím không có vị trí: liệt kê ở góc trên trái
                {
                    string t = string.Join("\n", legend.ConvertAll(K2));
                    var sz = g.MeasureString(t, small);
                    using (var br = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillRectangle(br, ox + 6, oy + 6, sz.Width + 10, sz.Height + 6);
                    g.DrawString(t, small, Brushes.White, ox + 11, oy + 9);
                }
                // Chú thích màu ở góc dưới trái
                float lx = ox + 6, ly = oy + h - 24;
                foreach (var it in new[] { Tuple.Create("tap", Color.FromArgb(60, 220, 90)), Tuple.Create("joy", Color.FromArgb(0, 200, 255)),
                                           Tuple.Create("combo", Color.FromArgb(255, 160, 0)), Tuple.Create("repeat", Color.FromArgb(220, 90, 255)) })
                {
                    var sz = g.MeasureString(it.Item1, small);
                    using (var br = new SolidBrush(Color.FromArgb(190, it.Item2))) g.FillRectangle(br, lx, ly, sz.Width + 8, 18);
                    g.DrawString(it.Item1, small, Brushes.Black, lx + 4, ly + 1);
                    lx += sz.Width + 14;
                }
            }
        }

        static string K2(string s) { int i = s.IndexOf(' '); return i > 0 ? K(s.Substring(0, i)) + s.Substring(i) : s; }

        // Nhãn chữ trên nền màu, căn giữa tại (x, y)
        static void Tag(Graphics g, Font f, StringFormat sf, Color col, string text, float x, float y)
        {
            var sz = g.MeasureString(text, f);
            var rc = new RectangleF(x - sz.Width / 2 - 3, y - sz.Height / 2, sz.Width + 6, sz.Height);
            using (var br = new SolidBrush(Color.FromArgb(200, col))) g.FillRectangle(br, rc);
            g.DrawString(text, f, Brushes.Black, rc, sf);
        }

        static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); } // In số 3 chữ số thập phân, dùng dấu chấm

        /// <summary>Đổi điểm chuột trên PictureBox thành toạ độ 0..1 trên ảnh; ngoài ảnh trả null.</summary>
        PointF? Norm(Point p)
        {
            var img = pb.Image; // Ảnh đang hiển thị
            if (img == null) return null; // Chưa có ảnh thì không tính được
            float sc = Math.Min((float)pb.ClientSize.Width / img.Width, (float)pb.ClientSize.Height / img.Height); // Tỉ lệ thu phóng của chế độ Zoom
            float w = img.Width * sc, h = img.Height * sc; // Kích thước ảnh thực vẽ trên màn hình
            float ox = (pb.ClientSize.Width - w) / 2, oy = (pb.ClientSize.Height - h) / 2; // Lề trái/trên do ảnh được căn giữa
            float x = (p.X - ox) / w, y = (p.Y - oy) / h; // Toạ độ chuẩn hoá theo chiều rộng/cao ảnh
            if (x < 0 || y < 0 || x > 1 || y > 1) return null; // Nằm ngoài ảnh thì trả null
            return new PointF(x, y); // Trả toạ độ chuẩn hoá
        }

        /// <summary>Chụp màn hình giả lập ở luồng nền rồi hiển thị lên PictureBox.</summary>
        void Grab()
        {
            info.Text = "  Đang chụp màn hình..."; // Báo đang chụp
            Task.Run(() => // Chạy nền để không đơ giao diện
            {
                byte[] png = null; // Dữ liệu ảnh PNG
                string err = null;
                try { png = grab(); } catch (Exception ex) { err = ex.Message; } // Gọi hàm chụp, lỗi thì giữ lý do
                BeginInvoke((Action)(() => // Quay về luồng giao diện để cập nhật control
                {
                    if (png == null || png.Length < 100) { info.Text = "  Chụp lỗi: " + (err ?? "không nhận được ảnh") + " - bấm F5 thử lại"; return; } // Không có ảnh hoặc quá nhỏ: báo lỗi
                    using (var ms = new MemoryStream(png)) // Bọc byte thành stream
                    using (var im = Image.FromStream(ms)) // Giải mã ảnh PNG
                    {
                        var old = pb.Image; // Giữ ảnh cũ để giải phóng sau
                        pb.Image = new Bitmap(im); // Tạo bản sao độc lập với stream rồi hiển thị
                        if (old != null) old.Dispose(); // Giải phóng ảnh cũ
                        info.Text = "  " + im.Width + "x" + im.Height + " - click vào vị trí cần lấy"; // Hiện kích thước ảnh và hướng dẫn
                    }
                }));
            });
        }
    }
}
