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

        /// <summary>Tạo cửa sổ, gắn các sự kiện chuột/phím và chụp ảnh khi hiện lên.</summary>
        public PickerForm(Func<byte[]> grab, Action<string> insert)
        {
            this.grab = grab; // Lưu hàm chụp màn hình
            this.insert = insert; // Lưu hàm chèn kết quả
            Text = "Lấy toạ độ - click: điểm | kéo: vuốt | F5: chụp lại"; // Tiêu đề cửa sổ kèm hướng dẫn
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
                try { png = grab(); } catch { } // Gọi hàm chụp, lỗi thì để null
                BeginInvoke((Action)(() => // Quay về luồng giao diện để cập nhật control
                {
                    if (png == null || png.Length < 100) { info.Text = "  Chụp lỗi - giả lập đã chạy và boot xong chưa?"; return; } // Không có ảnh hoặc quá nhỏ: báo lỗi
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
