using System; // DateTime, Math, StringSplitOptions
using System.Globalization; // CultureInfo để đọc số thập phân dấu chấm

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>
    /// Đọc tốc độ mạng, ping và FPS của Android bằng MỘT lệnh adb shell mỗi lần:
    ///  - /proc/net/dev (wlan0): số byte nhận/gửi -> tốc độ = chênh lệch / thời gian
    ///  - SurfaceFlinger timestats: số khung hình đã hiển thị từ lần xoá trước -> FPS (màn hình đứng yên thì gần 0)
    ///  - ping 1 gói tới PingHost: độ trễ ra Internet
    /// </summary>
    public class StatsPoller
    {
        // Lệnh shell gộp; {0} = địa chỉ ping
        const string Cmd = "\"grep wlan0: /proc/net/dev; " +
                           "dumpsys SurfaceFlinger --timestats -enable >/dev/null; " +
                           "dumpsys SurfaceFlinger --timestats -dump -maxlayers 0 | grep -m1 totalFrames; " +
                           "dumpsys SurfaceFlinger --timestats -clear >/dev/null; " +
                           "ping -c1 -W1 {0} | grep -o 'time=[0-9.]*'\"";

        long lastRx = -1, lastTx; // Số byte nhận/gửi ở lần đọc trước (-1 = chưa có)
        DateTime lastAt; // Thời điểm lần đọc trước

        public double DownKBs, UpKBs; // Tốc độ tải xuống / tải lên (KB/s)
        public double Fps = -1; // Khung hình/giây (-1 = chưa đo được)
        public double PingMs = -1; // Độ trễ ping (-1 = không có phản hồi)
        public bool Valid; // Lần đọc gần nhất có lấy được số liệu mạng không

        // Xoá số liệu cũ (gọi khi emulator khởi động lại)
        public void Reset() { lastRx = -1; Valid = false; Fps = -1; PingMs = -1; DownKBs = UpKBs = 0; }

        /// <summary>Đọc một lần; chạy ở luồng nền (mất tới ~1-2 giây vì có ping).</summary>
        public void Poll(Adb adb, string pingHost)
        {
            string o = adb.Shell(string.Format(Cmd, pingHost), 6000); // Chạy lệnh gộp, timeout 6 giây
            var now = DateTime.Now; // Thời điểm đọc
            double sec = lastRx < 0 ? 0 : (now - lastAt).TotalSeconds; // Số giây kể từ lần đọc trước
            long rx = -1, tx = -1; // Byte nhận/gửi đọc được
            int frames = -1; // Số khung hình
            double ping = -1; // Ping đọc được
            foreach (var raw in o.Split('\n')) // Duyệt từng dòng kết quả
            {
                string l = raw.Trim(); // Bỏ khoảng trắng đầu/cuối
                if (l.StartsWith("wlan0:")) // Dòng thống kê card mạng Wi-Fi ảo
                {
                    // Sau "wlan0:" lần lượt: byte nhận, gói, lỗi, drop, fifo, frame, compressed, multicast, byte gửi...
                    var f = l.Substring(6).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 9) { long.TryParse(f[0], out rx); long.TryParse(f[8], out tx); } // Cột 0 = nhận, cột 8 = gửi
                }
                else if (l.StartsWith("totalFrames")) // "totalFrames = 88"
                {
                    int i = l.IndexOf('='); // Vị trí dấu '='
                    if (i > 0) int.TryParse(l.Substring(i + 1).Trim(), out frames); // Lấy số sau dấu '='
                }
                else if (l.StartsWith("time=")) // "time=37.0"
                {
                    double v; // Giá trị tạm
                    if (double.TryParse(l.Substring(5), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) ping = v; // Đọc số ms
                }
            }
            if (rx >= 0 && lastRx >= 0 && sec > 0.2) // Có 2 lần đọc liên tiếp thì tính tốc độ
            {
                DownKBs = Math.Max(0, (rx - lastRx) / 1024.0 / sec); // KB/s tải xuống
                UpKBs = Math.Max(0, (tx - lastTx) / 1024.0 / sec); // KB/s tải lên
            }
            Fps = frames >= 0 && sec > 0.2 ? frames / sec : -1; // Khung hình từ lần xoá trước / số giây
            PingMs = ping; // Lưu ping
            if (rx >= 0) { lastRx = rx; lastTx = tx; } // Lưu mốc cho lần sau
            lastAt = now; // Lưu thời điểm
            Valid = rx >= 0; // Có số liệu mạng thì coi là hợp lệ
        }

        // Định dạng tốc độ: dưới 1024 KB/s hiện KB/s, trên thì MB/s
        public static string Speed(double kbs)
        {
            return kbs >= 1024 ? (kbs / 1024).ToString("0.0") + " MB/s" : kbs.ToString("0") + " KB/s";
        }
    }
}
