using System;  // Kiểu cơ bản: Action, Func, Math, DateTime...
using System.Collections.Generic;  // List, Dictionary
using System.ComponentModel;  // Win32Exception (lỗi khi huỷ quyền Admin)
using System.Diagnostics;  // Process, ProcessStartInfo
using System.Drawing;  // Color, Font, Size
using System.IO;  // File, Directory, Path, DriveInfo
using System.Linq;  // LINQ: Select, OrderBy, FirstOrDefault...
using System.Threading.Tasks;  // Task.Run để chạy việc ở luồng nền
using System.Windows.Forms;  // WinForms: Form, Button, Label...

// Không gian tên chung của ứng dụng
namespace DsoftEmu
{
    // Cửa sổ chính: thanh menu bên trái + các trang nội dung + ô log ở dưới
    public class MainForm : Form
    {
        // ---------- màu giao diện ----------
        static readonly Color CSide = Color.FromArgb(24, 26, 33);  // Nền thanh bên (sidebar)
        static readonly Color CSideHover = Color.FromArgb(38, 41, 52);  // Nền nút menu khi rê chuột
        static readonly Color CSideActive = Color.FromArgb(55, 60, 78);  // Nền nút menu đang được chọn
        static readonly Color CBg = Color.FromArgb(242, 244, 248);  // Nền vùng nội dung
        static readonly Color CCard = Color.White;  // Nền các thẻ (card)
        static readonly Color CText = Color.FromArgb(28, 30, 36);  // Màu chữ chính
        static readonly Color CMuted = Color.FromArgb(110, 116, 130);  // Màu chữ phụ (mờ)
        static readonly Color CBlue = Color.FromArgb(59, 120, 230);  // Xanh dương: nút hành động chính
        static readonly Color CGreen = Color.FromArgb(34, 150, 85);  // Xanh lá: đạt / đang chạy
        static readonly Color CRed = Color.FromArgb(215, 65, 65);  // Đỏ: lỗi / dừng
        static readonly Color COrange = Color.FromArgb(220, 135, 30);  // Cam: cảnh báo / đang xử lý
        static readonly Color CGray = Color.FromArgb(228, 231, 237);  // Xám: nút thường
        static readonly Font FBase = new Font("Segoe UI", 9.75f);  // Font mặc định
        static readonly Font FBold = new Font("Segoe UI Semibold", 9.75f);  // Font đậm
        static readonly Font FTitle = new Font("Segoe UI Semibold", 12f);  // Font tiêu đề thẻ
        static readonly Font FBig = new Font("Segoe UI Semibold", 17f);  // Font chữ lớn (tên bản Android)
        static readonly Font FMono = new Font("Consolas", 10.5f);  // Font đơn cách cho ô soạn thảo

        // ---------- trạng thái và dịch vụ chính ----------
        readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;  // Thư mục chứa file exe
        readonly Settings S;  // Cấu hình đọc/ghi settings.ini
        readonly HostInfo host;  // Thông tin phần cứng máy thật (CPU, RAM...)
        readonly EmulatorController emu;  // Bộ điều khiển giả lập (bật/tắt, máy ảo AVD)
        readonly TouchService touch = new TouchService();  // Dịch vụ gửi thao tác chạm vào Android
        readonly KeyMapper mapper;  // Bộ map phím bàn phím -> thao tác chạm
        volatile bool androidReady;  // Android đã boot xong (volatile vì đọc/ghi từ nhiều luồng)
        // Thao tác giao diện đang làm, volatile vì luồng nền cũng đổi giá trị
        volatile int uiOp; // 0 = rảnh, 1 = đang bật, 2 = đang dừng
        Button bRun, bCold, bStop;  // Nút Chạy, Cold boot, Dừng ở trang Tổng quan
        // Kết quả kiểm tra ảo hoá phần cứng (emulator -accel-check)
        string accel; // null = chưa kiểm tra

