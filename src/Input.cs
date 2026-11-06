using System; // Kiểu cơ bản: Math, Random, IDisposable, DateTime...
using System.Collections.Generic; // Dictionary
using System.Diagnostics; // Process (tiến trình adb/scrcpy-server)
using System.IO; // IOException
using System.Net.Sockets; // TcpClient, NetworkStream, SocketException
using System.Text.RegularExpressions; // Regex để đọc kết quả lệnh adb shell
using System.Threading; // Thread, CancellationToken, Timeout

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    /// <summary>Kích thước màn hình Android theo hướng xoay hiện tại (poll nhẹ mỗi 3s).</summary>
    public class Screen
    {
        public volatile int W, H, Rot; // Rộng, cao (pixel, đã tính xoay) và hướng xoay 0..3
        Adb adb; // Đối tượng adb để chạy lệnh shell
        Thread th; // Thread nền poll kích thước định kỳ

        // Gắn adb, đọc kích thước ngay và khởi động thread poll (chỉ một lần)
        public void Attach(Adb a)
        {
            adb = a; // Lưu adb
            Refresh(); // Đọc kích thước ngay lập tức
            if (th == null) // Chưa có thread poll thì tạo
            {
                // Thread nền: cứ 3 giây gọi Refresh một lần, nuốt mọi lỗi để không chết thread
                th = new Thread(() => { while (true) { Thread.Sleep(3000); try { Refresh(); } catch { } } }) { IsBackground = true };
                th.Start(); // Chạy thread
            }
        }

        // Gỡ adb: thread poll vẫn chạy nhưng không gọi adb nữa (dùng khi tắt ADB)
        public void Detach() { adb = null; }

        // Đọc kích thước và hướng xoay màn hình qua adb shell, cập nhật W/H/Rot
        public void Refresh()
        {
            if (adb == null) return; // Chưa gắn adb -> thoát
            // Chạy 3 lệnh trong 1 lần shell: wm size, hướng xoay từ dumpsys input, và dự phòng từ dumpsys window (timeout 8s)
            string o = adb.Shell("\"wm size; dumpsys input | grep -m1 SurfaceOrientation; dumpsys window displays | grep -m1 -E 'mCurrentRotation|mRotation='\"", 8000);
            var m = Regex.Match(o, @"Override size:\s*(\d+)x(\d+)"); // Ưu tiên kích thước bị ghi đè (wm size đã đổi)
            if (!m.Success) m = Regex.Match(o, @"Physical size:\s*(\d+)x(\d+)"); // Không có thì lấy kích thước vật lý
            if (!m.Success) return; // Không đọc được -> giữ giá trị cũ
            int w = int.Parse(m.Groups[1].Value), h = int.Parse(m.Groups[2].Value); // Lấy rộng và cao từ nhóm regex

            int rot = 0; // Hướng xoay, mặc định 0 (dọc)
            var r = Regex.Match(o, @"SurfaceOrientation:\s*(\d)"); // Tìm hướng xoay trong dumpsys input
            if (r.Success) rot = int.Parse(r.Groups[1].Value); // Tìm thấy -> dùng giá trị 0..3
            else // Không thấy -> thử dumpsys window
            {
                r = Regex.Match(o, @"(?:mCurrentRotation|mRotation)=(?:ROTATION_)?(\d+)"); // Dạng "mRotation=1" hoặc "ROTATION_90"
                if (r.Success) { rot = int.Parse(r.Groups[1].Value); if (rot >= 90) rot /= 90; } // Nếu là độ (90/180/270) thì đổi về 1/2/3
            }
            if (rot % 2 == 1) { int t = w; w = h; h = t; } // Xoay ngang (90/270) -> hoán đổi rộng và cao
            if (w != W || h != H) Log.I("Màn hình Android: " + w + "x" + h + " (xoay " + rot * 90 + "°)"); // Kích thước đổi -> ghi log
            W = w; H = h; Rot = rot; // Cập nhật giá trị mới
        }
    }

    // Giao diện chung cho các cách bơm cảm ứng vào Android
    public interface ITouch : IDisposable
    {
        bool Alive { get; } // Kênh bơm còn hoạt động không
        string Name { get; } // Tên mô tả phương thức bơm
        /// <summary>action: 0=down 1=up 2=move. nx,ny chuẩn hoá 0..1</summary>
        void Send(int action, long id, double nx, double ny); // Gửi một sự kiện chạm cho ngón có pointer id
    }

    /// <summary>Bơm cảm ứng đa điểm độ trễ thấp qua socket điều khiển của scrcpy-server (không stream video).</summary>
    public class ScrcpyTouch : ITouch
    {
        readonly Adb adb; // adb dùng để push/forward/khởi chạy server
        readonly Screen scr; // Kích thước màn hình hiện tại
        readonly string jar; // Đường dẫn file scrcpy-server.jar trên máy Windows
        readonly object lk = new object(); // Khoá để ghi socket tuần tự từ nhiều thread
        Process server; // Tiến trình adb shell đang chạy scrcpy-server
        TcpClient tcp; // Kết nối TCP tới socket điều khiển
        NetworkStream ns; // Luồng dữ liệu của kết nối
        int port; // Cổng TCP local đã forward
        volatile bool alive; // Kết nối còn sống

        public ScrcpyTouch(Adb adb, Screen scr, string jar) { this.adb = adb; this.scr = scr; this.jar = jar; } // Hàm tạo: lưu các tham số

        public bool Alive { get { return alive; } } // Trả trạng thái kết nối
        public string Name { get { return "scrcpy-server " + SdkInstaller.ScrcpyVersion + " (đa điểm, nhanh)"; } } // Tên hiển thị kèm phiên bản

        // Đẩy server lên máy, forward cổng, khởi chạy server và kết nối socket điều khiển; trả true nếu thành công
        public bool Start()
        {
            string push = adb.Run("push \"" + jar + "\" /data/local/tmp/scrcpy-server.jar", 30000); // Đẩy file jar vào /data/local/tmp (timeout 30s)
            if (push.Contains("error")) { Log.I("push scrcpy-server lỗi: " + push.Trim()); return false; } // Push lỗi -> ghi log, thất bại

            var rnd = new Random(); // Bộ sinh số ngẫu nhiên
            int scid = rnd.Next(1, int.MaxValue); // scid ngẫu nhiên để socket không trùng phiên khác
            string id = scid.ToString("x8"); // Đổi scid sang 8 ký tự hex
            port = 27183 + rnd.Next(0, 200); // Chọn cổng local ngẫu nhiên trong 27183..27382
            adb.Run("forward tcp:" + port + " localabstract:scrcpy_" + id, 5000); // Forward cổng local tới socket abstract scrcpy_<scid> trên máy

            // Tham số chạy server: chạy jar bằng app_process, đúng phiên bản và scid
            string args = adb.Target + " shell CLASSPATH=/data/local/tmp/scrcpy-server.jar app_process / com.genymobile.scrcpy.Server " +
                SdkInstaller.ScrcpyVersion + " scid=" + id + // Phiên bản phải khớp jar; scid chọn socket
                " log_level=info video=false audio=false control=true tunnel_forward=true cleanup=true" + // Tắt video/audio, chỉ bật điều khiển, dùng forward
                " power_off_on_close=false send_device_meta=false send_dummy_byte=true clipboard_autosync=false"; // Gửi 1 byte giả khi sẵn sàng, không gửi metadata
            server = Proc.Start(adb.Exe, args, null, l => Log.I("[scrcpy] " + l), false); // Khởi chạy tiến trình, mỗi dòng output ghi vào log

            for (int i = 0; i < 60; i++) // Thử kết nối tối đa 60 lần (~6 giây)
            {
                Thread.Sleep(100); // Chờ 100ms giữa các lần thử
                if (server.HasExited) break; // Server đã thoát -> dừng thử
                TcpClient c = null; // Kết nối thử
                try
                {
                    c = new TcpClient { NoDelay = true }; // Tạo TCP client, tắt Nagle để gửi ngay
                    c.Connect("127.0.0.1", port); // Kết nối cổng local đã forward
                    var s = c.GetStream(); // Lấy luồng dữ liệu
                    s.ReadTimeout = 1500; // Chờ byte giả tối đa 1.5s
                    if (s.ReadByte() >= 0) // dummy byte = server đã sẵn sàng
                    {
                        s.ReadTimeout = Timeout.Infinite; // Bỏ timeout đọc cho thread Drain
                        tcp = c; ns = s; alive = true; // Lưu kết nối và đánh dấu sống
                        new Thread(Drain) { IsBackground = true }.Start(); // Thread nền đọc bỏ dữ liệu server gửi về
                        Log.I("✔ Input: " + Name); // Ghi log thành công
                        return true; // Thành công
                    }
                }
                catch (IOException) { } // Hết thời gian đọc / lỗi luồng -> thử lại
                catch (SocketException) { } // Chưa kết nối được -> thử lại
                if (c != null) c.Close(); // Đóng kết nối thử thất bại
            }
            Dispose(); // Hết lượt thử -> dọn dẹp
            return false; // Thất bại
        }

        // Đọc và bỏ mọi dữ liệu server gửi về (vd clipboard) để socket không bị đầy; phát hiện mất kết nối
        void Drain()
        {
            var buf = new byte[4096]; // Bộ đệm đọc
            try { while (ns.Read(buf, 0, buf.Length) > 0) { } } catch { } // Đọc đến khi luồng đóng (trả 0) hoặc lỗi
            if (alive) Log.I("⚠ Mất kết nối scrcpy-server"); // Mất kết nối ngoài ý muốn -> cảnh báo
            alive = false; // Đánh dấu đã chết
        }

        // Ghi giá trị v dạng big-endian dài len byte vào mảng b bắt đầu từ vị trí off
        static void Put(byte[] b, int off, long v, int len)
        {
            for (int i = len - 1; i >= 0; i--) { b[off + i] = (byte)(v & 0xFF); v >>= 8; } // Ghi byte thấp nhất vào cuối, dịch phải 8 bit mỗi vòng
        }

        // Gửi một sự kiện chạm dạng gói INJECT_TOUCH_EVENT 32 byte qua socket điều khiển
        public void Send(int action, long id, double nx, double ny)
        {
            if (!alive) return; // Kết nối chết -> bỏ qua
            int w = scr.W & ~7, h = scr.H & ~7; // scrcpy làm tròn kích thước về bội số 8
            if (w <= 0 || h <= 0) return; // Chưa biết kích thước -> bỏ qua
            int x = Math.Max(0, Math.Min(w - 1, (int)Math.Round(nx * w))); // Đổi X chuẩn hoá sang pixel, kẹp trong [0, w-1]
            int y = Math.Max(0, Math.Min(h - 1, (int)Math.Round(ny * h))); // Đổi Y chuẩn hoá sang pixel, kẹp trong [0, h-1]

            var m = new byte[32]; // Gói tin 32 byte
            m[0] = 2;                       // TYPE_INJECT_TOUCH_EVENT
            m[1] = (byte)action; // Byte 1: action (0 down, 1 up, 2 move)
            Put(m, 2, id, 8); // Byte 2-9: pointer id (8 byte)
            Put(m, 10, x, 4); // Byte 10-13: toạ độ X (4 byte)
            Put(m, 14, y, 4); // Byte 14-17: toạ độ Y (4 byte)
            Put(m, 18, w, 2); // Byte 18-19: chiều rộng màn hình (2 byte)
            Put(m, 20, h, 2); // Byte 20-21: chiều cao màn hình (2 byte)
            Put(m, 22, action == 1 ? 0 : 0xFFFF, 2); // pressure
            Put(m, 24, 0, 4);               // actionButton
            Put(m, 28, 0, 4);               // buttons
            lock (lk) // Khoá để các gói không bị xen kẽ khi nhiều thread cùng gửi
            {
                try { ns.Write(m, 0, m.Length); } // Ghi gói tin vào socket
                catch { alive = false; } // Ghi lỗi -> đánh dấu mất kết nối
            }
        }

        // Đóng kết nối, dừng server và gỡ forward cổng
        public void Dispose()
        {
            alive = false; // Đánh dấu không còn sống
            try { if (tcp != null) tcp.Close(); } catch { } // Đóng kết nối TCP
            try { if (server != null && !server.HasExited) server.Kill(); } catch { } // Diệt tiến trình server nếu còn chạy
            if (port > 0) adb.Run("forward --remove tcp:" + port, 5000); // Gỡ forward cổng đã tạo
        }
    }

    /// <summary>Dự phòng khi không có scrcpy-server: dùng 'input tap/swipe' qua 1 phiên adb shell (chậm hơn, không đa điểm thật).</summary>
    public class AdbTouch : ITouch
    {
        readonly Screen scr; // Kích thước màn hình hiện tại
        readonly Process sh; // Phiên adb shell giữ mở để gửi lệnh
        readonly Dictionary<long, int[]> g = new Dictionary<long, int[]>(); // Pointer id -> [x đầu, y đầu, x cuối, y cuối]
        readonly Dictionary<long, DateTime> t0 = new Dictionary<long, DateTime>(); // Pointer id -> thời điểm chạm xuống

        // Mở một phiên adb shell tương tác (stdin được chuyển hướng)
        public AdbTouch(Adb adb, Screen scr)
        {
            this.scr = scr; // Lưu đối tượng màn hình
            sh = Proc.Start(adb.Exe, adb.Target + " shell", null, null, true); // Khởi chạy "adb -s <serial> shell"
            Log.I("Input: adb shell input (dự phòng, chậm)"); // Ghi log phương thức đang dùng
        }

        public bool Alive { get { return !sh.HasExited; } } // Còn sống khi phiên shell chưa thoát
        public string Name { get { return "adb input (dự phòng)"; } } // Tên hiển thị

        // Gom down/move/up của một ngón rồi khi nhả mới gửi một lệnh input tap hoặc swipe
        public void Send(int action, long id, double nx, double ny)
        {
            int x = (int)(nx * scr.W), y = (int)(ny * scr.H); // Đổi toạ độ chuẩn hoá sang pixel
            lock (g) // Khoá vì có thể gọi từ nhiều thread
            {
                if (action == 0) { g[id] = new[] { x, y, x, y }; t0[id] = DateTime.Now; return; } // Down: lưu điểm đầu và thời điểm bắt đầu
                int[] s; // Dữ liệu ngón đang theo dõi
                if (!g.TryGetValue(id, out s)) return; // Không có down tương ứng -> bỏ qua
                s[2] = x; s[3] = y; // Cập nhật điểm cuối
                if (action != 1) return; // Move: chỉ cập nhật, chưa gửi
                g.Remove(id); // Up: xoá ngón khỏi bảng
                int dur = (int)(DateTime.Now - t0[id]).TotalMilliseconds; // Thời gian giữ (ms)
                bool moved = Math.Abs(s[0] - s[2]) + Math.Abs(s[1] - s[3]) > 8; // Di chuyển quá 8 pixel thì coi là vuốt
                // Vuốt hoặc giữ lâu (>350ms) -> input swipe (tối thiểu 50ms); ngược lại -> input tap
                string cmd = moved || dur > 350
                    ? "input swipe " + s[0] + " " + s[1] + " " + s[2] + " " + s[3] + " " + Math.Max(50, dur) // swipe x1 y1 x2 y2 thời_gian
                    : "input tap " + s[0] + " " + s[1]; // tap x y
                try { sh.StandardInput.WriteLine(cmd); sh.StandardInput.Flush(); } catch { } // Gửi lệnh vào stdin của shell
            }
        }

        public void Dispose() { try { sh.Kill(); } catch { } } // Diệt phiên adb shell
    }

    /// <summary>Ngẫu nhiên hoá kiểu người thật: lệch vị trí theo Gauss, thời gian nhấn log-normal, độ trễ tam giác.</summary>
    public static class Human
    {
        static readonly Random R = new Random(); // Bộ sinh số ngẫu nhiên dùng chung
        static readonly object L = new object(); // Khoá vì Random không an toàn đa luồng

        public static double U() { lock (L) return R.NextDouble(); } // Số ngẫu nhiên đều trong [0, 1)

        // Số ngẫu nhiên phân phối chuẩn (trung bình 0, độ lệch 1) theo Box-Muller
        public static double Gauss()
        {
            double u1 = 1.0 - U(), u2 = U(); // u1 trong (0,1] để tránh log(0)
            return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); // Công thức Box-Muller
        }

        /// <summary>Độ lệch trong hình tròn bán kính r, dày ở tâm, thưa ở rìa.</summary>
        public static void Jitter(double r, out double dx, out double dy)
        {
            dx = dy = 0; // Mặc định không lệch
            if (r <= 0) return; // Bán kính 0 -> không lệch
            double ang = U() * 2 * Math.PI; // Góc ngẫu nhiên 0..2π
            double mag = Math.Min(1.0, Math.Abs(Gauss()) * 0.45) * r; // Độ lớn theo Gauss (tập trung gần tâm), tối đa r
            dx = Math.Cos(ang) * mag; // Thành phần lệch X
            dy = Math.Sin(ang) * mag; // Thành phần lệch Y
        }

        // Số nguyên ngẫu nhiên trong [a, b] theo phân phối tam giác (hay rơi vào giữa)
        public static int Between(int a, int b)
        {
            if (b < a) { int t = a; a = b; b = t; } // Đảm bảo a <= b
            return a + (int)Math.Round((b - a) * (U() + U()) / 2); // Trung bình 2 số đều -> phân phối tam giác
        }

        // Thời gian nhấn ngón (ms) theo log-normal quanh 85ms, kẹp trong [45, 180]
        public static int PressMs()
        {
            double v = Math.Exp(Math.Log(85) + Gauss() * 0.25); // Log-normal: trung vị 85ms, độ lệch log 0.25
            return (int)Math.Max(45, Math.Min(180, v)); // Kẹp giá trị trong 45..180ms
        }

        public static bool Chance(double pct) { return U() * 100 < pct; } // Trả true với xác suất pct%
    }

    /// <summary>Các thao tác chạm mức cao, toạ độ chuẩn hoá 0..1, bán kính r tính theo cạnh ngắn màn hình.</summary>
    public class TouchService
    {
        public volatile ITouch Inj; // Bộ bơm cảm ứng đang dùng (scrcpy hoặc adb)
        public readonly Screen Scr = new Screen(); // Thông tin kích thước màn hình

        public bool Ready { get { var i = Inj; return i != null && i.Alive && Scr.W > 0; } } // Sẵn sàng khi có bộ bơm sống và đã biết kích thước

        // Chờ ms mili giây; nếu bị huỷ trong lúc chờ thì ném OperationCanceledException
        public static void Wait(int ms, CancellationToken ct)
        {
            if (ct.WaitHandle.WaitOne(Math.Max(0, ms))) throw new OperationCanceledException(); // WaitOne trả true nghĩa là token đã bị huỷ
        }

        // Gửi sự kiện chạm thô, kẹp toạ độ trong [0, 1]
        public void Raw(int action, long id, double nx, double ny)
        {
            var i = Inj; // Chép tham chiếu ra biến tạm (Inj có thể bị thay giữa chừng)
            if (i != null) i.Send(action, id, Math.Max(0, Math.Min(1, nx)), Math.Max(0, Math.Min(1, ny))); // Có bộ bơm -> gửi với toạ độ đã kẹp
        }

        /// <summary>Đổi bán kính (theo cạnh ngắn) sang đơn vị chuẩn hoá trục X/Y.</summary>
        public void Radius(double r, out double rx, out double ry)
        {
            double w = Math.Max(1, Scr.W), h = Math.Max(1, Scr.H), s = Math.Min(w, h); // Rộng, cao (tối thiểu 1) và cạnh ngắn
            rx = r * s / w; // Bán kính theo trục X chuẩn hoá
            ry = r * s / h; // Bán kính theo trục Y chuẩn hoá
        }

        // Tính toạ độ (x, y) = điểm (nx, ny) cộng độ lệch ngẫu nhiên trong bán kính r
        public void Jittered(double nx, double ny, double r, out double x, out double y)
        {
            double rx, ry, dx, dy; // Hệ số quy đổi trục và độ lệch
            Radius(1, out rx, out ry); // Hệ số đổi 1 đơn vị cạnh ngắn sang trục X/Y
            Human.Jitter(r, out dx, out dy); // Sinh độ lệch ngẫu nhiên
            x = nx + dx * rx; // Toạ độ X sau khi lệch
            y = ny + dy * ry; // Toạ độ Y sau khi lệch
        }

        // Giữ chạm tại (nx, ny) trong ms mili giây, có rung tay nhỏ; luôn nhả ngón kể cả khi bị huỷ
        public void Hold(long id, double nx, double ny, double r, int ms, CancellationToken ct)
        {
            double x, y, rx, ry; // Toạ độ hiện tại và biên độ rung
            Jittered(nx, ny, r, out x, out y); // Điểm chạm có lệch ngẫu nhiên
            Radius(0.003, out rx, out ry); // rung tay rất nhỏ khi giữ
            Raw(0, id, x, y); // Chạm xuống
            try
            {
                int moves = Human.Between(1, 3), slice = Math.Max(10, ms / (moves + 1)); // Số lần rung 1-3, khoảng thời gian giữa mỗi lần
                for (int i = 0; i < moves; i++) // Thực hiện từng lần rung
                {
                    Wait(slice, ct); // Chờ một khoảng
                    x += (Human.U() - 0.5) * rx; // Dịch X một chút
                    y += (Human.U() - 0.5) * ry; // Dịch Y một chút
                    Raw(2, id, x, y); // Gửi sự kiện di chuyển
                }
                Wait(Math.Max(0, ms - slice * moves), ct); // Chờ phần thời gian còn lại
            }
            finally { Raw(1, id, x, y); } // Luôn nhả ngón để không kẹt chạm
        }

        // Chạm nhanh: giữ trong thời gian nhấn ngẫu nhiên kiểu người
        public void Tap(long id, double nx, double ny, double r, CancellationToken ct)
        {
            Hold(id, nx, ny, r, Human.PressMs(), ct); // Giữ với thời gian PressMs (45-180ms)
        }

        /// <summary>Vuốt theo đường cong Bezier, tốc độ nhanh-chậm tự nhiên.</summary>
        public void Swipe(long id, double x1, double y1, double x2, double y2, int ms, CancellationToken ct)
        {
            double sx, sy, ex, ey; // Điểm đầu và điểm cuối thực tế
            Jittered(x1, y1, 0.01, out sx, out sy); // Điểm đầu có lệch nhỏ
            Jittered(x2, y2, 0.01, out ex, out ey); // Điểm cuối có lệch nhỏ
            double len = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy)); // Độ dài đoạn thẳng đầu-cuối
            double bend = (Human.U() - 0.5) * 0.25 * len; // Độ cong ngẫu nhiên tối đa ±12.5% độ dài
            double cx = (sx + ex) / 2 - (ey - sy) / Math.Max(1e-6, len) * bend; // Điểm điều khiển X: trung điểm dịch theo pháp tuyến
            double cy = (sy + ey) / 2 + (ex - sx) / Math.Max(1e-6, len) * bend; // Điểm điều khiển Y: trung điểm dịch theo pháp tuyến

            int steps = Math.Max(6, ms / 12); // Số bước: khoảng 12ms mỗi bước, tối thiểu 6
            double x = sx, y = sy; // Vị trí hiện tại bắt đầu từ điểm đầu
            Raw(0, id, x, y); // Chạm xuống tại điểm đầu
            try
            {
                for (int i = 1; i <= steps; i++) // Đi qua từng bước của đường cong
                {
                    double t = (double)i / steps; // Tham số tiến độ 0..1
                    t = t * t * (3 - 2 * t); // ease in-out
                    double a = 1 - t; // Hệ số phần còn lại
                    x = a * a * sx + 2 * a * t * cx + t * t * ex; // Bezier bậc 2 cho X
                    y = a * a * sy + 2 * a * t * cy + t * t * ey; // Bezier bậc 2 cho Y
                    Wait(Math.Max(1, ms / steps + Human.Between(-3, 3)), ct); // Chờ thời lượng mỗi bước, lệch ±3ms
                    Raw(2, id, x, y); // Gửi sự kiện di chuyển
                }
                Wait(Human.Between(10, 40), ct); // Dừng ngón ở cuối 10-40ms trước khi nhấc
            }
            finally { Raw(1, id, x, y); } // Luôn nhả ngón
        }
    }
}