        // ---------- control khung chung ----------
        // Vùng chứa các trang nội dung, lề 18/16/18/10
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = CBg, Padding = new Padding(18, 16, 18, 10) };
        readonly Dictionary<string, Control> pages = new Dictionary<string, Control>();  // Các trang theo khoá ("home", "images"...)
        readonly List<Button> nav = new List<Button>();  // Các nút menu ở thanh bên
        // Ô log chỉ đọc, nền tối, nằm ở đáy cửa sổ
        readonly TextBox logBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 130, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(18, 19, 24), ForeColor = Color.FromArgb(200, 205, 215), BorderStyle = BorderStyle.None };
        // Nhãn trạng thái giả lập ở cuối thanh bên
        readonly Label sideStatus = new Label { AutoSize = false, Width = 200, Height = 46, ForeColor = Color.Silver, Padding = new Padding(18, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        readonly Timer timer = new Timer { Interval = 1500 };  // Bộ đếm giờ làm mới trạng thái mỗi 1,5 giây

        // ---------- thanh bên: trạng thái map phím, số liệu mạng/FPS, thao tác nhanh ----------
        // Nhãn trạng thái map phím (xanh = bật, xám = tắt)
        readonly Label mapLbl = new Label { AutoSize = false, Width = 200, Height = 24, Padding = new Padding(18, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI Semibold", 9.5f) };
        // Nhãn số liệu: tốc độ mạng, ping, FPS (3 dòng)
        readonly Label statsLbl = new Label { AutoSize = false, Width = 200, Height = 66, Padding = new Padding(18, 2, 0, 0), ForeColor = Color.FromArgb(200, 204, 215), Font = new Font("Consolas", 9f) };
        readonly StatsPoller stats = new StatsPoller();  // Bộ đọc số liệu qua adb
        volatile bool polling;  // Đang đọc số liệu (tránh chạy chồng)
        DateTime lastPoll = DateTime.MinValue;  // Lần đọc số liệu gần nhất
        Button bNet, bSave, bRestart, bLoad, bAdb;  // Nút thao tác nhanh ở thanh bên
        bool syncingMap;  // Đang đồng bộ ô tick map phím từ code (không hỏi bật ADB)
        // App được dùng ADB ngay lúc này: người dùng cho phép và ADB trong Android chưa bị tắt
        bool AdbReady { get { return emu.AdbOn && !emu.AdbKilled; } }
        volatile bool busy;  // Đang làm một thao tác nhanh (lưu / làm mới mạng / khởi động lại)

        // tổng quan
        readonly Label lCurImg = new Label { AutoSize = true, Font = FBig, ForeColor = CText };  // Tên bản Android đang chọn (chữ lớn)
        readonly Label lCurSub = new Label { AutoSize = true, ForeColor = CMuted, Margin = new Padding(3, 0, 3, 8) };  // Dòng mô tả cấu hình máy ảo
        readonly Label lState = new Label { AutoSize = true, Font = FBold, ForeColor = Color.White, Padding = new Padding(10, 4, 10, 4), Margin = new Padding(3, 0, 3, 10) };  // Nhãn trạng thái có màu nền (Đang chạy / Đã tắt...)
        readonly TableLayoutPanel checks = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill };  // Bảng 4 cột: biểu tượng, tên mục, chi tiết, nút sửa
        readonly Dictionary<string, Label[]> checkRows = new Dictionary<string, Label[]>();  // Mỗi mục kiểm tra: [nhãn biểu tượng, nhãn chi tiết]
        readonly Dictionary<string, Button> checkBtns = new Dictionary<string, Button>();  // Nút sửa lỗi của từng mục kiểm tra

        // cấu hình
        NumericUpDown nCores, nRam, nW, nH, nDpi;  // Ô số: nhân CPU, RAM, rộng, cao, DPI
        ComboBox cGpu, cDns;  // Ô chọn chế độ GPU, DNS
        CheckBox kQuick, kNoAudio, kNoVulkan, kRestart, kPrio, kBootAnim, kStats;  // Các ô tick tuỳ chọn
        const string DnsAuto = "Theo máy";  // Chữ hiển thị khi dùng DNS của Windows
        static string DnsLabel(string v) { return string.IsNullOrEmpty(v) ? DnsAuto : v; }  // Giá trị lưu -> chữ hiển thị
        TextBox tExtra;  // Tham số dòng lệnh thêm cho emulator

        // bản Android
        static readonly string[] ImageTags = { "google_apis_playstore", "google_apis" };  // Loại image hỗ trợ: có Play Store / Google APIs
        // Bảng danh sách bản Android (chọn cả dòng, chỉ chọn 1 dòng)
        readonly ListView imgList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Font = new Font("Segoe UI", 10f), BorderStyle = BorderStyle.None };
        List<SdkInstaller.ImageInfo> images = new List<SdkInstaller.ImageInfo>();  // Danh sách image lấy từ SDK

        // map phím / macro
        readonly TextBox keyEdit = Editor();  // Ô soạn keymap
        readonly TextBox macroEdit = Editor();  // Ô soạn macro
        // Danh sách macro ở bên trái trang Auto click
        readonly ListBox macroList = new ListBox { Dock = DockStyle.Left, Width = 180, Font = new Font("Segoe UI", 10.5f), BorderStyle = BorderStyle.None, IntegralHeight = false };
        // Ô tick bật/tắt map phím, mặc định bật
        readonly CheckBox kMap = new CheckBox { Text = "Bật map phím", Checked = true, AutoSize = true, Font = FBold, Margin = new Padding(0, 9, 14, 0) };
        // Nhãn trạng thái kết nối input (cam = chưa kết nối)
        readonly Label inputLbl = new Label { AutoSize = true, Margin = new Padding(12, 10, 0, 0), ForeColor = COrange, Text = "Input: chưa kết nối" };

        string KeymapPath { get { return Path.Combine(baseDir, "keymaps", "default.txt"); } }  // Đường dẫn file keymap mặc định
        string MacroDir { get { return Path.Combine(baseDir, "macros"); } }  // Thư mục chứa các file macro

        // Hàm khởi tạo: tạo các dịch vụ, dựng giao diện, gắn sự kiện
        public MainForm()
        {
            S = new Settings(Path.Combine(baseDir, "settings.ini"));  // Nạp cấu hình từ settings.ini
            host = HostInfo.Detect();  // Dò phần cứng máy thật
            if (!S.Has("Cores")) { host.ApplyRecommended(S); S.Save(); }  // Lần đầu chạy (chưa có cấu hình): áp cấu hình đề xuất rồi lưu
            emu = new EmulatorController(S, baseDir);  // Tạo bộ điều khiển giả lập
            mapper = new KeyMapper(touch);  // Tạo bộ map phím dùng dịch vụ chạm
            mapper.ToggleMacro = ToggleMacro;  // Phím tắt macro trong keymap sẽ gọi ToggleMacro

            Text = "Dsoft Android Emulator";  // Tiêu đề cửa sổ
            if (Program.AppIcon != null) Icon = Program.AppIcon;  // Dùng icon ứng dụng nếu có
            Size = new Size(1120, 780);  // Kích thước mặc định
            MinimumSize = new Size(900, 600);  // Kích thước tối thiểu
            Font = FBase;  // Font chung của form
            BackColor = CBg;  // Màu nền form
            AllowDrop = true;  // Cho phép kéo thả file vào (để cài APK)

            // Tạo 5 trang và nút menu tương ứng
            AddPage("home", "Tổng quan", BuildHome());  // Trang Tổng quan
            AddPage("images", "Bản Android", BuildImages());  // Trang Bản Android
            AddPage("config", "Cấu hình", BuildConfig());  // Trang Cấu hình
            AddPage("keys", "Map phím", BuildKeys());  // Trang Map phím
            AddPage("macro", "Auto click", BuildMacro());  // Trang Auto click

            Controls.Add(content);  // Vùng nội dung (Fill) - thêm trước để dock đúng thứ tự
            Controls.Add(logBox);  // Ô log ở đáy
            Controls.Add(BuildSidebar());  // Thanh bên trái (thêm sau cùng nên được dock trước)
            ShowPage("home");  // Mở trang Tổng quan

            // Mỗi dòng log mới: đẩy vào ô log trên luồng UI
            Log.Line += s => UI(() =>
            {
                if (logBox.TextLength > 60000) logBox.Text = logBox.Text.Substring(30000);  // Log quá 60000 ký tự thì cắt bỏ nửa đầu
                logBox.AppendText(s + "\r\n");  // Thêm dòng log mới kèm xuống dòng
            });
            MacroRunner.Changed += () => UI(RefreshMacroList);  // Danh sách macro đang chạy thay đổi -> vẽ lại danh sách
            // Android boot xong -> đánh dấu sẵn sàng; chỉ kết nối input khi được dùng ADB
            emu.Booted += () => { androidReady = true; if (AdbReady) Task.Run(() => ConnectInput()); UI(RefreshAll); };
            emu.Stopped += () => { androidReady = false; UI(RefreshAll); };  // Giả lập tắt -> xoá cờ sẵn sàng, làm mới giao diện
            timer.Tick += (s, e) => RefreshRuntime();  // Mỗi nhịp timer cập nhật trạng thái chạy

            DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };  // Kéo file vào: chấp nhận nếu dữ liệu là file
            // Thả file: cài từng file có đuôi .apk
            DragDrop += (s, e) =>
            {
                var apks = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();  // Chỉ lấy file .apk
                if (apks.Count > 0 && RequireAdb("Cài APK")) foreach (var f in apks) InstallApk(f);  // Cài khi được dùng ADB
            };

            Load += (s, e) => OnLoaded();  // Form hiện xong thì khởi động phần còn lại
            FormClosing += OnClosing;  // Xử lý khi đóng form
        }

        // =============== khung chung ===============

        // Dựng thanh bên: logo, nút menu, trạng thái
        Control BuildSidebar()
        {
            var side = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 200, BackColor = CSide, FlowDirection = FlowDirection.TopDown, WrapContents = false };  // Panel xếp dọc, nền tối, không xuống cột
            // Chữ logo "Dsoft"
            side.Controls.Add(new Label { Text = "Dsoft", Font = new Font("Segoe UI Semibold", 20f), ForeColor = Color.White, AutoSize = false, Width = 200, Height = 50, Padding = new Padding(16, 10, 0, 0) });
            // Dòng phụ "Android Emulator"
            side.Controls.Add(new Label { Text = "Android Emulator", ForeColor = Color.Gray, AutoSize = false, Width = 200, Height = 34, Padding = new Padding(19, 0, 0, 0) });
            foreach (var b in nav) side.Controls.Add(b);  // Thêm các nút menu đã tạo trong AddPage
            side.Controls.Add(new Panel { Height = 20, Width = 10 });  // Khoảng trống
            side.Controls.Add(sideStatus);  // Nhãn trạng thái giả lập
            side.Controls.Add(mapLbl);  // Nhãn trạng thái map phím
            side.Controls.Add(statsLbl);  // Nhãn tốc độ mạng / ping / FPS
            // Nút bật/tắt ADB (chữ và màu đổi theo trạng thái trong RefreshSide)
            bAdb = SideBtn(side, "ADB", (s, e) => ToggleAdb(), "ADB tắt: game không thấy USB debugging, nhưng map phím / auto click / số liệu / cài APK không dùng được.");
            // Tiêu đề nhóm thao tác nhanh
            side.Controls.Add(new Label { Text = "THAO TÁC NHANH", ForeColor = Color.Gray, AutoSize = false, Width = 200, Height = 26, Padding = new Padding(18, 8, 0, 0), Font = new Font("Segoe UI", 8f) });
            bNet = SideBtn(side, "⇄  Làm mới mạng", (s, e) => RefreshNet(), "Tắt/bật lại Wi-Fi ảo trong Android (~15s). Dùng khi mạng chậm/mất.");
            bSave = SideBtn(side, "⬇  Lưu trạng thái", (s, e) => SaveState(), "Lưu trạng thái hiện tại (Android đứng hình ~10-20s).");
            bLoad = SideBtn(side, "↺  Chạy từ bản lưu", (s, e) => RestartEmu(true), "Tắt rồi bật lại đúng trạng thái đã lưu.");
            bRestart = SideBtn(side, "⟳  Khởi động lại nhanh", (s, e) => RestartEmu(false), "Tắt rồi bật lại bằng Quick Boot, giữ nguyên trạng thái (~20-30s). Dùng khi chạy lâu bị chậm.");
            return side;  // Trả về thanh bên
        }

        readonly ToolTip tips = new ToolTip();  // Chú thích khi rê chuột lên nút thanh bên

        // Tạo nút nhỏ kiểu tối cho thanh bên
        Button SideBtn(Control parent, string text, EventHandler h, string tip)
        {
            var b = new Button
            {
                Text = text, Width = 176, Height = 34, Margin = new Padding(12, 3, 12, 3), FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 0, 0, 0), ForeColor = Color.FromArgb(220, 224, 232), BackColor = CSideHover, Font = new Font("Segoe UI", 9.5f), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;  // Bỏ viền
            b.FlatAppearance.MouseOverBackColor = CSideActive;  // Sáng hơn khi rê chuột
            b.Click += h;  // Gắn xử lý khi bấm
            tips.SetToolTip(b, tip);  // Chú thích
            parent.Controls.Add(b);  // Thêm vào thanh bên
            return b;  // Trả về nút
        }

        // Đăng ký một trang và tạo nút menu cho nó
        void AddPage(string key, string title, Control page)
        {
            page.Dock = DockStyle.Fill;  // Trang lấp đầy vùng nội dung
            page.Visible = false;  // Ẩn mặc định
            pages[key] = page;  // Lưu trang theo khoá
            content.Controls.Add(page);  // Thêm trang vào vùng nội dung
            // Nút menu phẳng, chữ căn trái, Tag = khoá trang
            var b = new Button
            {
                Text = title, Tag = key, Padding = new Padding(16, 0, 0, 0), Width = 200, Height = 44, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(200, 204, 215), BackColor = CSide, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0), Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;  // Bỏ viền nút
            b.FlatAppearance.MouseOverBackColor = CSideHover;  // Màu nền khi rê chuột
            b.Click += (s, e) => ShowPage(key);  // Bấm nút -> mở trang tương ứng
            nav.Add(b);  // Lưu vào danh sách nút menu
        }

        // Hiện trang theo khoá và tô sáng nút menu tương ứng
        void ShowPage(string key)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == key;  // Chỉ trang được chọn hiển thị, còn lại ẩn
            // Duyệt các nút menu để đổi kiểu
            foreach (var b in nav)
            {
                bool on = (string)b.Tag == key;  // Nút này có phải trang đang mở không
                b.BackColor = on ? CSideActive : CSide;  // Đổi màu nền
                b.ForeColor = on ? Color.White : Color.FromArgb(200, 204, 215);  // Đổi màu chữ
                b.Font = on ? new Font("Segoe UI Semibold", 10.5f) : new Font("Segoe UI", 10.5f);  // Chữ đậm nếu đang chọn
            }
            if (key == "images" && images.Count == 0) LoadImages();  // Lần đầu mở trang Bản Android thì tải danh sách image
        }

        /// <summary>Thẻ trắng có tiêu đề.</summary>
        static Control Card(string title, Control body)
        {
            // Bảng 1 cột nền trắng, tự co giãn theo nội dung
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = CCard, Padding = new Padding(18, 14, 18, 16), Margin = new Padding(0, 0, 0, 14), Dock = DockStyle.Fill };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Cột duy nhất rộng 100%
            if (title != null) t.Controls.Add(new Label { Text = title, Font = FTitle, ForeColor = CText, AutoSize = true, Margin = new Padding(0, 0, 0, 10) });  // Có tiêu đề thì thêm nhãn tiêu đề
            body.Margin = new Padding(0);  // Bỏ lề của nội dung
            body.Dock = DockStyle.Fill;  // Nội dung lấp đầy thẻ
            t.Controls.Add(body);  // Thêm nội dung vào thẻ
            return t;  // Trả về thẻ
        }

        /// <summary>Trang cuộn gồm các thẻ xếp dọc.</summary>
        static Control Stack(params Control[] cards)
        {
            var page = new Panel { AutoScroll = true };  // Panel cho phép cuộn
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };  // Bảng 1 cột xếp thẻ từ trên xuống
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Cột rộng 100%
            foreach (var c in cards) t.Controls.Add(c);  // Thêm từng thẻ
            page.Controls.Add(t);  // Đặt bảng vào trang
            return page;  // Trả về trang
        }

        // Tạo hàng ngang tự co, không xuống dòng (dùng cho hàng nút)
        static FlowLayoutPanel Row() { return new FlowLayoutPanel { AutoSize = true, WrapContents = false }; }

        // Làm tối màu c theo tỉ lệ k (0..1)
        static Color Darker(Color c, double k) { return Color.FromArgb((int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k))); }

        // Tạo nút phẳng; back = màu nền (null = nền xám, chữ đen)
        static Button Btn(Control parent, string text, EventHandler h, Color? back = null)
        {
            Color bg = back ?? CGray;  // Không truyền màu thì dùng xám
            // Nút tự co theo chữ, cao tối thiểu 36px; có màu nền thì chữ trắng
            var b = new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(0, 36), FlatStyle = FlatStyle.Flat,
                Padding = new Padding(12, 2, 12, 2), Margin = new Padding(0, 4, 8, 4), Cursor = Cursors.Hand, Font = FBold,
                BackColor = bg, ForeColor = back.HasValue ? Color.White : CText
            };
            b.FlatAppearance.BorderSize = 0;  // Bỏ viền
            b.FlatAppearance.MouseOverBackColor = Darker(bg, 0.1);  // Tối hơn 10% khi rê chuột
            b.FlatAppearance.MouseDownBackColor = Darker(bg, 0.2);  // Tối hơn 20% khi nhấn
            b.Click += h;  // Gắn xử lý khi bấm
            if (parent != null) parent.Controls.Add(b);  // Có parent thì thêm nút vào luôn
            return b;  // Trả về nút
        }

        /// <summary>TextBox của Windows chỉ xuống dòng với \r\n.</summary>
        static string Crlf(string s) { return s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        // Tạo ô soạn thảo nhiều dòng, font đơn cách (keymap/macro)
        static TextBox Editor()
        {
            // Không tự xuống dòng, có thanh cuộn 2 chiều, cho gõ Tab
            return new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = FMono, AcceptsTab = true, BorderStyle = BorderStyle.None };
        }

        /// <summary>Trang dạng: thanh công cụ trên + vùng chính lấp đầy.</summary>
        static Control ToolPage(Control toolbar, Control main, Control bottom)
        {
            var page = new Panel();  // Trang gốc
            var mainCard = new Panel { Dock = DockStyle.Fill, BackColor = CCard, Padding = new Padding(10) };  // Thẻ trắng chứa vùng chính
            mainCard.Controls.Add(main);  // Đặt vùng chính vào thẻ
            page.Controls.Add(mainCard);  // Thêm thẻ vào trang (Fill)
            if (bottom != null) { bottom.Dock = DockStyle.Bottom; page.Controls.Add(bottom); }  // Có phần dưới thì dock xuống đáy
            var top = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = CCard, Padding = new Padding(12, 8, 12, 4) };  // Khung chứa thanh công cụ ở trên
            toolbar.Dock = DockStyle.Fill;  // Thanh công cụ lấp đầy khung
            top.Controls.Add(toolbar);  // Đặt thanh công cụ vào khung
            page.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });  // Khoảng cách 12px giữa thanh công cụ và vùng chính
            page.Controls.Add(top);  // Thêm khung trên (thêm sau nên nằm trên cùng)
            return page;  // Trả về trang
        }

        // Chạy hành động trên luồng UI (an toàn khi gọi từ luồng nền)
        void UI(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;  // Form đã đóng hoặc chưa tạo handle thì bỏ qua
            if (InvokeRequired) BeginInvoke(a); else a();  // Khác luồng thì chuyển sang luồng UI, cùng luồng thì gọi luôn
        }

        // Chạy tác vụ ở luồng nền, ghi log bắt đầu/kết thúc rồi làm mới giao diện
        void Bg(string what, Action a)
        {
            Log.I(what + "...");  // Ghi log bắt đầu
            Task.Run(() =>  // Chạy ở luồng nền
            {
                try { a(); Log.I("✔ " + what + ": xong"); }  // Chạy tác vụ rồi báo xong
                catch (Exception ex) { Log.I("✖ " + what + ": " + ex.Message); }  // Lỗi thì ghi log thông báo lỗi
                UI(RefreshAll);  // Làm mới giao diện
            });
        }

        // Làm mới toàn bộ: mục kiểm tra, trạng thái chạy, bảng image (nếu đã tải)
        void RefreshAll() { RefreshChecks(); RefreshRuntime(); if (images.Count > 0) FillImages(); }

        // =============== Tổng quan ===============

        // Dựng trang Tổng quan
        Control BuildHome()
        {
            var head = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };  // Khối đầu trang xếp dọc
            head.Controls.Add(lCurImg);  // Tên bản Android
            head.Controls.Add(lCurSub);  // Dòng mô tả cấu hình
            head.Controls.Add(lState);  // Nhãn trạng thái
            var actions = Row();  // Hàng nút hành động
            bRun = Btn(actions, "▶  Chạy", (s, e) => StartEmu(false), CGreen);  // Nút Chạy (xanh lá)
            bCold = Btn(actions, "⟳  Cold boot", (s, e) => StartEmu(true));  // Nút Cold boot (khởi động nguội, bỏ snapshot)
            bStop = Btn(actions, "■  Dừng", (s, e) => StopEmu(), CRed);  // Nút Dừng (đỏ)
            // Nút Cài APK: chọn một hoặc nhiều file .apk rồi cài
            Btn(actions, "Cài APK...", (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "APK|*.apk", Multiselect = true })  // Hộp thoại chọn file APK, cho chọn nhiều
                    if (d.ShowDialog(this) == DialogResult.OK && RequireAdb("Cài APK")) foreach (var f in d.FileNames) InstallApk(f);  // Bấm OK -> cài từng file (cần ADB)
            });
            Btn(actions, "Đổi bản Android", (s, e) => ShowPage("images"));  // Nút chuyển sang trang Bản Android
            head.Controls.Add(actions);  // Thêm hàng nút vào khối đầu
            // Dòng mẹo kéo thả APK
            head.Controls.Add(new Label { AutoSize = true, ForeColor = CMuted, Margin = new Padding(3, 6, 3, 0), Text = "Mẹo: kéo thả file .apk vào cửa sổ này để cài." });

            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));  // Cột 1: biểu tượng (34px)
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));  // Cột 2: tên mục (235px)
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // Cột 3: chi tiết (phần còn lại)
            checks.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));  // Cột 4: nút sửa (tự co)
            AddCheck("accel", "Ảo hoá phần cứng (WHPX)");  // Mục ảo hoá phần cứng
            AddCheck("tools", "Emulator + ADB");  // Mục công cụ emulator + adb
            AddCheck("scrcpy", "scrcpy-server (map phím nhanh)");  // Mục scrcpy-server
            AddCheck("image", "Bản Android đã chọn");  // Mục bản Android đang chọn
            AddCheck("disk", "Ổ đĩa trống");  // Mục dung lượng ổ đĩa
            AddCheck("run", "Giả lập");  // Mục trạng thái giả lập
            AddCheck("input", "Điều khiển (map phím / auto click)");  // Mục kết nối input
            var reCheck = Row();  // Hàng chứa nút kiểm tra lại
            Btn(reCheck, "Kiểm tra lại", (s, e) => { accel = null; CheckAccel(); RefreshChecks(); });  // Xoá kết quả cũ, kiểm tra lại ảo hoá và các mục

            var checkBody = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };  // Bảng chứa danh sách kiểm tra + nút
            checkBody.Controls.Add(checks);  // Thêm bảng kiểm tra
            checkBody.Controls.Add(reCheck);  // Thêm hàng nút

            var hostLbl = new Label { AutoSize = true, ForeColor = CText, Text = host.ToString() };  // Nhãn thông tin máy thật
            whpxCard = Card("Cách bật ảo hoá (Windows Hypervisor Platform)", BuildWhpxHelp());  // Thẻ hướng dẫn bật WHPX
            return Stack(Card(null, head), Card("Kiểm tra trước khi chạy", checkBody), whpxCard, Card("Máy tính này", hostLbl));  // Ghép các thẻ thành trang cuộn
        }

        Control whpxCard;  // Thẻ hướng dẫn WHPX (ẩn khi đã bật)
        const string WhpxCmd = "dism /online /enable-feature /featurename:HypervisorPlatform /all /norestart";  // Lệnh DISM bật Windows Hypervisor Platform

        // Nội dung hướng dẫn bật WHPX: 3 cách
        Control BuildWhpxHelp()
        {
            var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };  // Khối xếp dọc
            // Hàm tạo nhãn đoạn văn tự xuống dòng (rộng tối đa 780px)
            Func<string, Label> text = s => new Label { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = CText, Margin = new Padding(0, 6, 0, 2), Text = s };

            body.Controls.Add(text("Cách 1 - tự động: bấm nút dưới, chọn Yes khi Windows hỏi quyền Admin, chờ chạy xong rồi KHỞI ĐỘNG LẠI MÁY."));  // Cách 1
            var r1 = Row();  // Hàng nút
            Btn(r1, "Bật WHPX tự động (Admin)", (s, e) => EnableWhpx(), CBlue);  // Nút chạy lệnh với quyền Admin
            body.Controls.Add(r1);  // Thêm hàng

            body.Controls.Add(text("Cách 2 - chạy lệnh: mở CMD bằng 'Run as administrator', dán lệnh sau, Enter, rồi khởi động lại máy:"));  // Cách 2
            // Ô chứa lệnh, chỉ đọc, để người dùng copy
            var cmdBox = new TextBox { Text = WhpxCmd, ReadOnly = true, Width = 620, Font = FMono, BackColor = Color.FromArgb(246, 247, 250) };
            cmdBox.Click += (s, e) => cmdBox.SelectAll();  // Bấm vào thì chọn hết lệnh
            var r2 = Row();  // Hàng ô lệnh + nút copy
            cmdBox.Margin = new Padding(0, 8, 8, 0);  // Căn lề ô lệnh
            r2.Controls.Add(cmdBox);  // Thêm ô lệnh vào hàng
            // Nút copy lệnh vào clipboard (lỗi clipboard thì bỏ qua)
            Btn(r2, "Copy lệnh", (s, e) => { try { Clipboard.SetText(WhpxCmd); Log.I("Đã copy lệnh bật WHPX."); } catch { } });
            body.Controls.Add(r2);  // Thêm hàng

            // Cách 3
            body.Controls.Add(text("Cách 3 - bằng giao diện: bấm nút dưới (hoặc Win+R gõ optionalfeatures), tick ô \"Windows Hypervisor Platform\", OK, rồi khởi động lại máy."));
            var r3 = Row();  // Hàng nút
            // Nút mở hộp thoại Windows Features, lỗi thì ghi log
            Btn(r3, "Mở Windows Features", (s, e) => { try { Process.Start("optionalfeatures.exe"); } catch (Exception ex) { Log.I("Không mở được: " + ex.Message); } });
            body.Controls.Add(r3);  // Thêm hàng

            // Ghi chú bật VT-x/SVM trong BIOS
            body.Controls.Add(text("Bật rồi vẫn báo lỗi: vào BIOS bật Intel VT-x (Virtualization Technology) hoặc AMD SVM. Sau khi khởi động lại, bấm 'Kiểm tra lại' ở trên."));
            return body;  // Trả về nội dung
        }

        // Thêm một dòng kiểm tra vào bảng (biểu tượng, tên, chi tiết, nút sửa)
        void AddCheck(string key, string name)
        {
            // Nhãn biểu tượng trạng thái ✔ ✖ ! …
            var icon = new Label { AutoSize = false, Size = new Size(26, 26), Font = new Font("Segoe UI Symbol", 12f), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(0, 6, 4, 6) };
            var title = new Label { Text = name, AutoSize = true, Font = FBold, ForeColor = CText, Margin = new Padding(0, 10, 8, 0) };  // Tên mục
            var detail = new Label { AutoSize = true, ForeColor = CMuted, Margin = new Padding(0, 10, 8, 0), MaximumSize = new Size(390, 0) };  // Chi tiết, tự xuống dòng khi rộng quá 390px
            var fix = Btn(null, "", (s, e) => { var a = ((Button)s).Tag as Action; if (a != null) a(); });  // Nút sửa: bấm thì gọi Action lưu trong Tag
            fix.Visible = false;  // Ẩn tới khi cần
            fix.Margin = new Padding(0, 3, 0, 3);  // Lề nút
            // đặt ô cố định: control ẩn trong TableLayoutPanel sẽ làm lệch cột nếu add tuần tự
            int row = checkRows.Count;  // Chỉ số dòng mới = số mục đã có
            checks.Controls.Add(icon, 0, row);  // Cột 0: biểu tượng
            checks.Controls.Add(title, 1, row);  // Cột 1: tên
            checks.Controls.Add(detail, 2, row);  // Cột 2: chi tiết
            checks.Controls.Add(fix, 3, row);  // Cột 3: nút sửa
            checkRows[key] = new[] { icon, detail };  // Lưu biểu tượng + chi tiết để cập nhật sau
            checkBtns[key] = fix;  // Lưu nút sửa
        }

        // Trạng thái mục kiểm tra: Đạt, Cảnh báo, Lỗi, Đang chờ
        enum St { Ok, Warn, Bad, Wait }

        // Cập nhật một mục kiểm tra; fixText null = ẩn nút sửa
        void SetCheck(string key, St st, string detail, string fixText, Action fix)
        {
            var r = checkRows[key];  // Lấy nhãn của mục
            r[0].Text = st == St.Ok ? "✔" : st == St.Bad ? "✖" : st == St.Warn ? "!" : "…";  // Ký hiệu theo trạng thái
            r[0].ForeColor = st == St.Ok ? CGreen : st == St.Bad ? CRed : st == St.Warn ? COrange : CMuted;  // Màu theo trạng thái
            r[1].Text = detail;  // Nội dung chi tiết
            var b = checkBtns[key];  // Nút sửa của mục
            b.Visible = fixText != null;  // Chỉ hiện nút khi có chữ
            if (fixText == null) return;  // Không có nút thì dừng ở đây
            b.Text = fixText;  // Chữ trên nút
            b.Tag = fix;  // Hành động khi bấm
            b.BackColor = st == St.Bad ? CBlue : CGray;  // Lỗi thì nút xanh nổi bật, còn lại xám
            b.ForeColor = st == St.Bad ? Color.White : CText;  // Màu chữ tương ứng
            b.FlatAppearance.MouseOverBackColor = Darker(b.BackColor, 0.1);  // Màu khi rê chuột
        }

        // Tên hiển thị bản đang chọn, ví dụ "Android 14 · Play Store"
        string CurImageName { get { return SdkInstaller.ImageInfo.AndroidName(emu.Api) + " · " + (emu.Tag.Contains("playstore") ? "Play Store" : "Google APIs"); } }
        // Bản đang chọn đã cài chưa (có file system.img)
        bool CurImageInstalled { get { return File.Exists(Path.Combine(new SdkInstaller(emu.SdkDir).ImageDir(emu.Api, emu.Tag, emu.Abi), "system.img")); } }

        // Kiểm tra ảo hoá phần cứng ở luồng nền
        void CheckAccel()
        {
            if (!File.Exists(emu.EmulatorExe)) return;  // Chưa có emulator thì chưa kiểm tra được
            Task.Run(() =>  // Chạy nền vì lệnh mất vài giây
            {
                string r = emu.AccelCheck();  // Gọi emulator -accel-check
                accel = r;  // Lưu kết quả
                UI(RefreshChecks);  // Cập nhật bảng kiểm tra trên luồng UI
            });
        }

        // Cập nhật các mục kiểm tra trước khi chạy và dòng thông tin máy ảo
        void RefreshChecks()
        {
            var inst = new SdkInstaller(emu.SdkDir);  // Trình cài SDK theo thư mục lưu hiện tại
            bool tools = inst.ToolsInstalled;  // Đã có emulator + adb chưa

            bool accelOk = accel != null && accel.Contains("usable");  // Ảo hoá dùng được khi kết quả có chữ "usable"
            // Chưa có emulator: chưa kiểm tra được, vẫn cho bấm bật WHPX
            if (!tools) SetCheck("accel", St.Wait, "Chưa kiểm tra được (cần tải Emulator). Nếu chưa bật thì làm theo hướng dẫn bên dưới.", "Bật WHPX (Admin)", EnableWhpx);
            else if (accel == null) SetCheck("accel", St.Wait, "Đang kiểm tra...", null, null);  // Đang chờ kết quả kiểm tra
            // Đạt: hiện dòng kết quả có chữ "usable"
            else if (accelOk) SetCheck("accel", St.Ok, accel.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Contains("usable")), null, null);
            // Không đạt: báo lỗi và hiện nút bật WHPX
            else SetCheck("accel", St.Bad, "Chưa bật - xem hướng dẫn bên dưới. Bật xong phải khởi động lại máy.", "Bật WHPX (Admin)", EnableWhpx);
            // Chỉ hiện thẻ hướng dẫn khi ảo hoá chưa dùng được
            whpxCard.Visible = !accelOk; // đã bật thì ẩn hướng dẫn

            // Mục Emulator + ADB: thiếu thì có nút Tải về
            SetCheck("tools", tools ? St.Ok : St.Bad, tools ? "Đã cài (bin\\sdk)" : "Chưa cài (~450 MB)", tools ? null : "Tải về", InstallTools);

            bool scr = File.Exists(SdkInstaller.ScrcpyFile(emu.SdkDir));  // File scrcpy-server đã có chưa
            // Mục scrcpy: thiếu thì cảnh báo (vẫn chạy được nhưng chậm)
            SetCheck("scrcpy", scr ? St.Ok : St.Warn, scr ? "Phiên bản " + SdkInstaller.ScrcpyVersion : "Thiếu: map phím sẽ chậm (dùng adb input)", scr ? null : "Tải về", InstallTools);

            bool img = CurImageInstalled;  // Bản đang chọn đã cài chưa
            string wipe = emu.WipePendingFor(emu.Api, emu.Tag) ? " · đã xoá trắng, lần chạy tới như máy mới" : "";  // Ghi chú thêm nếu bản này vừa bị xoá trắng
            // Mục bản Android: đã cài -> nút Đổi bản, chưa cài -> nút Tải bản này
            SetCheck("image", img ? St.Ok : St.Bad, CurImageName + (img ? " · đã cài" + wipe : " · chưa tải"), img ? "Đổi bản" : "Tải bản này", img ? (Action)(() => ShowPage("images")) : InstallCurrentImage);

            try  // Đọc thông tin ổ đĩa có thể lỗi nên bọc try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(emu.SdkDir)));  // Ổ chứa thư mục SDK
                double gb = drive.AvailableFreeSpace / 1073741824.0;  // Dung lượng trống (GB)
                // Mục ổ đĩa: ≥10 GB đạt, ≥4 GB cảnh báo, ít hơn là lỗi
                SetCheck("disk", gb >= 10 ? St.Ok : gb >= 4 ? St.Warn : St.Bad, "Nơi lưu ở ổ " + drive.Name + " còn " + gb.ToString("0.0") + " GB" + (gb < 10 ? " (nên ≥ 10 GB)" : ""),
                         "Đổi nơi lưu", () => ShowPage("config"));  // Nút mở trang Cấu hình để đổi nơi lưu
            }
            catch { }  // Lỗi đọc ổ thì bỏ qua

            lCurImg.Text = CurImageName;  // Hiện tên bản đang chọn
            // Dòng mô tả: tên máy ảo, nhân, RAM, độ phân giải, GPU
            lCurSub.Text = "Máy ảo " + emu.AvdName + "  ·  " + S.GetInt("Cores", 4) + " nhân  ·  " + S.GetInt("RamMB", 4096) + " MB RAM  ·  " +
                           S.GetInt("Width", 1280) + "x" + S.GetInt("Height", 720) + "  ·  GPU " + S.Get("Gpu", "host");  // Phần sau của chuỗi mô tả
        }

        // Cập nhật trạng thái chạy: các nút, nhãn trạng thái, mục "run" và "input"
        void RefreshRuntime()
        {
            string st; Color c;  // Chữ và màu trạng thái
            bool on = emu.Running || androidReady;  // Coi là đang bật nếu process còn chạy hoặc Android đã sẵn sàng
            // Chỉ hiện Chạy khi đã tắt hẳn, chỉ hiện Dừng khi đang bật/đang chạy
            bRun.Visible = bCold.Visible = uiOp == 0 && !on;  // Hiện Chạy/Cold boot khi rảnh và đã tắt
            bStop.Visible = uiOp != 0 || on;  // Hiện Dừng khi đang thao tác hoặc đang bật
            bStop.Enabled = uiOp != 2;  // Đang dừng thì khoá nút Dừng
            bStop.Text = uiOp == 2 ? "Đang dừng..." : "■  Dừng";  // Đổi chữ nút khi đang dừng
            // Trường hợp đang dừng
            if (uiOp == 2)
            {
                st = "●  Đang dừng...";  // Chữ trạng thái
                c = COrange;  // Màu cam
                SetCheck("run", St.Wait, "Đang tắt giả lập (lưu trạng thái nếu bật Quick boot)", null, null);  // Mục giả lập: đang chờ tắt
            }
            // Đang bật nhưng process emulator chưa chạy
            else if (uiOp == 1 && !emu.Running)
            {
                st = "●  Đang bật...";  // Chữ trạng thái
                c = COrange;  // Màu cam
                SetCheck("run", St.Wait, "Đang chuẩn bị máy ảo", null, null);  // Mục giả lập: đang chuẩn bị
            }
            // Process đã chạy nhưng Android chưa boot xong
            else if (emu.Running && !emu.IsBooted && !androidReady)
            {
                int sec = (int)(DateTime.Now - emu.StartedAt).TotalSeconds;  // Số giây kể từ lúc bật
                st = "●  Đang khởi động... " + sec + "s";  // Chữ trạng thái kèm số giây
                c = COrange;  // Màu cam
                SetCheck("run", St.Wait, "Đang boot " + sec + "s (lần đầu của mỗi bản có thể mất 2-5 phút, màn hình đen là bình thường)", null, null);  // Mục giả lập: đang boot
            }
            // Đã chạy, Android sẵn sàng
            else if (emu.Running || androidReady)
            {
                st = "●  Đang chạy";  // Chữ trạng thái
                c = CGreen;  // Màu xanh lá
                SetCheck("run", St.Ok, "Android đã sẵn sàng", "■ Dừng", StopEmu);  // Mục giả lập: đạt, có nút Dừng
            }
            else  // Còn lại: đã tắt
            {
                st = "●  Đã tắt";  // Chữ trạng thái
                c = Color.FromArgb(140, 145, 155);  // Màu xám
                bool can = CurImageInstalled && File.Exists(emu.EmulatorExe);  // Đủ điều kiện chạy: có image và emulator
                SetCheck("run", can ? St.Wait : St.Bad, can ? "Chưa chạy" : "Cần đủ các mục trên", can ? "▶ Chạy" : null, () => StartEmu(false));  // Đủ thì hiện nút Chạy, thiếu thì báo lỗi
            }
            lState.Text = st;  // Cập nhật nhãn trạng thái ở trang Tổng quan
            lState.BackColor = c;  // Màu nền nhãn
            sideStatus.Text = st;  // Nhãn trạng thái ở thanh bên
            sideStatus.ForeColor = c == Color.FromArgb(140, 145, 155) ? Color.Gray : c;  // Màu xám thì dùng Gray cho dễ nhìn trên nền tối

            var inj = touch.Inj;  // Bộ gửi chạm hiện tại
            // ADB đang tắt: map phím / auto click không dùng được
            if (!AdbReady)
            {
                SetCheck("input", St.Warn, "ADB đang tắt (game không thấy USB debugging). Map phím / auto click cần ADB.", "Bật ADB", ToggleAdb);
                inputLbl.Text = "Input: ADB đang tắt";  // Nhãn trên trang Map phím
                inputLbl.ForeColor = COrange;  // Màu cam
            }
            // Đã kết nối input
            else if (touch.Ready)
            {
                bool fast = inj is ScrcpyTouch;  // Kiểu scrcpy là kiểu nhanh
                SetCheck("input", fast ? St.Ok : St.Warn, inj.Name, "Kết nối lại", () => Task.Run(() => ConnectInput()));  // Mục input: scrcpy đạt, adb cảnh báo; có nút kết nối lại
                inputLbl.Text = "Input: " + inj.Name;  // Hiện tên kiểu input trên trang Map phím
                inputLbl.ForeColor = fast ? CGreen : COrange;  // Xanh nếu nhanh, cam nếu chậm
            }
            else  // Chưa kết nối input
            {
                // Chỉ hiện nút Kết nối khi Android đã boot
                SetCheck("input", St.Wait, "Tự kết nối khi Android boot xong", androidReady || emu.IsBooted ? "Kết nối" : null, () => Task.Run(() => ConnectInput()));
                inputLbl.Text = "Input: chưa kết nối";  // Nhãn báo chưa kết nối
                inputLbl.ForeColor = COrange;  // Màu cam
            }
            RefreshSide(on);  // Cập nhật thanh bên: map phím, số liệu, nút thao tác nhanh
        }

        // Cập nhật phần thêm ở thanh bên; on = giả lập đang bật
        void RefreshSide(bool on)
        {
            // Phím toggle trong keymap đổi mapper.Enabled ở luồng hook -> đồng bộ lại ô tick trên trang Map phím
            if (kMap.Checked != mapper.Enabled) { syncingMap = true; kMap.Checked = mapper.Enabled; syncingMap = false; }
            bool adbOk = AdbReady;  // Được dùng ADB không
            mapLbl.Text = "Map phím: " + (mapper.Enabled ? (adbOk ? "BẬT" : "BẬT · cần ADB") : "TẮT");  // Chữ trạng thái map phím
            mapLbl.ForeColor = !mapper.Enabled ? Color.Gray : adbOk ? Color.FromArgb(80, 200, 120) : Color.FromArgb(240, 170, 80);  // Xanh / cam (thiếu ADB) / xám

            // Nút ADB: tắt = xanh (an toàn với game), bật = cam (game có thể phát hiện)
            bAdb.Text = !emu.AdbOn ? "●  ADB: TẮT  (bấm để bật)" : emu.AdbKilled && (emu.Running || androidReady) ? "●  ADB: BẬT  (chờ cold boot)" : "●  ADB: BẬT  (bấm để tắt)";
            bAdb.ForeColor = emu.AdbOn ? Color.FromArgb(240, 170, 80) : Color.FromArgb(80, 200, 120);
            bAdb.Enabled = uiOp == 0 && !busy;  // Không cho đổi khi đang bật/tắt giả lập

            bool ready = on && androidReady && uiOp == 0;  // Android sẵn sàng và không đang bật/tắt
            bNet.Enabled = bSave.Enabled = ready && !busy;  // Làm mới mạng / lưu: cần Android sẵn sàng
            bRestart.Enabled = emu.Running && uiOp == 0 && !busy;  // Khởi động lại: chỉ cần emulator đang chạy
            bLoad.Enabled = uiOp == 0 && !busy && emu.HasSave;  // Chạy từ bản lưu: cần có bản lưu (tắt hay bật đều được)

            bool show = S.GetBool("ShowStats", true) && ready;  // Có hiện số liệu không
            statsLbl.Visible = show;  // Ẩn khi tắt tuỳ chọn hoặc chưa sẵn sàng
            if (show && !adbOk) { statsLbl.Text = "Mạng / FPS: cần ADB"; statsLbl.ForeColor = Color.Gray; return; }  // ADB tắt: không đọc được số liệu
            if (!show || busy || polling || (DateTime.Now - lastPoll).TotalSeconds < 2) return;  // Mỗi 2 giây đọc 1 lần, không chạy chồng
            polling = true;  // Đánh dấu đang đọc
            lastPoll = DateTime.Now;  // Ghi mốc
            var adb = emu.NewAdb();  // adb nhắm vào emulator
            string host = S.Get("PingHost", "8.8.8.8");  // Địa chỉ ping
            Task.Run(() =>  // Đọc ở luồng nền vì adb + ping mất 1-2 giây
            {
                try { stats.Poll(adb, host); } catch { }  // Lỗi đọc thì bỏ qua lần này
                finally { polling = false; }  // Xong thì cho phép lần sau
                UI(ShowStats);  // Hiện kết quả trên luồng UI
            });
        }

        // Hiện số liệu đã đọc lên nhãn thanh bên
        void ShowStats()
        {
            if (!stats.Valid) { statsLbl.Text = "Mạng: đang đọc..."; statsLbl.ForeColor = Color.Gray; return; }  // Chưa có số liệu
            string ping = stats.PingMs < 0 ? "mất mạng" : stats.PingMs.ToString("0") + " ms";  // Không có phản hồi ping = mất mạng
            statsLbl.Text = "↓ " + StatsPoller.Speed(stats.DownKBs) + "  ↑ " + StatsPoller.Speed(stats.UpKBs) + "\r\n" +  // Dòng 1: tốc độ
                            "Ping " + ping + "\r\n" +  // Dòng 2: ping
                            "FPS  " + (stats.Fps < 0 ? "-" : stats.Fps.ToString("0"));  // Dòng 3: FPS
            // Màu theo ping: <100ms xanh, <250ms cam, còn lại / mất mạng đỏ
            statsLbl.ForeColor = stats.PingMs < 0 ? Color.FromArgb(240, 110, 110) : stats.PingMs < 100 ? Color.FromArgb(200, 204, 215) : stats.PingMs < 250 ? Color.FromArgb(240, 170, 80) : Color.FromArgb(240, 110, 110);
        }

        // Chạy một thao tác nhanh ở nền, khoá các nút tới khi xong
        void QuickOp(string what, Action a)
        {
            if (busy) return;  // Đang làm thao tác khác thì bỏ qua
            busy = true;  // Khoá nút
            RefreshRuntime();  // Cập nhật nút ngay
            Bg(what, () => { try { a(); } finally { busy = false; } });  // Chạy nền, luôn mở khoá khi xong
        }

        // ---------- ADB ----------

        /// <summary>Gọi trước tính năng cần ADB (luồng UI). ADB đang tắt thì hỏi có bật không.
        /// Trả true nếu dùng được ngay; false nếu người dùng từ chối hoặc phải chờ cold boot.</summary>
        bool RequireAdb(string what)
        {
            if (AdbReady) return true;  // Đang dùng được
            bool needCold = emu.AdbKilled && (emu.Running || androidReady);  // ADB đã bị tắt trong Android đang chạy -> phải cold boot
            string msg = what + " cần ADB, mà ADB đang tắt (để game không phát hiện USB debugging).\r\n\r\nBật ADB?" +
                         (needCold ? "\r\n\r\nAndroid sẽ KHỞI ĐỘNG NGUỘI (~1-2 phút): app/game đang mở sẽ bị đóng." : "");
            if (MessageBox.Show(this, msg, "Cần ADB", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;  // Không đồng ý
            EnableAdb();  // Bật
            return AdbReady;  // Không cần cold boot thì dùng được ngay
        }

        // Bấm nút ADB ở thanh bên
        void ToggleAdb()
        {
            if (emu.AdbOn) { DisableAdb(); return; }  // Đang bật -> tắt
            RequireAdb("Map phím, auto click, số liệu mạng/FPS, cài APK");  // Đang tắt -> hỏi rồi bật
        }

        // Cho phép dùng ADB; ADB đã bị tắt trong Android đang chạy thì cold boot để bật lại
        void EnableAdb()
        {
            S.Set("AdbOn", true); S.Save();  // Lưu lựa chọn
            Log.I("Đã cho phép dùng ADB.");  // Ghi log
            if (!emu.AdbKilled)  // ADB trong Android vẫn còn chạy
            {
                if (androidReady && !touch.Ready) Task.Run(() => ConnectInput());  // Kết nối input ngay
                RefreshAll();  // Làm mới giao diện
                return;
            }
            if (!(emu.Running || androidReady)) { Log.I("Lần ▶ Chạy tới sẽ khởi động nguội để bật lại ADB."); RefreshAll(); return; }  // Giả lập đang tắt
            // Đang chạy và ADB đã tắt: tắt rồi cold boot
            busy = true; uiOp = 2; RefreshRuntime();  // Khoá nút, trạng thái "Đang dừng"
            Bg("Khởi động nguội để bật ADB", () =>
            {
                try
                {
                    if (emu.Running) emu.Stop();  // Tắt (Quick Boot lưu trạng thái lúc ADB tắt)
                    androidReady = false; stats.Reset();  // Xoá trạng thái cũ
                    uiOp = 1;  // "Đang bật"
                    emu.Start(true);  // Cold boot -> ADB bật lại
                }
                finally { uiOp = 0; busy = false; }  // Luôn mở khoá
            });
        }

        // Không cho dùng ADB: dừng macro, ngắt input, tắt adbd trong Android
        void DisableAdb()
        {
            S.Set("AdbOn", false); S.Save();  // Lưu lựa chọn
            MacroRunner.StopAll();  // Macro cần ADB -> dừng
            bool alive = androidReady && !emu.AdbKilled;  // ADB trong Android đang chạy
            Bg("Tắt ADB", () =>
            {
                lock (touch)  // Không để ConnectInput chạy song song
                {
                    var i = touch.Inj; touch.Inj = null;  // Gỡ bộ gửi chạm
                    if (i != null) i.Dispose();  // Đóng scrcpy-server (cần ADB nên làm trước khi tắt)
                    touch.Scr.Detach();  // Dừng đọc kích thước màn hình qua adb
                }
                if (alive) emu.KillAdb();  // Tắt adbd trong Android
            });
        }

        // Tắt/bật Wi-Fi ảo trong Android
        void RefreshNet()
        {
            if (!RequireAdb("Làm mới mạng")) return;  // Cần ADB
            QuickOp("Làm mới mạng", () =>
            {
                if (!emu.RefreshNetwork()) throw new Exception("Chưa có Internet sau ~60s - thử 'Khởi động lại nhanh'.");  // Không có mạng lại thì báo lỗi
                stats.Reset();  // Số liệu cũ không còn đúng
            });
        }

        // Lưu trạng thái vào snapshot dsoft_save
        void SaveState()
        {
            QuickOp("Lưu trạng thái (Android đứng hình ~10-20s)", emu.SaveState);
        }

        // Tắt rồi bật lại giả lập; fromSave = bật từ bản lưu thay vì Quick Boot
        void RestartEmu(bool fromSave)
        {
            if (busy || uiOp != 0) return;  // Đang thao tác khác thì bỏ qua
            if (fromSave && !emu.HasSave) { Log.I("Chưa có bản lưu - bấm 'Lưu trạng thái' khi đang chạy."); return; }  // Chưa lưu thì báo
            // Chạy từ bản lưu sẽ thay trạng thái hiện tại -> hỏi trước
            if (fromSave && (emu.Running || androidReady) &&
                MessageBox.Show(this, "Bật lại từ bản lưu? Màn hình/app đang mở sẽ quay về lúc bấm 'Lưu trạng thái'.", "Chạy từ bản lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SaveUi();  // Lưu cấu hình trên giao diện trước khi bật lại
            busy = true;  // Khoá nút thao tác nhanh
            uiOp = emu.Running ? 2 : 1;  // Đang chạy thì trạng thái "Đang dừng", không thì "Đang bật"
            RefreshRuntime();  // Cập nhật nút ngay
            Bg(fromSave ? "Chạy từ bản lưu" : "Khởi động lại nhanh", () =>
            {
                try
                {
                    if (emu.Running) emu.Stop();  // Tắt mềm (Quick Boot tự lưu trạng thái)
                    androidReady = false;  // Android chưa sẵn sàng
                    stats.Reset();  // Xoá số liệu cũ
                    uiOp = 1;  // Chuyển sang "Đang bật"
                    emu.Restart(fromSave);  // Bật lại
                }
                finally { uiOp = 0; busy = false; }  // Luôn mở khoá
            });
        }

        // Bật WHPX bằng DISM với quyền Admin (cửa sổ cmd giữ lại để xem kết quả)
        void EnableWhpx()
        {
            try  // Bọc lỗi khi người dùng huỷ cấp quyền
            {
                // Chạy cmd /k lệnh DISM; xong thì in nhắc khởi động lại máy
                Process.Start(new ProcessStartInfo("cmd.exe", "/k dism /online /enable-feature /featurename:HypervisorPlatform /all /norestart && echo. && echo XONG - hay khoi dong lai may tinh.")
                { Verb = "runas", UseShellExecute = true });  // runas = Windows hỏi quyền Admin
            }
            catch (Win32Exception) { Log.I("Đã huỷ cấp quyền Admin."); }  // Người dùng bấm No khi hỏi quyền
        }

        // Tải emulator, adb, scrcpy-server ở nền
        void InstallTools() { Bg("Tải Emulator + ADB + scrcpy-server", () => new SdkInstaller(emu.SdkDir).InstallTools()); }

        // Tải bản Android đang chọn ở nền
        void InstallCurrentImage()
        {
            Bg("Tải " + CurImageName, () => new SdkInstaller(emu.SdkDir).InstallAll(emu.Api, emu.Tag, emu.Abi));  // Cài công cụ + image theo API, loại, ABI
        }

        // Bật giả lập; cold = true thì khởi động nguội (bỏ snapshot)
        void StartEmu(bool cold)
        {
            SaveUi();  // Lưu cấu hình trên giao diện trước
            if (!File.Exists(emu.EmulatorExe)) { Log.I("Chưa có Emulator - bấm 'Tải về' ở mục Emulator + ADB."); ShowPage("home"); return; }  // Thiếu emulator thì báo và về Tổng quan
            if (!CurImageInstalled) { Log.I("Chưa tải " + CurImageName + "."); ShowPage("images"); return; }  // Chưa có image thì mở trang Bản Android
            uiOp = 1;  // Đánh dấu đang bật
            RefreshRuntime();  // Cập nhật nút ngay
            // Chạy emulator ở nền; xong hay lỗi đều trả về trạng thái rảnh
            Bg(cold ? "Cold boot " + CurImageName : "Khởi động " + CurImageName, () => { try { emu.Start(cold); } finally { uiOp = 0; } });
        }

        // Dừng giả lập ở nền
        void StopEmu()
        {
            if (uiOp == 2) return;  // Đang dừng rồi thì bỏ qua
            uiOp = 2;  // Đánh dấu đang dừng
            RefreshRuntime();  // Cập nhật nút ngay
            // Dừng emulator; luôn reset cờ dù có lỗi
            Bg("Dừng giả lập", () => { try { emu.Stop(); } finally { androidReady = false; uiOp = 0; } });
        }

        // =============== Bản Android ===============

        // Dựng trang Bản Android
        Control BuildImages()
        {
            // Khai báo các cột dạng "Tên|độ rộng"
            foreach (var c in new[] { "Phiên bản|120", "API|50", "Loại|110", "Dung lượng|95", "Image|170", "Máy ảo (dữ liệu)|210" })
                imgList.Columns.Add(c.Split('|')[0], int.Parse(c.Split('|')[1]));  // Tách tên và độ rộng rồi thêm cột
            imgList.DoubleClick += (s, e) => UseSelectedImage();  // Nhấp đúp = dùng bản này

            var bar = Row();  // Thanh công cụ
            Btn(bar, "✔ Dùng bản này", (s, e) => UseSelectedImage(), CGreen);  // Nút dùng bản đang chọn
            Btn(bar, "⬇ Cài", (s, e) => InstallSelectedImage(false), CBlue);  // Nút cài bản đang chọn
            Btn(bar, "Xoá trắng dữ liệu", (s, e) => WipeSelected(), COrange);  // Nút xoá trắng dữ liệu máy ảo
            Btn(bar, "Xoá máy ảo", (s, e) => DeleteSelectedAvd(), CRed);  // Nút xoá hẳn máy ảo
            Btn(bar, "Xoá image", (s, e) => DeleteSelectedImage());  // Nút xoá file image
            Btn(bar, "Làm mới", (s, e) => LoadImages());  // Nút tải lại danh sách

            // Ghi chú giải thích các nút xoá
            var note = new Label
            {
                Height = 64, ForeColor = CMuted, Padding = new Padding(4, 10, 4, 0),  // Cao 64px, chữ mờ
                Text = "• Xoá trắng dữ liệu: như reset máy - mất app/tài khoản, GIỮ bản Android và cấu hình.\r\n" +  // Dòng 1
                       "• Xoá máy ảo: xoá hẳn máy ảo của bản đó (dùng lại sẽ tạo mới).   • Xoá image: xoá file hệ điều hành, tải lại được.\r\n" +  // Dòng 2
                       "• Mỗi bản có máy ảo riêng, đổi bản không ảnh hưởng dữ liệu bản khác. Play Store = có CH Play."  // Dòng 3
            };
            return ToolPage(bar, imgList, note);  // Ghép thành trang
        }

        // Tải danh sách image từ SDK ở nền
        void LoadImages()
        {
            Bg("Tải danh sách bản Android", () =>  // Chạy ở luồng nền
            {
                var l = new SdkInstaller(emu.SdkDir).ListImages(ImageTags, emu.Abi);  // Lấy danh sách image theo loại và ABI
                UI(() => { images = l; FillImages(); });  // Gán và vẽ lại bảng trên luồng UI
            });
        }

        // Vẽ lại bảng image, giữ dòng đang chọn
        void FillImages()
        {
            var sel = SelImage();  // Nhớ dòng đang chọn
            imgList.BeginUpdate();  // Tạm dừng vẽ cho nhanh
            imgList.Items.Clear();  // Xoá các dòng cũ
            // Sắp API mới nhất trước, bản Play Store đứng trước Google APIs
            foreach (var i in images.OrderByDescending(x => x.Api).ThenBy(x => x.Tag.Contains("playstore") ? 0 : 1))
            {
                bool cur = i.Api == emu.Api && i.Tag == emu.Tag;  // Bản đang dùng
                string avd = emu.WipePendingFor(i.Api, i.Tag) ? "Đã xoá trắng" : emu.AvdExistsFor(i.Api, i.Tag) ? "Có dữ liệu" : "";  // Trạng thái dữ liệu máy ảo
                // Một dòng: phiên bản, API, loại, dung lượng, trạng thái image, máy ảo
                var it = new ListViewItem(new[]
                {
                    i.Version, i.Api.ToString(), i.Kind, i.SizeMB > 0 ? i.SizeMB + " MB" : "",  // Phiên bản, API, loại, dung lượng
                    i.Installed ? (cur ? "✔ Đã cài · đang dùng" : "Đã cài") : (cur ? "Chưa cài · đang chọn" : "Chưa cài"), avd  // Trạng thái cài đặt, trạng thái máy ảo
                }) { Tag = i };  // Tag lưu ImageInfo để lấy lại khi chọn
                if (cur) it.Font = new Font(imgList.Font, FontStyle.Bold);  // Bản đang dùng in đậm
                if (i.Installed) it.ForeColor = CGreen;  // Đã cài thì chữ xanh
                imgList.Items.Add(it);  // Thêm dòng vào bảng
                if (sel != null && sel.Api == i.Api && sel.Tag == i.Tag) it.Selected = true;  // Chọn lại dòng đã chọn trước đó
            }
            imgList.EndUpdate();  // Vẽ lại bảng
        }

        // Lấy image đang chọn trong bảng (null nếu chưa chọn)
        SdkInstaller.ImageInfo SelImage()
        {
            return imgList.SelectedItems.Count == 0 ? null : (SdkInstaller.ImageInfo)imgList.SelectedItems[0].Tag;  // Không chọn thì null, có thì lấy Tag
        }

        // Bản này là bản đang dùng và giả lập đang chạy?
        bool IsCurrentAndRunning(SdkInstaller.ImageInfo i)
        {
            return i.Api == emu.Api && i.Tag == emu.Tag && (emu.Running || androidReady);  // Cùng API, cùng loại và đang chạy
        }

        // Cài bản đang chọn; useAfter = dùng luôn sau khi cài
        void InstallSelectedImage(bool useAfter)
        {
            var i = SelImage();  // Lấy bản đang chọn
            if (i == null) { Log.I("Chọn 1 bản trong danh sách trước."); return; }  // Chưa chọn thì báo
            if (i.Installed) { Log.I(i.Version + " (" + i.Kind + ") đã cài rồi."); if (useAfter) UseImage(i); return; }  // Đã cài rồi thì báo, nếu cần thì dùng luôn
            // Cài ở nền
            Bg("Cài " + i.Version + " " + i.Kind, () =>
            {
                var inst = new SdkInstaller(emu.SdkDir);  // Trình cài SDK
                if (!inst.ToolsInstalled) inst.InstallTools();  // Thiếu công cụ thì tải trước
                inst.InstallImage(i);  // Tải image
                i.Installed = true;  // Đánh dấu đã cài
                if (useAfter) UI(() => UseImage(i));  // Cần dùng luôn thì chọn bản này trên luồng UI
            });
        }

        // Dùng bản đang chọn (hỏi tải nếu chưa cài)
        void UseSelectedImage()
        {
            var i = SelImage();  // Lấy bản đang chọn
            if (i == null) return;  // Chưa chọn thì thôi
            // Chưa cài: hỏi có tải và dùng luôn không
            if (!i.Installed)
            {
                // Hộp thoại Yes/No
                if (MessageBox.Show(this, i.Version + " (" + i.Kind + ", " + i.SizeMB + " MB) chưa cài. Tải và dùng luôn?", "Cài bản Android", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    InstallSelectedImage(true);  // Đồng ý -> cài rồi dùng
                return;  // Kết thúc
            }
            UseImage(i);  // Đã cài thì dùng ngay
        }

        // Lưu bản được chọn vào cấu hình và làm mới giao diện
        void UseImage(SdkInstaller.ImageInfo i)
        {
            S.Set("Api", i.Api);  // Lưu API
            S.Set("ImageTag", i.Tag);  // Lưu loại image
            S.Save();  // Ghi file cấu hình
            RefreshAll();  // Làm mới giao diện
            // Báo đã chọn và hướng dẫn cách chuyển bản
            Log.I("Đã chọn " + i.Version + " (" + i.Kind + "), máy ảo " + emu.AvdName + ". " +
                  (emu.Running ? "Bấm ■ Dừng rồi ▶ Chạy để chuyển." : "Bấm ▶ Chạy ở Tổng quan."));  // Đang chạy thì phải dừng rồi chạy lại
        }

        // Xoá file image (giữ dữ liệu máy ảo)
        void DeleteSelectedImage()
        {
            var i = SelImage();  // Lấy bản đang chọn
            if (i == null || !i.Installed) return;  // Chưa chọn hoặc chưa cài thì thôi
            if (IsCurrentAndRunning(i)) { MessageBox.Show(this, "Bản này đang chạy, dừng giả lập trước."); return; }  // Đang chạy thì không cho xoá
            // Hỏi xác nhận, không đồng ý thì thôi
            if (MessageBox.Show(this, "Xoá image " + i.Version + " (" + i.Kind + ")?\r\nDữ liệu máy ảo vẫn giữ, muốn dùng lại phải tải lại image.", "Xoá image", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            Bg("Xoá image", () => { new SdkInstaller(emu.SdkDir).DeleteImage(i.Api, i.Tag, i.Abi); i.Installed = false; });  // Xoá image ở nền và đánh dấu chưa cài
        }

        // Xoá trắng dữ liệu máy ảo của bản đang chọn
        void WipeSelected()
        {
            var i = SelImage();  // Lấy bản đang chọn
            if (i == null || !emu.AvdExistsFor(i.Api, i.Tag)) { Log.I("Bản này chưa có máy ảo/dữ liệu để xoá."); return; }  // Chưa có máy ảo thì báo
            if (IsCurrentAndRunning(i)) { MessageBox.Show(this, "Máy ảo này đang chạy, bấm ■ Dừng trước."); return; }  // Đang chạy thì không cho xoá
            // Hỏi xác nhận (cảnh báo không khôi phục được)
            if (MessageBox.Show(this, "Xoá trắng toàn bộ app, tài khoản, dữ liệu trong " + i.Version + " (" + i.Kind + ")?\r\n\r\nBản Android và cấu hình vẫn giữ. Không khôi phục được.",
                    "Xoá trắng dữ liệu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;  // Không đồng ý thì thôi
            Bg("Xoá trắng dữ liệu " + i.Version, () => emu.WipeAvd(i.Api, i.Tag));  // Xoá trắng ở nền
        }

        // Xoá hẳn máy ảo của bản đang chọn
        void DeleteSelectedAvd()
        {
            var i = SelImage();  // Lấy bản đang chọn
            if (i == null || !emu.AvdExistsFor(i.Api, i.Tag)) return;  // Chưa có máy ảo thì thôi
            if (IsCurrentAndRunning(i)) { MessageBox.Show(this, "Máy ảo này đang chạy, dừng giả lập trước."); return; }  // Đang chạy thì không cho xoá
            // Hỏi xác nhận, không đồng ý thì thôi
            if (MessageBox.Show(this, "XOÁ hẳn máy ảo " + i.Version + " (" + i.Kind + ") cùng toàn bộ dữ liệu?\r\nKhông khôi phục được.", "Xoá máy ảo", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Bg("Xoá máy ảo", () => emu.DeleteAvd(i.Api, i.Tag));  // Xoá máy ảo ở nền
        }

        // =============== Cấu hình ===============

        // Dựng trang Cấu hình
        Control BuildConfig()
        {
            var grid = new TableLayoutPanel { ColumnCount = 4, AutoSize = true };  // Lưới 4 cột: nhãn, ô nhập, gợi ý, cột đệm
            // Hàm thêm 1 dòng cấu hình, trả về control để gán vào biến
            Func<string, string, Control, Control> row = (label, hint, c) =>
            {
                grid.Controls.Add(new Label { Text = label, AutoSize = true, Font = FBold, Margin = new Padding(0, 9, 10, 0) });  // Nhãn tên
                c.Margin = new Padding(0, 5, 10, 5);  // Lề ô nhập
                grid.Controls.Add(c);  // Ô nhập
                grid.Controls.Add(new Label { Text = hint, AutoSize = true, ForeColor = CMuted, Margin = new Padding(0, 9, 30, 0) });  // Gợi ý
                grid.Controls.Add(new Label { AutoSize = true });  // Ô trống cho cột đệm
                return c;  // Trả về ô nhập
            };
            // Số nhân CPU (tối đa = số luồng của máy)
            nCores = (NumericUpDown)row("Nhân CPU", "Máy có " + host.Threads + " luồng, nên để ≤ một nửa", Num(1, Math.Max(1, host.Threads), S.GetInt("Cores", 4), 1));
            // RAM máy ảo, bước 512 MB
            nRam = (NumericUpDown)row("RAM (MB)", "Máy có " + host.RamMB / 1024 + " GB", Num(1024, 16384, S.GetInt("RamMB", 4096), 512));
            // Chiều rộng màn hình máy ảo
            nW = (NumericUpDown)row("Rộng", "Rộng > Cao = màn ngang", Num(480, 3840, S.GetInt("Width", 1280), 8));
            // Chiều cao màn hình máy ảo
            nH = (NumericUpDown)row("Cao", "", Num(480, 3840, S.GetInt("Height", 720), 8));
            // Mật độ điểm ảnh
            nDpi = (NumericUpDown)row("DPI", "Càng cao chữ càng to", Num(120, 640, S.GetInt("Dpi", 240), 20));
            // Chế độ GPU
            cGpu = (ComboBox)row("GPU", "host = nhanh nhất. Nhấp nháy/màn đen: thử swangle (vẽ bằng CPU, chậm hơn)", Combo(emu.GpuMode, EmulatorController.GpuModes));
            cGpu.DropDownStyle = ComboBoxStyle.DropDownList;  // Chỉ cho chọn trong danh sách, tránh gõ sai tên chế độ
            cGpu.SelectedItem = emu.GpuMode;  // Chọn lại giá trị hiện tại sau khi đổi kiểu ô
            // DNS cho Android: mặc định theo máy thật
            cDns = (ComboBox)row("DNS", "Theo máy = dùng DNS của Windows. Mạng công ty nên để Theo máy", Combo(DnsLabel(S.Get("Dns", "")), DnsAuto, "8.8.8.8,8.8.4.4", "1.1.1.1,1.0.0.1"));
            // Tham số thêm cho emulator
            tExtra = (TextBox)row("Tham số thêm", "Cho người dùng nâng cao", new TextBox { Width = 220, Text = S.Get("ExtraArgs", "") });

            var opts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };  // Khối các ô tick xếp dọc
            kQuick = Chk(opts, "Quick boot - lưu trạng thái khi tắt, mở lại nhanh", S.GetBool("QuickBoot", true));  // Quick boot
            kBootAnim = Chk(opts, "Hiện hoạt ảnh khởi động (để biết đang boot, không bị màn đen)", S.GetBool("BootAnim", true));  // Hiện hoạt ảnh boot
            kNoVulkan = Chk(opts, "Tắt Vulkan - ổn định hơn trên GPU Intel", S.GetBool("DisableVulkan", true));  // Tắt Vulkan
            kRestart = Chk(opts, "Tự khởi động lại khi giả lập crash", S.GetBool("AutoRestart", true));  // Tự khởi động lại khi crash
            kPrio = Chk(opts, "Ưu tiên CPU cao cho giả lập", S.GetBool("HighPriority", true));  // Ưu tiên CPU cao
            kNoAudio = Chk(opts, "Tắt âm thanh - nhẹ hơn chút", S.GetBool("NoAudio", false));  // Tắt âm thanh
            kStats = Chk(opts, "Hiện tốc độ mạng / ping / FPS ở thanh bên (đọc qua adb mỗi 2 giây)", S.GetBool("ShowStats", true));  // Hiện số liệu
            kStats.CheckedChanged += (s, e) => { S.Set("ShowStats", kStats.Checked); S.Save(); RefreshRuntime(); };  // Áp dụng ngay, không cần bấm Lưu

            var bar = Row();  // Thanh nút trên cùng
            // Nút Lưu cấu hình
            Btn(bar, "Lưu cấu hình", (s, e) => { SaveUi(); Log.I("Đã lưu cấu hình. Áp dụng ở lần ▶ Chạy tới."); RefreshAll(); }, CBlue);
            // Nút Đề xuất theo máy: điền lại giá trị đề xuất (chưa lưu)
            Btn(bar, "Đề xuất theo máy", (s, e) =>
            {
                host.ApplyRecommended(S);  // Ghi giá trị đề xuất vào S
                nCores.Value = S.GetInt("Cores", 4); nRam.Value = S.GetInt("RamMB", 4096);  // Cập nhật ô nhân CPU và RAM
                nW.Value = S.GetInt("Width", 1280); nH.Value = S.GetInt("Height", 720); nDpi.Value = S.GetInt("Dpi", 240);  // Cập nhật ô rộng, cao, DPI
                cGpu.Text = emu.GpuMode;  // Cập nhật ô GPU
                Log.I("Đã điền cấu hình đề xuất, bấm Lưu để áp dụng.");  // Nhắc bấm Lưu
            });
            // Ghi chú thời điểm áp dụng
            bar.Controls.Add(new Label { AutoSize = true, ForeColor = CMuted, Margin = new Padding(6, 13, 0, 0), Text = "Thay đổi có hiệu lực khi giả lập khởi động lần tới." });

            var store = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };  // Khối nơi lưu xếp dọc
            store.Controls.Add(lStore);  // Nhãn nơi lưu và dung lượng
            var sbar = Row();  // Hàng nút
            Btn(sbar, "Đổi thư mục lưu...", (s, e) => ChangeStorage(), CBlue);  // Nút đổi thư mục lưu
            // Nút mở thư mục cha của SDK trong Explorer
            Btn(sbar, "Mở thư mục", (s, e) => Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(emu.SdkDir) + "\""));
            Btn(sbar, "Tính lại dung lượng", (s, e) => RefreshStorage());  // Nút tính lại dung lượng
            store.Controls.Add(sbar);  // Thêm hàng nút

            // Ghép các thẻ thành trang cuộn
            return Stack(Card(null, bar), Card("Nơi lưu dữ liệu (image + máy ảo)", store), Card("Phần cứng máy ảo", grid), Card("Tuỳ chọn", opts));
        }

        // Nhãn hiển thị nơi lưu và dung lượng
        readonly Label lStore = new Label { AutoSize = true, ForeColor = CText, Margin = new Padding(0, 0, 0, 8), Text = "Đang tính dung lượng..." };

        // Tính dung lượng thư mục ở nền (có thể chậm)
        void RefreshStorage()
        {
            string sdk = emu.SdkDir, avd = emu.AvdHome;  // Chép đường dẫn ra biến cục bộ trước khi sang luồng nền
            Task.Run(() =>  // Chạy nền
            {
                string t;  // Chuỗi kết quả
                try  // Bọc lỗi đọc ổ/thư mục
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(sdk)));  // Ổ chứa thư mục SDK
                    // Ghép chuỗi: dung lượng image, máy ảo, ổ trống
                    t = "Image + emulator:  " + sdk + "   (" + (FileUtil.DirSize(sdk) / 1073741824.0).ToString("0.0") + " GB)\r\n" +
                        "Máy ảo (dữ liệu):  " + avd + "   (" + (FileUtil.DirSize(avd) / 1073741824.0).ToString("0.0") + " GB)\r\n" +  // Dòng máy ảo
                        "Ổ " + drive.Name + " còn trống " + (drive.AvailableFreeSpace / 1073741824.0).ToString("0.0") + " GB.  " +  // Dòng ổ trống
                        "Mỗi bản Android cần ~2-3 GB image + 2-8 GB dữ liệu máy ảo.";  // Dòng gợi ý dung lượng cần
                }
                catch (Exception ex) { t = "Không đọc được: " + ex.Message; }  // Lỗi thì hiện thông báo lỗi
                UI(() => lStore.Text = t);  // Hiện kết quả trên luồng UI
            });
        }

        // Đổi thư mục lưu image + máy ảo, có thể chuyển dữ liệu cũ sang
        void ChangeStorage()
        {
            if (emu.Running || androidReady) { MessageBox.Show(this, "Dừng giả lập trước khi đổi nơi lưu."); return; }  // Đang chạy thì không cho đổi
            string dir;  // Thư mục mới
            // Hộp thoại chọn thư mục
            using (var d = new FolderBrowserDialog { Description = "Chọn thư mục lưu image + máy ảo (nên chọn ổ còn nhiều chỗ, ví dụ D:\\DsoftData)", ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;  // Huỷ thì thôi
                dir = d.SelectedPath;  // Lấy thư mục đã chọn
            }
            bool hasData = Directory.Exists(emu.SdkDir) || Directory.Exists(emu.AvdHome);  // Có dữ liệu cũ không
            // Không có dữ liệu thì coi như No; có thì hỏi Yes/No/Cancel
            var r = !hasData ? DialogResult.No : MessageBox.Show(this,
                "Lưu vào: " + dir + "\r\n\r\nChuyển dữ liệu hiện có (image + máy ảo) sang đó?\r\n\r\n" +  // Nội dung câu hỏi
                "Yes = chuyển sang (khác ổ sẽ copy, có thể mất vài phút)\r\nNo = chỉ đổi chỗ lưu, phải tải lại image\r\nCancel = huỷ",  // Giải thích các lựa chọn
                "Đổi nơi lưu", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);  // Tiêu đề và nút
            if (r == DialogResult.Cancel) return;  // Cancel thì huỷ
            bool move = r == DialogResult.Yes;  // Yes = chuyển dữ liệu
            // Chạy ở nền
            Bg(move ? "Chuyển dữ liệu sang " + dir : "Đổi nơi lưu sang " + dir, () =>
            {
                emu.MoveData(dir, move);  // Chuyển dữ liệu hoặc chỉ đổi nơi lưu
                images.Clear();  // Xoá danh sách image cũ
                UI(() => { RefreshStorage(); LoadImages(); });  // Tính lại dung lượng và tải lại danh sách
            });
        }

        // Tạo ô số có giới hạn và bước nhảy
        static NumericUpDown Num(int min, int max, int val, int inc)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), Increment = inc, Width = 100, Font = FBase };  // Giá trị được kẹp trong [min, max]
        }

        // Tạo ô chọn có sẵn danh sách (vẫn gõ tay được)
        static ComboBox Combo(string val, params string[] items)
        {
            var c = new ComboBox { Width = 180, Font = FBase };  // Ô chọn rộng 180px
            c.Items.AddRange(items);  // Thêm các lựa chọn
            c.Text = val;  // Giá trị hiện tại
            return c;  // Trả về ô chọn
        }

        // Tạo ô tick và thêm vào parent
        static CheckBox Chk(Control parent, string text, bool v)
        {
            var c = new CheckBox { Text = text, Checked = v, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };  // Ô tick với giá trị ban đầu
            parent.Controls.Add(c);  // Thêm vào parent
            return c;  // Trả về ô tick
        }

        // Lưu giá trị trên trang Cấu hình vào settings.ini
        void SaveUi()
        {
            S.Set("Cores", (int)nCores.Value);  // Nhân CPU
            S.Set("RamMB", (int)nRam.Value);  // RAM
            S.Set("Width", (int)nW.Value);  // Rộng
            S.Set("Height", (int)nH.Value);  // Cao
            S.Set("Dpi", (int)nDpi.Value);  // DPI
            S.Set("Gpu", cGpu.Text.Trim());  // GPU
            S.Set("Dns", cDns.Text.Trim() == DnsAuto ? "" : cDns.Text.Trim().Replace(" ", ""));  // DNS: "Theo máy" lưu thành rỗng
            S.Set("ExtraArgs", tExtra.Text.Trim());  // Tham số thêm
            S.Set("QuickBoot", kQuick.Checked);  // Quick boot
            S.Set("BootAnim", kBootAnim.Checked);  // Hoạt ảnh boot
            S.Set("NoAudio", kNoAudio.Checked);  // Tắt âm thanh
            S.Set("DisableVulkan", kNoVulkan.Checked);  // Tắt Vulkan
            S.Set("AutoRestart", kRestart.Checked);  // Tự khởi động lại
            S.Set("HighPriority", kPrio.Checked);  // Ưu tiên CPU cao
            S.Save();  // Ghi file
        }

        // =============== Map phím ===============

        // Dựng trang Map phím
        Control BuildKeys()
        {
            var bar = Row();  // Thanh công cụ
            bar.Controls.Add(kMap);  // Ô tick bật map phím
            // Bật/tắt map phím theo ô tick; người dùng tự tick bật khi ADB tắt -> hỏi bật ADB, từ chối thì bỏ tick
            kMap.CheckedChanged += (s, e) =>
            {
                if (kMap.Checked && !syncingMap && !AdbReady && !RequireAdb("Map phím")) { syncingMap = true; kMap.Checked = false; syncingMap = false; }
                mapper.Enabled = kMap.Checked;
            };
            Btn(bar, "Lưu && áp dụng  (Ctrl+S)", (s, e) => ApplyKeymap(true), CBlue);  // "&&" hiển thị thành "&" trên nút
            Btn(bar, "Lấy toạ độ từ màn hình...", (s, e) => Pick(keyEdit));  // Nút lấy toạ độ từ ảnh chụp màn hình
            bar.Controls.Add(inputLbl);  // Nhãn trạng thái input
            // Ctrl+S trong ô soạn = lưu và áp dụng
            keyEdit.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S) { ApplyKeymap(true); e.SuppressKeyPress = true; } };
            return ToolPage(bar, keyEdit, null);  // Ghép thành trang
        }

        // Nạp keymap từ ô soạn; save = ghi ra file trước
        void ApplyKeymap(bool save)
        {
            if (save) File.WriteAllText(KeymapPath, keyEdit.Text);  // Ghi file keymap
            foreach (var e in mapper.Load(keyEdit.Text)) Log.I("  keymap " + e);  // Nạp keymap, ghi log từng lỗi
        }

        // Mở cửa sổ chọn toạ độ trên ảnh chụp màn hình, chèn kết quả vào ô soạn
        void Pick(TextBox target)
        {
            if (!RequireAdb("Lấy toạ độ từ màn hình")) return;  // Chụp màn hình cần ADB
            var adb = emu.NewAdb();  // Tạo kết nối adb
            // Chọn xong thì chèn chuỗi toạ độ vào vị trí con trỏ
            new PickerForm(adb.Screencap, t => UI(() => { target.SelectedText = t; target.Focus(); })).Show(this);
        }

        // =============== Auto click ===============

        // Dựng trang Auto click
        Control BuildMacro()
        {
            var bar = Row();  // Thanh công cụ
            // Nút Chạy: lưu rồi bật macro đang chọn nếu chưa chạy
            Btn(bar, "▶ Chạy", (s, e) => { SaveMacro(); var n = CurMacro(); if (n != null && !MacroRunner.IsRunning(n) && RequireAdb("Auto click")) ToggleMacro(n); }, CGreen);
            // Nút Dừng macro đang chọn
            Btn(bar, "■ Dừng", (s, e) => { var n = CurMacro(); if (n != null) MacroRunner.Stop(n); }, CRed);
            Btn(bar, "Dừng tất cả", (s, e) => MacroRunner.StopAll());  // Nút dừng tất cả macro
            Btn(bar, "Mới", (s, e) => NewMacro());  // Nút tạo macro mới
            Btn(bar, "Lưu (Ctrl+S)", (s, e) => SaveMacro(), CBlue);  // Nút lưu macro
            Btn(bar, "Lấy toạ độ...", (s, e) => Pick(macroEdit));  // Nút lấy toạ độ
            // Nút xoá macro đang chọn sau khi xác nhận
            Btn(bar, "Xoá", (s, e) =>
            {
                var n = CurMacro();  // Tên macro đang chọn
                if (n == null || MessageBox.Show(this, "Xoá macro '" + n + "'?", "Xác nhận", MessageBoxButtons.YesNo) != DialogResult.Yes) return;  // Chưa chọn hoặc không đồng ý thì thôi
                MacroRunner.Stop(n);  // Dừng macro nếu đang chạy
                File.Delete(Path.Combine(MacroDir, n + ".txt"));  // Xoá file macro
                RefreshMacroList();  // Vẽ lại danh sách
            });
            // Chọn macro khác -> nạp nội dung vào ô soạn
            macroList.SelectedIndexChanged += (s, e) =>
            {
                var n = CurMacro();  // Tên macro đang chọn
                if (n != null) macroEdit.Text = Crlf(File.ReadAllText(Path.Combine(MacroDir, n + ".txt")));  // Đọc file, đổi xuống dòng cho TextBox
            };
            // Ctrl+S trong ô soạn = lưu macro
            macroEdit.KeyDown += (s, e) => { if (e.Control && e.KeyCode == Keys.S) { SaveMacro(); e.SuppressKeyPress = true; } };

            var main = new Panel { Dock = DockStyle.Fill };  // Vùng chính
            main.Controls.Add(macroEdit);  // Ô soạn (Fill)
            main.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10 });  // Khoảng cách 10px
            main.Controls.Add(macroList);  // Danh sách macro bên trái
            return ToolPage(bar, main, null);  // Ghép thành trang
        }

        // Tên macro đang chọn (bỏ dấu ▶ ở đầu)
        string CurMacro()
        {
            var o = macroList.SelectedItem as string;  // Mục đang chọn
            return o == null ? null : o.TrimStart('▶', ' ');  // Bỏ ký tự ▶ và khoảng trắng đầu
        }

        // Vẽ lại danh sách macro, giữ lựa chọn hiện tại
        void RefreshMacroList()
        {
            string cur = CurMacro();  // Nhớ macro đang chọn
            Directory.CreateDirectory(MacroDir);  // Tạo thư mục nếu chưa có
            var names = Directory.GetFiles(MacroDir, "*.txt").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList();  // Lấy tên các file .txt, sắp theo tên
            macroList.BeginUpdate();  // Tạm dừng vẽ
            macroList.Items.Clear();  // Xoá danh sách cũ
            foreach (var n in names) macroList.Items.Add((MacroRunner.IsRunning(n) ? "▶ " : "") + n);  // Thêm từng tên, macro đang chạy có dấu ▶
            int i = cur == null ? -1 : names.IndexOf(cur);  // Vị trí của macro đã chọn trước đó
            if (i >= 0) macroList.SelectedIndex = i;  // Chọn lại nếu còn
            else if (names.Count > 0 && cur == null) macroList.SelectedIndex = 0;  // Chưa chọn gì thì chọn mục đầu
            macroList.EndUpdate();  // Vẽ lại
        }

        // Tạo file macro mẫu mới
        void NewMacro()
        {
            string name = "macro" + DateTime.Now.ToString("HHmmss");  // Tên theo giờ phút giây
            // Nội dung mẫu: lặp vô hạn, chạm giữa màn hình, chờ 0,8-1,5 giây
            File.WriteAllText(Path.Combine(MacroDir, name + ".txt"), "# " + name + "\r\nloop 0\r\ntap 0.5 0.5 0.02\r\nwait 800 1500\r\n");
            RefreshMacroList();  // Vẽ lại danh sách
            macroList.SelectedItem = name;  // Chọn macro vừa tạo
        }

        // Lưu macro, kiểm tra cú pháp và báo lỗi
        void SaveMacro()
        {
            var n = CurMacro();  // Tên macro đang chọn
            if (n == null) return;  // Chưa chọn thì thôi
            File.WriteAllText(Path.Combine(MacroDir, n + ".txt"), macroEdit.Text);  // Ghi file
            var errs = new List<string>();  // Danh sách lỗi cú pháp
            Macro.Parse(n, macroEdit.Text, errs);  // Phân tích để tìm lỗi
            Log.I("Đã lưu macro '" + n + "'" + (errs.Count > 0 ? " - " + string.Join("; ", errs) : ""));  // Báo đã lưu kèm lỗi (nếu có)
        }

        /// <summary>Bật/tắt macro theo tên (gọi từ nút hoặc phím tắt trong keymap).</summary>
        void ToggleMacro(string name)
        {
            if (MacroRunner.IsRunning(name)) { MacroRunner.Stop(name); return; }  // Đang chạy thì dừng
            // Gọi từ phím tắt lúc đang chơi: không bật hộp thoại (sẽ che game), chỉ ghi log
            if (!AdbReady) { Log.I("Macro '" + name + "' cần ADB - bấm 'ADB: TẮT' ở thanh bên để bật."); return; }
            string f = Path.Combine(MacroDir, name + ".txt");  // Đường dẫn file macro
            if (!File.Exists(f)) { Log.I("Không có macro " + f); return; }  // Không có file thì báo
            var errs = new List<string>();  // Danh sách lỗi cú pháp
            var m = Macro.Parse(name, File.ReadAllText(f), errs);  // Phân tích file macro
            foreach (var e in errs) Log.I("  macro " + name + " " + e);  // Ghi log từng lỗi
            if (!touch.Ready) Log.I("⚠ Input chưa sẵn sàng, macro sẽ chờ tới khi kết nối.");  // Chưa có input thì cảnh báo
            MacroRunner.Start(m, touch);  // Chạy macro
        }

        // =============== Chung ===============

        // Kết nối điều khiển: ưu tiên scrcpy (nhanh), lỗi thì dùng adb input (chậm)
        void ConnectInput()
        {
            if (!AdbReady) return;  // Không được dùng ADB thì không kết nối
            lock (touch)  // Khoá tránh 2 luồng kết nối cùng lúc
            {
                var adb = emu.NewAdb();  // Tạo kết nối adb
                if (!adb.Online()) { Log.I("Chưa thấy thiết bị " + adb.Serial + " trong adb."); return; }  // Thiết bị chưa online thì báo
                androidReady = true;  // Đánh dấu Android sẵn sàng
                var old = touch.Inj;  // Bộ gửi chạm cũ
                touch.Inj = null;  // Gỡ bộ cũ
                if (old != null) old.Dispose();  // Giải phóng bộ cũ
                touch.Scr.Attach(adb);  // Gắn adb cho phần đọc thông tin màn hình

                ITouch inj = null;  // Bộ gửi chạm mới
                string jar = SdkInstaller.ScrcpyFile(emu.SdkDir);  // Đường dẫn scrcpy-server
                // Có scrcpy-server thì thử dùng
                if (File.Exists(jar))
                {
                    var s = new ScrcpyTouch(adb, touch.Scr, jar);  // Tạo bộ gửi chạm scrcpy
                    if (s.Start()) inj = s;  // Khởi động được thì dùng
                    else Log.I("⚠ scrcpy-server không khởi động được, chuyển sang adb input.");  // Không được thì báo
                }
                else Log.I("⚠ Thiếu " + jar + " -> dùng adb input (chậm).");  // Thiếu file scrcpy thì báo
                if (inj == null) inj = new AdbTouch(adb, touch.Scr);  // Dự phòng: dùng adb input
                touch.Inj = inj;  // Gắn bộ gửi chạm mới
                UI(RefreshRuntime);  // Cập nhật trạng thái
            }
        }

        // Cài APK qua adb ở nền
        void InstallApk(string file)
        {
            // -r = cài đè, -g = cấp sẵn quyền, timeout 5 phút
            Bg("Cài " + Path.GetFileName(file), () => Log.I(emu.NewAdb().Run("install -r -g \"" + file + "\"", 300000).Trim()));
        }

        // Khởi động phần còn lại sau khi form hiện
        void OnLoaded()
        {
            Directory.CreateDirectory(Path.Combine(baseDir, "keymaps"));  // Tạo thư mục keymaps
            if (File.Exists(KeymapPath)) keyEdit.Text = Crlf(File.ReadAllText(KeymapPath));  // Có file keymap thì nạp vào ô soạn
            ApplyKeymap(false);  // Áp dụng keymap (không ghi file)
            mapper.Install();  // Cài hook bàn phím
            RefreshMacroList();  // Vẽ danh sách macro
            RefreshAll();  // Làm mới giao diện
            RefreshStorage();  // Tính dung lượng
            CheckAccel();  // Kiểm tra ảo hoá
            timer.Start();  // Bật timer làm mới
            // Mở app khi giả lập đã chạy sẵn: có ADB thì kết nối input, không cho dùng ADB thì tắt nó đi
            if (File.Exists(emu.AdbExe)) Task.Run(() =>
            {
                if (!emu.NewAdb().Booted()) return;  // Chưa có Android chạy sẵn
                androidReady = true;  // Android đang chạy
                emu.AdbKilled = false;  // ADB đang chạy
                if (emu.AdbOn) ConnectInput(); else emu.KillAdb();  // Kết nối hoặc tắt ADB
                UI(RefreshAll);  // Làm mới giao diện
            });
        }

        // Xử lý khi đóng form: hỏi tắt giả lập, dừng macro, giải phóng tài nguyên
        void OnClosing(object sender, FormClosingEventArgs e)
        {
            // Giả lập đang chạy thì hỏi
            if (emu.Running)
            {
                var r = MessageBox.Show(this, "Tắt luôn giả lập?", "Thoát", MessageBoxButtons.YesNoCancel);  // Hỏi Yes/No/Cancel
                if (r == DialogResult.Cancel) { e.Cancel = true; return; }  // Cancel = không đóng form
                if (r == DialogResult.Yes) emu.Stop();  // Yes = tắt luôn giả lập
            }
            timer.Stop();  // Dừng timer
            MacroRunner.StopAll();  // Dừng tất cả macro
            mapper.Dispose();  // Gỡ hook bàn phím
            var i = touch.Inj;  // Bộ gửi chạm hiện tại
            if (i != null) i.Dispose();  // Giải phóng nếu có
        }
    }
}
