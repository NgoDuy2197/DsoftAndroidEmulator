using System; // Kiểu cơ bản: Exception, Action, DateTime, StringComparison...
using System.Collections.Generic; // Dictionary dùng cho biến môi trường
using System.Diagnostics; // Process, ProcessPriorityClass để chạy/quản lý tiến trình
using System.IO; // Path, File, Directory, DriveInfo để thao tác file/thư mục
using System.Linq; // Any
using System.Text; // StringBuilder để ghép chuỗi tham số và config.ini
using System.Threading; // Thread, Thread.Sleep cho luồng theo dõi boot

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    // Lớp bọc adb.exe: chạy lệnh adb nhắm vào một thiết bị (serial) cụ thể
    public class Adb
    {
        public readonly string Exe; // Đường dẫn tới adb.exe
        public readonly string Serial; // Serial thiết bị, ví dụ "emulator-5700"
        public readonly int ServerPort; // Cổng adb server RIÊNG của app (mặc định 5038), tách khỏi server 5037 của scrcpy/tool khác

        // Hàm khởi tạo: lưu đường dẫn adb, serial thiết bị và cổng adb server
        public Adb(string exe, string serial, int serverPort) { Exe = exe; Serial = serial; ServerPort = serverPort; }

        // Phần đầu mọi lệnh: chọn server riêng + thiết bị, ví dụ "-P 5038 -s emulator-5700"
        public string Target { get { return "-P " + ServerPort + " -s " + Serial; } }

        // Chạy "adb -P <port> -s <serial> <args>" và trả về output dạng chuỗi, có timeout
        public string Run(string args, int timeoutMs) { return Proc.Run(Exe, Target + " " + args, timeoutMs, null); }
        // Chạy một lệnh shell trên Android qua "adb shell <cmd>"
        public string Shell(string cmd, int timeoutMs) { return Run("shell " + cmd, timeoutMs); }
        // Chụp màn hình thiết bị (PNG thô) qua "adb exec-out screencap -p", timeout 15 giây
        public byte[] Screencap() { return Proc.RunBytes(Exe, Target + " exec-out screencap -p", 15000); }

        // Kiểm tra thiết bị có đang online trong "adb devices" (của server riêng) không
        public bool Online()
        {
            // Tìm dòng "<serial>\tdevice" (trạng thái device = online, không phải offline)
            return Proc.Run(Exe, "-P " + ServerPort + " devices", 8000, null).Contains(Serial + "\tdevice");
        }

        // Trạng thái thiết bị trong "adb devices": "device" (online), "offline", "unauthorized"... hoặc "" nếu không thấy
        public string State()
        {
            foreach (var l in Proc.Run(Exe, "-P " + ServerPort + " devices", 8000, null).Split('\n'))
            {
                var p = l.Trim().Split('\t');
                if (p.Length >= 2 && p[0] == Serial) return p[1].Trim();
            }
            return "";
        }

        // Ép adb server kết nối lại các thiết bị đang kẹt "offline" (hay gặp khi cổng adb nằm ngoài dải 5555-5585)
        public void ReconnectOffline() { Proc.Run(Exe, "-P " + ServerPort + " reconnect offline", 10000, null); }

        // Kiểm tra Android đã boot xong chưa
        public bool Booted()
        {
            // Phải online và thuộc tính sys.boot_completed trả về "1"
            return Online() && Shell("getprop sys.boot_completed", 5000).Trim() == "1";
        }
    }

    /// <summary>Tạo AVD, chạy/dừng emulator, watchdog tự khởi động lại khi crash.</summary>
    public class EmulatorController
    {
        readonly Settings S; // Đối tượng cấu hình (đọc/ghi settings của app)
        readonly string baseDir; // Thư mục gốc của ứng dụng, dùng để tính đường dẫn tương đối
        Process proc; // Tiến trình emulator.exe hiện tại
        volatile bool stopping; // Cờ báo người dùng chủ động dừng (để không tự khởi động lại)
        int autoRestarts; // Số lần đã tự khởi động lại liên tiếp sau crash

        public event Action Booted; // Sự kiện phát ra khi Android boot xong
        public event Action Stopped; // Sự kiện phát ra khi emulator đã dừng hẳn
        public event Action<int, bool> Exited; // Emulator thoát: (mã thoát, true = không phải do app chủ động dừng)

        // Hàm khởi tạo: nhận cấu hình, thư mục gốc và số thứ tự máy (0 = Máy 1)
        public EmulatorController(Settings s, string baseDir) : this(s, baseDir, 0) { }
        public EmulatorController(Settings s, string baseDir, int index) { S = s; this.baseDir = baseDir; Index = index; }

        // ---------- Nhiều máy trên 1 PC ----------
        // Máy thứ N (Index = N-1) dùng: cổng 5700 + 2*(N-1), AVD "<tên>_mN", khoá settings "<khoá>_N", log emulator-N.log.
        // Máy 1 giữ nguyên tên/khoá cũ để không mất dữ liệu đã có.
        public readonly int Index; // 0 = Máy 1
        public static volatile int Count = 1; // Tổng số máy (để gắn nhãn [Máy N] vào log khi có nhiều máy)
        public string Label { get { return "Máy " + (Index + 1); } }
        string K(string key) { return Index == 0 ? key : key + "_" + (Index + 1); } // Khoá settings riêng của máy này
        void LogI(string msg) { Log.I(Count > 1 ? "[" + Label + "] " + msg : msg); }

        // Trạng thái giao diện riêng từng máy (do MainForm đọc/ghi)
        public volatile int UiOp; // 0 = rảnh, 1 = đang bật, 2 = đang dừng
        public volatile bool Busy; // Đang làm thao tác nhanh (lưu / làm mới mạng / khởi động lại)
        public volatile bool Ready; // Android đã sẵn sàng (theo app)

        // Chuyển đường dẫn tương đối thành tuyệt đối (ghép với baseDir), đường dẫn tuyệt đối giữ nguyên
        string Abs(string p) { return Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p); }

        // Thư mục SDK (mặc định "sdk" cạnh file exe)
        public string SdkDir { get { return Abs(S.Get("SdkDir", "sdk")); } }
        // Thư mục chứa các máy ảo AVD (mặc định "avd" cạnh file exe)
        public string AvdHome { get { return Abs(S.Get("AvdHome", "avd")); } }

        /// <summary>
        /// Chuyển toàn bộ SDK/image + máy ảo sang thư mục gốc mới (newRoot\sdk, newRoot\avd).
        /// Cùng ổ thì đổi tên (tức thì), khác ổ thì copy rồi xoá bản cũ. move=false: chỉ đổi đường dẫn.
        /// </summary>
        public void MoveData(string newRoot, bool move)
        {
            if (Running) throw new Exception("Dừng giả lập trước khi đổi nơi lưu."); // Không cho chuyển khi emulator đang chạy
            string newSdk = Path.Combine(newRoot, "sdk"), newAvd = Path.Combine(newRoot, "avd"); // Đường dẫn SDK/AVD mới
            string oldSdk = SdkDir, oldAvd = AvdHome; // Ghi nhớ đường dẫn cũ
            // Nếu thư mục mới trùng thư mục đang dùng (không phân biệt hoa thường) thì báo lỗi
            if (string.Equals(Path.GetFullPath(oldSdk), Path.GetFullPath(newSdk), StringComparison.OrdinalIgnoreCase))
                throw new Exception("Đây đã là thư mục đang dùng."); // Báo đã là thư mục hiện tại
            if (move) // Có di chuyển dữ liệu thật (không chỉ đổi đường dẫn)
            {
                long need = FileUtil.DirSize(oldSdk) + FileUtil.DirSize(oldAvd); // Tổng dung lượng cần chuyển (byte)
                if (!FileUtil.SameDrive(oldSdk, newRoot)) // Khác ổ đĩa thì phải copy nên cần kiểm tra chỗ trống
                {
                    // Lấy dung lượng trống của ổ đích
                    long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(newRoot))).AvailableFreeSpace;
                    if (free < need + 512L * 1024 * 1024) // Cần dư thêm 512 MB dự phòng
                        // Báo lỗi không đủ chỗ, hiển thị số GB cần và còn trống
                        throw new Exception("Ổ đích không đủ chỗ: cần " + need / 1073741824.0 + " GB, còn " + free / 1073741824.0 + " GB");
                }
                LogI("Chuyển " + (need / 1073741824.0).ToString("0.0") + " GB sang " + newRoot + " ..."); // Ghi log bắt đầu chuyển
                FileUtil.MoveDir(oldSdk, newSdk); // Chuyển thư mục SDK
                FileUtil.MoveDir(oldAvd, newAvd); // Chuyển thư mục AVD
                // file <tên>.ini chứa đường dẫn tuyệt đối tới thư mục máy ảo -> sửa lại
                if (Directory.Exists(newAvd)) // Chỉ sửa khi thư mục AVD mới tồn tại
                    foreach (var ini in Directory.GetFiles(newAvd, "*.ini")) // Duyệt từng file <tên>.ini
                    {
                        string n = Path.GetFileNameWithoutExtension(ini); // Tên AVD (bỏ đuôi .ini)
                        var lines = File.ReadAllLines(ini); // Đọc toàn bộ các dòng
                        for (int i = 0; i < lines.Length; i++) // Duyệt từng dòng
                            // Thay dòng "path=" bằng đường dẫn mới tới thư mục <tên>.avd
                            if (lines[i].StartsWith("path=")) lines[i] = "path=" + Path.Combine(newAvd, n + ".avd");
                        File.WriteAllLines(ini, lines); // Ghi lại file .ini
                    }
            }
            S.Set("SdkDir", newSdk); // Lưu đường dẫn SDK mới vào cấu hình
            S.Set("AvdHome", newAvd); // Lưu đường dẫn AVD mới vào cấu hình
            S.Save(); // Ghi cấu hình xuống đĩa
            LogI("✔ Nơi lưu mới: " + newRoot); // Ghi log hoàn tất
        }
        // Mỗi bản Android một AVD riêng: dùng chung userdata giữa các bản sẽ không boot được.
        // Tên AVD đang dùng: lấy từ cấu hình, nếu không có thì sinh theo API + loại image
        public string AvdName { get { string b = S.Get("AvdName", AvdNameFor(Api, Tag)); return Index == 0 ? b : b + "_m" + (Index + 1); } }
        string AvdDir { get { return Path.Combine(AvdHome, AvdName + ".avd"); } }

        /// <summary>Nhân bản dữ liệu máy src (app, tài khoản) sang máy này. Cả 2 phải đang tắt.
        /// File qcow2 trỏ file nền bằng tên tương đối nên copy cả thư mục là đủ; bỏ snapshot (máy mới boot nguội lần đầu).</summary>
        public void CloneFrom(EmulatorController src)
        {
            if (Running || src.Running) throw new Exception("Tắt " + src.Label + " và " + Label + " trước khi nhân bản.");
            string from = src.AvdDir, to = AvdDir;
            if (!File.Exists(Path.Combine(from, "config.ini"))) throw new Exception(src.Label + " chưa có dữ liệu (chưa chạy lần nào).");
            string[] skipDirs = { "snapshots", "tmpAdbCmds" };
            string[] skipFiles = { ".lock", "read-snapshot.txt", "quickbootChoice.ini", "emu-launch-params.txt", "hardware-qemu.ini" };
            long need = FileUtil.DirSize(from) - (Directory.Exists(Path.Combine(from, "snapshots")) ? FileUtil.DirSize(Path.Combine(from, "snapshots")) : 0);
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AvdHome))).AvailableFreeSpace;
            if (free < need + 1073741824L) throw new Exception("Không đủ chỗ: cần " + (need / 1073741824.0).ToString("0.0") + " GB, còn " + (free / 1073741824.0).ToString("0.0") + " GB.");
            if (Directory.Exists(to)) FileUtil.DeleteDir(to);
            LogI("Nhân bản " + src.Label + " → " + Label + " (" + (need / 1073741824.0).ToString("0.0") + " GB)...");
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(from.Length).TrimStart('\\');
                string top = rel.Split('\\')[0];
                if (Array.IndexOf(skipDirs, top) >= 0 || skipFiles.Any(x => rel.EndsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                string dst = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
            }
            CreateAvd(); // Ghi lại config.ini + <tên>.ini đúng tên/đường dẫn của máy này
            AdbKilled = false; // Boot nguội -> ADB bật lại (app tự tắt nếu ADB của máy này đang để TẮT)
            LogI("✔ Đã nhân bản xong. Lần chạy đầu sẽ khởi động nguội.");
        }

        /// <summary>Xoá dữ liệu máy ảo của máy này (bản Android đang chọn).</summary>
        public void DeleteOwnAvd()
        {
            if (Running) throw new Exception("Tắt " + Label + " trước.");
            FileUtil.DeleteDir(AvdDir);
            FileUtil.DeleteFile(Path.Combine(AvdHome, AvdName + ".ini"));
            LogI("Đã xoá dữ liệu máy ảo " + AvdName);
        }

        // Sinh tên AVD theo phiên bản API và tag image
        public static string AvdNameFor(int api, string tag)
        {
            bool play = tag.Contains("playstore"); // true nếu là image có Google Play
            if (api == 30 && play) return "Dsoft"; // tên của bản đầu tiên, giữ để không mất dữ liệu cũ
            return "Dsoft_api" + api + (play ? "_play" : "_gapi"); // Ví dụ: Dsoft_api33_play / Dsoft_api33_gapi
        }

        // Kiểm tra thư mục <tên>.avd của bản Android này đã tồn tại chưa
        public bool AvdExistsFor(int api, string tag) { return Directory.Exists(Path.Combine(AvdHome, AvdNameFor(api, tag) + ".avd")); }

        // Xoá hẳn máy ảo (thư mục .avd và file .ini) của một bản Android
        public void DeleteAvd(int api, string tag)
        {
            string n = AvdNameFor(api, tag); // Tên AVD cần xoá
            string dir = Path.Combine(AvdHome, n + ".avd"), ini = Path.Combine(AvdHome, n + ".ini"); // Thư mục dữ liệu và file mô tả
            FileUtil.DeleteDir(dir); // Xoá thư mục máy ảo
            FileUtil.DeleteFile(ini); // Xoá file .ini trỏ tới máy ảo
            LogI("Đã xoá máy ảo " + n + " (toàn bộ app/dữ liệu)"); // Ghi log
        }

        const string WipeMarker = ".dsoft_wipe"; // Tên file đánh dấu "lần chạy tới phải xoá trắng dữ liệu"

        // Kiểm tra máy ảo này có đang chờ xoá trắng (có file đánh dấu) không
        public bool WipePendingFor(int api, string tag) { return File.Exists(Path.Combine(AvdHome, AvdNameFor(api, tag) + ".avd", WipeMarker)); }

        /// <summary>Xoá trắng dữ liệu (app, tài khoản, snapshot) nhưng giữ máy ảo + cấu hình. Lần chạy tới sẽ boot như máy mới.</summary>
        public void WipeAvd(int api, string tag)
        {
            string dir = Path.Combine(AvdHome, AvdNameFor(api, tag) + ".avd"); // Thư mục máy ảo
            if (!Directory.Exists(dir)) return; // Chưa có máy ảo thì không làm gì
            // Duyệt các mẫu file dữ liệu: userdata, cache, khoá mã hoá, thẻ nhớ, snapshot, cấu hình phần cứng sinh ra, file khoá
            foreach (var pat in new[] { "userdata-qemu.img*", "cache.img*", "encryptionkey.img*", "sdcard.img*", "read-snapshot.txt", "hardware-qemu.ini", "*.lock" })
                foreach (var f in Directory.GetFiles(dir, pat)) FileUtil.DeleteFile(f); // Xoá từng file khớp mẫu
            foreach (var d in new[] { "snapshots", "data", "tmpAdbCmds" }) // Các thư mục con chứa dữ liệu/snapshot
            {
                string p = Path.Combine(dir, d); // Đường dẫn thư mục con
                FileUtil.DeleteDir(p); // Xoá thư mục con
            }
            File.WriteAllText(Path.Combine(dir, WipeMarker), "1"); // lần chạy tới thêm -wipe-data cho chắc
            LogI("Đã xoá trắng dữ liệu " + AvdNameFor(api, tag) + ". Lần chạy tới sẽ như máy mới."); // Ghi log
        }

        public volatile bool IsBooted; // true khi Android đã boot xong
        public DateTime StartedAt; // Thời điểm bắt đầu chạy emulator
        // Cổng console của emulator; cổng adb = Port + 1. Mặc định 5700: NGOÀI dải 5554-5585 mà adb server tự quét,
        // nên adb/scrcpy khác trên máy (server 5037) không thấy emulator này
        public int Port { get { return S.GetInt("Port", 5700) + 2 * Index; } } // Máy N: 5700 + 2*(N-1)
        public string Serial { get { return "emulator-" + Port; } } // Serial adb tương ứng, ví dụ "emulator-5700"
        public int AdbServerPort { get { return S.GetInt("AdbServerPort", 5038); } } // Cổng adb server riêng của app
        public string EmulatorExe { get { return Path.Combine(SdkDir, "emulator", "emulator.exe"); } } // Đường dẫn emulator.exe
        public string AdbExe { get { return Path.Combine(SdkDir, "platform-tools", "adb.exe"); } } // Đường dẫn adb.exe
        public int Api { get { return S.GetInt("Api", 30); } } // Phiên bản API Android (mặc định 30 = Android 11)
        public string Tag { get { return S.Get("ImageTag", "google_apis_playstore"); } } // Loại image (Play Store / Google APIs)
        public string Abi { get { return S.Get("Abi", "x86_64"); } } // Kiến trúc CPU của image
        // Đường dẫn tương đối tới system image trong SDK: system-images\android-<api>\<tag>\<abi>\
        public string SysImgRel { get { return "system-images\\android-" + Api + "\\" + Tag + "\\" + Abi + "\\"; } }
        public bool Running { get { return proc != null && !proc.HasExited; } } // true nếu tiến trình emulator còn sống
        // AVD tồn tại khi có file config.ini trong thư mục <tên>.avd
        public bool AvdExists { get { return File.Exists(Path.Combine(AvdHome, AvdName + ".avd", "config.ini")); } }
        public Adb NewAdb() { return new Adb(AdbExe, Serial, AdbServerPort); } // Tạo đối tượng Adb nhắm vào emulator này

        // Biến môi trường truyền cho emulator.exe để nó tìm thấy SDK và AVD
        public Dictionary<string, string> Env()
        {
            return new Dictionary<string, string> // Tạo bảng biến môi trường
            {
                { "ANDROID_SDK_ROOT", SdkDir }, // Thư mục SDK (tên biến kiểu cũ)
                { "ANDROID_HOME", SdkDir }, // Thư mục SDK (tên biến kiểu mới)
                { "ANDROID_AVD_HOME", AvdHome }, // Thư mục chứa các AVD
                { "ANDROID_ADB_SERVER_PORT", AdbServerPort.ToString() } // Emulator báo danh / gọi adb qua server riêng, không đụng server 5037
            };
        }

        // ---------- Hồ sơ hiển thị: play = Mặc định (Width/Height/Dpi), afk = Treo máy (nhẹ), custom = Tuỳ chỉnh ----------
        public static readonly string[] DisplayModes = { "play", "afk", "custom" };
        public static string DisplayName(string mode) { return mode == "afk" ? "Treo máy" : mode == "custom" ? "Tuỳ chỉnh" : "Mặc định"; }
        // Hồ sơ của máy này (riêng từng máy)
        public string Display
        {
            get { string d = S.Get(K("Display"), "play"); return Array.IndexOf(DisplayModes, d) >= 0 ? d : "play"; }
            set { S.Set(K("Display"), value); S.Save(); }
        }
        // Kích thước + DPI của một hồ sơ (giá trị dùng chung mọi máy)
        public void ScreenOf(string mode, out int w, out int h, out int dpi)
        {
            int pw = S.GetInt("Width", 1280), ph = S.GetInt("Height", 720);
            if (mode == "afk")
            {
                // Mặc định 960x540 (cùng hướng ngang/dọc với hồ sơ Mặc định)
                w = S.GetInt("AfkWidth", pw >= ph ? 960 : 540); h = S.GetInt("AfkHeight", pw >= ph ? 540 : 960);
                dpi = S.GetInt("AfkDpi", 160);
            }
            else if (mode == "custom") { w = S.GetInt("CustomWidth", pw); h = S.GetInt("CustomHeight", ph); dpi = S.GetInt("CustomDpi", S.GetInt("Dpi", 240)); }
            else { w = pw; h = ph; dpi = S.GetInt("Dpi", 240); }
        }

        // Ghi file config.ini và <tên>.ini cho AVD theo cấu hình hiện tại
        public void CreateAvd()
        {
            string sys = Path.Combine(SdkDir, SysImgRel); // Đường dẫn tuyệt đối tới system image
            if (!Directory.Exists(sys)) throw new Exception("Chưa có system image: " + sys); // Báo lỗi nếu chưa tải image

            string avdDir = Path.Combine(AvdHome, AvdName + ".avd"); // Thư mục máy ảo
            Directory.CreateDirectory(avdDir); // Tạo thư mục nếu chưa có
            int w, h, dpi; ScreenOf(Display, out w, out h, out dpi); // Độ phân giải theo hồ sơ hiển thị của máy này
            bool play = Tag.Contains("playstore"); // Có Google Play hay không

            var c = new StringBuilder(); // Bộ đệm nội dung config.ini
            // Hàm phụ ghi một dòng "khoá=giá trị" kết thúc bằng CRLF
            Action<string, object> kv = (k, v) => c.Append(k).Append('=').Append(v).Append("\r\n");
            kv("AvdId", AvdName); // Mã định danh AVD
            kv("avd.ini.displayname", AvdName); // Tên hiển thị
            kv("avd.ini.encoding", "UTF-8"); // Bảng mã của file
            kv("abi.type", Abi); // Kiến trúc của image
            kv("hw.cpu.arch", Abi == "x86_64" ? "x86_64" : "x86"); // Kiến trúc CPU ảo
            kv("hw.cpu.ncore", S.GetInt("Cores", 4)); // Số nhân CPU
            kv("hw.ramSize", S.GetInt("RamMB", 4096)); // Dung lượng RAM (MB)
            kv("vm.heapSize", 512); // Heap tối đa của máy ảo Java (MB)
            kv("hw.lcd.width", w); // Chiều rộng màn hình
            kv("hw.lcd.height", h); // Chiều cao màn hình
            kv("hw.lcd.density", dpi); // Mật độ điểm ảnh (DPI)
            kv("hw.initialOrientation", w > h ? "landscape" : "portrait"); // Hướng màn hình: ngang nếu rộng > cao
            kv("hw.gpu.enabled", "yes"); // Bật tăng tốc GPU
            kv("hw.gpu.mode", GpuMode); // Chế độ GPU (host = dùng card đồ hoạ thật)
            kv("hw.keyboard", "yes"); // Cho phép gõ bằng bàn phím máy tính
            kv("hw.mainKeys", "no"); // Không có phím cứng -> hiện thanh điều hướng
            kv("hw.audioInput", "no"); // Tắt micro
            kv("hw.camera.back", "none"); // Không có camera sau
            kv("hw.camera.front", "none"); // Không có camera trước
            kv("hw.gps", "no"); // Tắt GPS
            kv("hw.sdCard", "no"); // Không dùng thẻ nhớ
            kv("disk.dataPartition.size", S.Get("DataSize", "8G")); // Dung lượng phân vùng dữ liệu
            kv("image.sysdir.1", SysImgRel); // Đường dẫn system image (tương đối với SDK)
            kv("tag.id", Tag); // Mã loại image
            kv("tag.display", play ? "Google Play" : "Google APIs"); // Tên hiển thị loại image
            kv("PlayStore.enabled", play ? "true" : "false"); // Bật/tắt Play Store
            kv("showDeviceFrame", "no"); // Không vẽ khung điện thoại quanh màn hình
            kv("skin.dynamic", "yes"); // Skin tự co theo độ phân giải
            kv("fastboot.forceColdBoot", "no"); // Không ép cold boot (cho phép quick boot từ snapshot)
            kv("target", "android-" + Api); // Nền tảng đích
            File.WriteAllText(Path.Combine(avdDir, "config.ini"), c.ToString()); // Ghi config.ini

            // Ghi file <tên>.ini: bảng mã, đường dẫn tuyệt đối/tương đối tới thư mục AVD và nền tảng đích
            File.WriteAllText(Path.Combine(AvdHome, AvdName + ".ini"),
                "avd.ini.encoding=UTF-8\r\npath=" + avdDir + "\r\npath.rel=avd\\" + AvdName + ".avd\r\ntarget=android-" + Api + "\r\n");
            // Ghi log tóm tắt cấu hình vừa ghi
            LogI("✔ Đã ghi AVD '" + AvdName + "' (" + DisplayName(Display) + " " + w + "x" + h + " DPI " + dpi + ", " + S.GetInt("Cores", 4) + " nhân, " + S.GetInt("RamMB", 4096) + " MB)");
        }

        // Kiểm tra máy có hỗ trợ tăng tốc phần cứng (WHPX/AEHD...) hay không
        public string AccelCheck()
        {
            return Proc.Run(EmulatorExe, "-accel-check", 30000, Env()); // Chạy "emulator -accel-check" và trả về kết quả
        }

        // Các chế độ GPU mà emulator 37.x chấp nhận (emulator -help-gpu)
        public static readonly string[] GpuModes = { "host", "auto", "swangle", "swiftshader", "software" };

        // Chế độ GPU hợp lệ: giá trị cũ không còn hỗ trợ (angle_indirect, swiftshader_indirect...) -> auto, giống emulator tự xử lý
        public string GpuMode
        {
            get
            {
                string g = S.Get("Gpu", "host").Trim(); // Giá trị trong settings.ini
                return Array.IndexOf(GpuModes, g) >= 0 ? g : "auto"; // Không nằm trong danh sách -> auto
            }
        }

        // Tên snapshot của nút "Lưu trạng thái" (tách riêng khỏi default_boot của Quick Boot)
        public const string SaveName = "dsoft_save";
        // Đã có bản lưu cho máy ảo đang chọn chưa (thư mục avd\<tên>.avd\snapshots\dsoft_save)
        public bool HasSave { get { return Directory.Exists(Path.Combine(AvdHome, AvdName + ".avd", "snapshots", SaveName)); } }

        // Gửi lệnh tới cổng console của emulator qua "adb emu <lệnh>"
        public string EmuConsole(string cmd, int timeoutMs) { return Proc.Run(AdbExe, NewAdb().Target + " emu " + cmd, timeoutMs, null); }

        /// <summary>Lưu trạng thái đang chạy vào snapshot dsoft_save (Android đứng hình ~10-20s).
        /// KHÔNG ghi đè default_boot: thử trên máy này thì emulator crash và hỏng điểm Quick Boot.</summary>
        public void SaveState()
        {
            if (!Running) throw new Exception("Giả lập chưa chạy."); // Phải đang chạy mới lưu được
            string r = EmuConsole("avd snapshot save " + SaveName, 180000); // Lưu snapshot, chờ tối đa 3 phút
            if (!r.Contains("OK")) throw new Exception("Lưu thất bại: " + r.Trim()); // Console trả "OK" khi thành công
            S.Set(K("SaveAdbKilled"), AdbKilled); S.Save(); // Nhớ bản lưu được tạo lúc ADB tắt hay bật
        }

        // ---------- ADB trong Android (game có thể phát hiện USB debugging) ----------
        // Người dùng cho phép app dùng ADB trên máy này không (mặc định TẮT)
        public bool AdbOn { get { return S.GetBool(K("AdbOn"), false); } }
        public void SetAdbOn(bool on) { S.Set(K("AdbOn"), on); S.Save(); }

        // ADB trong trạng thái Android hiện tại đã bị tắt chưa. Quick Boot giữ nguyên trạng thái này; cold boot bật lại ADB.
        public bool AdbKilled
        {
            get { return S.GetBool(K("AdbKilled"), false); }
            set { S.Set(K("AdbKilled"), value); S.Save(); }
        }

        /// <summary>Tắt ADB trong Android: đặt cờ "USB debugging" về 0 -> Android dừng adbd ngay.
        /// Cờ adb_enabled phải được đặt CUỐI và phải đổi giá trị (1 rồi 0), nếu không adbd không dừng.
        /// Sau đó chỉ cold boot mới bật lại được ADB.</summary>
        public void KillAdb()
        {
            var adb = NewAdb(); // adb nhắm vào emulator
            // Thử tối đa 3 lần: ngay sau boot (nhất là máy yếu / thiếu RAM) Android có thể chưa xử lý kịp lệnh settings
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                // Bật Developer options trước (nếu đang tắt sẵn - vd boot nguội sau lần tắt trước / máy nhân bản - Android bỏ qua adb_enabled=1
                // nên đổi 1 -> 0 không dừng được adbd), rồi tắt Developer options (chạy nền vì có thể làm adbd dừng sớm) và đổi adb_enabled 1 -> 0
                adb.Shell("\"settings put global development_settings_enabled 1; settings put global adb_enabled 1; sleep 1; " +
                          "settings put global development_settings_enabled 0 >/dev/null 2>&1 & settings put global adb_enabled 0\"", 15000);
                for (int i = 0; i < 20 && adb.Online(); i++) Thread.Sleep(500); // Chờ thiết bị rời khỏi adb (tối đa 10s)
                if (!adb.Online())
                {
                    AdbKilled = true; // Chỉ ghi nhớ khi đã tắt thật
                    LogI("✔ Đã tắt ADB trong Android" + (attempt > 1 ? " (lần thử " + attempt + ")" : "") + ".");
                    return;
                }
                LogI("ADB chưa tắt, thử lại (" + attempt + "/3)...");
                Thread.Sleep(5000);
            }
            AdbKilled = false; // Tắt hụt: ADB vẫn bật -> game có thể phát hiện
            LogI("⚠ KHÔNG tắt được ADB sau 3 lần. Game có thể phát hiện USB debugging - bấm ⟳ Khởi động lại nhanh rồi thử lại.");
        }

        /// <summary>Tắt Wi-Fi ảo rồi bật lại trong Android, chờ có Internet trở lại (tối đa ~60s).</summary>
        public bool RefreshNetwork()
        {
            var adb = NewAdb(); // adb nhắm vào emulator
            adb.Shell("cmd wifi set-wifi-enabled disabled", 15000); // Tắt Wi-Fi ảo
            Thread.Sleep(1500); // Chờ Android ngắt hẳn
            adb.Shell("cmd wifi set-wifi-enabled enabled", 15000); // Bật lại
            string host = S.Get("PingHost", "8.8.8.8"); // Địa chỉ dùng để thử Internet
            for (int i = 0; i < 30; i++) // Thử tối đa 30 lần
            {
                if (adb.Shell("ping -c1 -W1 " + host, 5000).Contains("1 received")) return true; // Có phản hồi -> có mạng
                Thread.Sleep(1000); // Chưa có, chờ rồi thử lại
            }
            return false; // Hết lượt vẫn chưa có mạng
        }

        /// <summary>Tắt emulator (Quick Boot tự lưu khi tắt) rồi bật lại. fromSave=true: bật từ bản lưu dsoft_save.</summary>
        public void Restart(bool fromSave)
        {
            if (fromSave && !HasSave) throw new Exception("Chưa có bản lưu - bấm 'Lưu trạng thái' trước."); // Không có bản lưu thì báo
            if (Running) Stop(); // Đang chạy thì tắt mềm trước (lưu default_boot nếu bật Quick Boot)
            Start(false, fromSave ? SaveName : null); // Bật lại: Quick Boot hoặc từ bản lưu
        }

        // Xoá/đổi tên emulator.log khi quá 4 MB để file không phình mãi qua nhiều phiên
        static void RotateLog(string file)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length < 4 * 1024 * 1024) return; // Còn nhỏ thì giữ
                string old = Path.ChangeExtension(file, ".old.log"); // emulator.old.log
                if (File.Exists(old)) File.Delete(old); // Xoá bản cũ hơn
                File.Move(file, old); // Giữ lại 1 bản để xem khi cần
            }
            catch { } // Lỗi (file đang mở...) thì bỏ qua
        }

        // Giới hạn số dòng lỗi của emulator đẩy lên ô log của app: lỗi GPU có lúc ra hàng trăm dòng/phút
        readonly object errLock = new object(); // Khoá vì stdout và stderr đọc ở 2 luồng
        DateTime errWindow = DateTime.MinValue; // Mốc bắt đầu cửa sổ 60 giây hiện tại
        int errShown, errHidden; // Số dòng đã hiện / đã ẩn trong cửa sổ hiện tại

        // Đẩy dòng lỗi lên ô log, tối đa 10 dòng mỗi 60 giây; phần dư chỉ đếm rồi báo tổng
        void ShowEmuError(string line)
        {
            string summary = null; // Dòng tóm tắt số dòng bị ẩn (nếu có)
            bool show; // Có hiện dòng này không
            lock (errLock)
            {
                if ((DateTime.Now - errWindow).TotalSeconds >= 60) // Sang cửa sổ mới
                {
                    if (errHidden > 0) summary = "[emu] (ẩn " + errHidden + " dòng lỗi lặp lại trong 60s - xem logs\\emulator.log)"; // Báo số dòng đã ẩn
                    errWindow = DateTime.Now; errShown = 0; errHidden = 0; // Đặt lại bộ đếm
                }
                show = errShown < 10; // Còn hạn mức thì hiện
                if (show) errShown++; else errHidden++; // Cập nhật bộ đếm
            }
            if (summary != null) LogI(summary); // Ghi tóm tắt ngoài khoá
            if (show) LogI("[emu] " + line); // Hiện dòng lỗi
        }

        // Khởi động emulator; cold=true thì bỏ qua snapshot (khởi động nguội)
        public void Start(bool cold) { Start(cold, null); }

        // snapshot != null: nạp snapshot có tên đó và không lưu đè khi tắt (giữ nguyên bản lưu)
        public void Start(bool cold, string snapshot)
        {
            if (Running) { LogI("Giả lập đang chạy."); return; } // Đang chạy rồi thì thoát
            if (!File.Exists(EmulatorExe)) throw new Exception("Chưa có emulator.exe, bấm 'Tải SDK + image' trước."); // Chưa tải SDK
            CreateAvd(); // luôn ghi lại config.ini theo cấu hình hiện tại (không đụng dữ liệu)

            Proc.Run(AdbExe, "-P " + AdbServerPort + " start-server", 20000, null); // Khởi động adb server riêng trước

            var a = new StringBuilder(); // Bộ đệm tham số dòng lệnh cho emulator.exe
            a.Append("-avd ").Append(AvdName) // -avd: tên máy ảo cần chạy
             .Append(" -ports ").Append(Port).Append(',').Append(Port + 1) // -ports: cổng console + cổng adb (ngoài dải adb tự quét)
             .Append(" -gpu ").Append(GpuMode) // -gpu: chế độ render đồ hoạ (đã lọc giá trị không hợp lệ)
             .Append(" -cores ").Append(S.GetInt("Cores", 4)) // -cores: số nhân CPU ảo
             .Append(" -memory ").Append(S.GetInt("RamMB", 4096)) // -memory: RAM (MB)
             .Append(" -netdelay none -netspeed full"); // -netdelay none: không giả lập độ trễ mạng; -netspeed full: tốc độ mạng tối đa
            string dns = S.Get("Dns", "").Trim(); // DNS cố định do người dùng chọn ("" = theo máy thật)
            if (dns.Length > 0) a.Append(" -dns-server ").Append(dns); // -dns-server: DNS cho Android
            if (!S.GetBool("BootAnim", true)) a.Append(" -no-boot-anim"); // -no-boot-anim: tắt hoạt ảnh khởi động cho boot nhanh hơn
            string wipe = Path.Combine(AvdHome, AvdName + ".avd", WipeMarker); // Đường dẫn file đánh dấu xoá trắng
            bool wiping = File.Exists(wipe); // Có yêu cầu xoá trắng đang chờ không
            if (wiping) { a.Append(" -wipe-data"); cold = true; snapshot = null; } // -wipe-data: xoá dữ liệu người dùng, buộc cold boot
            // Muốn dùng ADB mà bản Quick Boot đang tắt ADB -> chỉ cold boot mới bật lại được
            if (AdbOn && AdbKilled && snapshot == null && !cold) { cold = true; LogI("ADB đang tắt trong trạng thái đã lưu -> khởi động nguội để bật lại ADB."); }
            if (snapshot != null) AdbKilled = S.GetBool(K("SaveAdbKilled"), false); // Bản lưu mang theo trạng thái ADB lúc lưu
            Interlocked.Exchange(ref snapLoadTicks, 0); snapFailed = false; bootDone = false; // Xoá dấu hiệu nạp snapshot / boot của lần trước
            if (snapshot != null) a.Append(" -snapshot ").Append(snapshot).Append(" -no-snapshot-save"); // Nạp bản lưu, tắt thì không ghi đè nó
            else if (!S.GetBool("QuickBoot", true)) a.Append(" -no-snapshot"); // -no-snapshot: không nạp và không lưu snapshot
            else if (cold) a.Append(" -no-snapshot-load"); // -no-snapshot-load: không nạp snapshot (vẫn lưu khi tắt)
            if (S.GetBool("NoAudio", false)) a.Append(" -no-audio"); // -no-audio: tắt âm thanh
            if (S.GetBool("DisableVulkan", true)) a.Append(" -feature -Vulkan"); // -feature -Vulkan: tắt tính năng Vulkan (tránh lỗi driver)
            string extra = S.Get("ExtraArgs", "").Trim(); // Tham số bổ sung do người dùng nhập
            if (extra.Length > 0) a.Append(' ').Append(extra); // Nối thêm nếu có

            string logFile = Path.Combine(baseDir, "logs", Index == 0 ? "emulator.log" : "emulator-" + (Index + 1) + ".log"); // File log riêng mỗi máy
            RotateLog(logFile); // Quá 4 MB thì đổi tên sang emulator.old.log
            var logLock = new object(); // Khoá đồng bộ khi ghi file log từ nhiều luồng
            StreamWriter logW = null; // Giữ file mở suốt phiên thay vì mở/đóng mỗi dòng
            try { logW = new StreamWriter(logFile, true, new UTF8Encoding(false)) { AutoFlush = true }; } catch { } // Không mở được thì bỏ qua ghi file
            LogI("emulator " + a); // Ghi log dòng lệnh sẽ chạy
            stopping = false; // Xoá cờ dừng chủ động
            // Chạy emulator.exe với tham số và biến môi trường, xử lý từng dòng output
            proc = Proc.Start(EmulatorExe, a.ToString(), Env(), line =>
            {
                lock (logLock) { try { if (logW != null) logW.WriteLine(line); } catch { } } // Ghi dòng vào emulator.log, bỏ qua lỗi
                // Dấu hiệu khôi phục trạng thái: dùng để biết Android sẵn sàng khi ADB tắt (không hỏi được qua adb)
                if (line.Contains("Successfully loaded snapshot")) Interlocked.Exchange(ref snapLoadTicks, DateTime.Now.Ticks);
                else if (line.Contains("Boot completed in")) bootDone = true; // Emulator tự báo Android boot xong (không cần hỏi adb)
                else if (line.Contains("Failed to load snapshot")) snapFailed = true; // Nạp lỗi -> emulator chuyển sang cold boot (ADB bật lại)
                string u = line.ToUpperInvariant(); // Chuyển chữ hoa để so khớp không phân biệt hoa thường
                if (u.Contains("ERROR") || u.Contains("FATAL") || u.Contains("PANIC")) ShowEmuError(line); // Chỉ đẩy dòng lỗi lên log giao diện (có giới hạn)
            }, false);
            proc.Exited += (s, e) => { lock (logLock) { try { if (logW != null) logW.Dispose(); } catch { } logW = null; } }; // Đóng file log khi emulator thoát
            proc.Exited += OnExit; // Đăng ký xử lý khi emulator thoát
            if (wiping) File.Delete(wipe); // Đã thêm -wipe-data thì xoá file đánh dấu
            IsBooted = false; // Chưa boot xong
            StartedAt = DateTime.Now; // Ghi nhận thời điểm bắt đầu

            var p = proc; // Giữ tham chiếu tiến trình hiện tại cho luồng theo dõi
            new Thread(() => BootWatch(p)) { IsBackground = true }.Start(); // Chạy luồng nền theo dõi quá trình boot
        }

        // Nâng ưu tiên CPU cho các tiến trình qemu-system-*; true nếu đã tìm thấy ít nhất 1 tiến trình
        bool RaiseQemuPriority()
        {
            bool found = false;
            foreach (var q in Process.GetProcesses()) // Duyệt mọi tiến trình đang chạy
            {
                try
                {
                    if (!q.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)) continue; // Chỉ lõi qemu của emulator
                    found = true;
                    if (q.PriorityClass == ProcessPriorityClass.AboveNormal) continue; // Đã đặt rồi
                    q.PriorityClass = ProcessPriorityClass.AboveNormal; // Đặt mức ưu tiên CPU trên trung bình
                    LogI("Đặt ưu tiên CPU AboveNormal cho " + q.ProcessName + " (pid " + q.Id + ")");
                }
                catch { } // Bỏ qua tiến trình không có quyền truy cập
            }
            return found;
        }

        // Báo "sẵn sàng" ngay khi có dấu hiệu đầu tiên (để chụp màn hình / gửi Discord dùng được sớm)
        void MarkReady(DateTime t0, string how)
        {
            autoRestarts = 0; // Khởi động thành công -> đặt lại bộ đếm tự khởi động lại
            IsBooted = true;
            LogI("✔ Android đã sẵn sàng sau " + (int)(DateTime.Now - t0).TotalSeconds + "s (" + how + ")");
            var h = Booted; // Sao chép delegate để tránh race khi gọi
            if (h != null) try { h(); } catch (Exception ex) { LogI("Lỗi xử lý sự kiện boot: " + ex.Message); }
        }

        /// <summary>Luồng nền: nâng ưu tiên CPU cho qemu, báo sẵn sàng sớm nhất có thể, rồi (nếu cần) tắt ADB ở nền.
        /// Dấu hiệu sẵn sàng (cái nào tới trước): adb báo sys.boot_completed=1 | emulator in "Boot completed in" |
        /// emulator in "Successfully loaded snapshot". Không bỏ cuộc sau 400s: vẫn thử tiếp (thưa hơn) tới khi emulator thoát.</summary>
        void BootWatch(Process p)
        {
            var adb = NewAdb(); // adb nhắm vào emulator
            var t0 = DateTime.Now; // Mốc bắt đầu chờ
            bool ready = false, prioDone = !S.GetBool("HighPriority", true), slowWarned = false, killWarned = false, reconnLogged = false;
            DateTime readyAt = DateTime.MinValue, lastReconn = t0;
            bool killedInSave = AdbKilled; // Trạng thái ADB ghi nhận trong bản Quick Boot / bản lưu sắp nạp
            Thread.Sleep(2000);
            while (!p.HasExited)
            {
                var now = DateTime.Now;
                double sec = (now - t0).TotalSeconds;
                if (!prioDone && sec >= 4) prioDone = RaiseQemuPriority() || sec > 60; // qemu có thể chưa kịp sinh ra -> thử lại

                string st = adb.State(); // "device" | "offline" | "" ...
                if (st == "device" && adb.Shell("getprop sys.boot_completed", 5000).Trim() == "1")
                {
                    if (!ready) { ready = true; readyAt = now; AdbKilled = false; MarkReady(t0, "adb"); }
                    // adb hay rớt "offline" ngay sau boot_completed lần đầu -> xác nhận lần 2 rồi mới tắt ADB
                    if (Sleep(3000) && adb.Booted())
                    {
                        AdbKilled = false; // ADB đang chạy
                        if (!AdbOn) KillAdb(); // Người dùng không cho dùng ADB -> tắt (ở nền, sau khi đã báo sẵn sàng)
                        return;
                    }
                    continue;
                }

                long sl = Interlocked.Read(ref snapLoadTicks); // Thời điểm nạp snapshot xong (0 = chưa thấy)
                bool snapOk = sl != 0 && !snapFailed;
                if (!ready && (bootDone || snapOk))
                {
                    ready = true; readyAt = now;
                    MarkReady(t0, bootDone ? "emulator báo boot xong" : "khôi phục trạng thái");
                }

                if (ready)
                {
                    // Trạng thái nạp lại đã tắt ADB sẵn: 10s sau khi nạp mà adb vẫn không online -> không cần làm gì thêm
                    if (snapOk && killedInSave && st != "device" && (now - new DateTime(sl)).TotalSeconds > 10) { AdbKilled = true; return; }
                    if (AdbOn && (now - readyAt).TotalMinutes > 10) return; // Dùng ADB: hết 10 phút vẫn không online thì thôi chờ
                    if (!AdbOn && !killWarned && (now - readyAt).TotalMinutes > 10)
                    {
                        killWarned = true; // Vẫn thử tiếp, chỉ cảnh báo 1 lần
                        LogI("⚠ Android đã chạy nhưng adb chưa kết nối được nên CHƯA tắt được ADB. Bấm ⟳ Khởi động lại nhanh nếu lo game phát hiện.");
                    }
                }
                else if (!slowWarned && sec > 400)
                {
                    slowWarned = true;
                    LogI("⚠ Sau 400s vẫn chưa thấy Android boot xong - vẫn tiếp tục kiểm tra. Xem logs\\emulator.log");
                }

                // adb kẹt "offline" (cổng ngoài dải chuẩn): ép kết nối lại mỗi 15s
                if (st == "offline" && (now - lastReconn).TotalSeconds >= 15 && !(ready && killedInSave && snapOk))
                {
                    lastReconn = now;
                    if (!reconnLogged) { reconnLogged = true; LogI("adb đang offline -> thử kết nối lại (adb reconnect offline)"); }
                    adb.ReconnectOffline();
                }
                Thread.Sleep(sec < 400 ? 2000 : 10000); // Quá 400s thì kiểm tra thưa hơn cho nhẹ
            }
        }

        long snapLoadTicks; // Thời điểm emulator in "Successfully loaded snapshot" (ticks, 0 = chưa thấy)
        volatile bool snapFailed; // Emulator báo nạp snapshot lỗi
        volatile bool bootDone; // Emulator in "Boot completed in ... ms"

        // Ngủ ms mili-giây rồi trả về true (để dùng được trong biểu thức &&)
        static bool Sleep(int ms) { Thread.Sleep(ms); return true; }

        // Xử lý khi tiến trình emulator thoát: tự khởi động lại nếu crash
        void OnExit(object sender, EventArgs e)
        {
            int code = -1; // Mã thoát mặc định khi không đọc được
            try { code = ((Process)sender).ExitCode; } catch { } // Đọc mã thoát của tiến trình
            IsBooted = false; // Không còn ở trạng thái đã boot
            LogI("Emulator thoát, mã " + code); // Ghi log mã thoát
            var ex0 = Exited; // Báo cho bộ giám sát (gửi Discord)
            if (ex0 != null) try { ex0(code, !stopping); } catch { }
            // mã 0 = người dùng tự đóng cửa sổ, không phải crash
            // Tự khởi động lại khi: không phải dừng chủ động, mã khác 0, bật AutoRestart và chưa quá 3 lần
            if (!stopping && code != 0 && S.GetBool("AutoRestart", true) && autoRestarts < 3)
            {
                autoRestarts++; // Tăng bộ đếm số lần tự khởi động lại
                LogI("⟳ Emulator dừng bất thường, khởi động lại (cold boot) lần " + autoRestarts + "/3 ..."); // Ghi log
                Thread.Sleep(3000); // Chờ 3 giây trước khi chạy lại
                try { Start(true); return; } catch (Exception ex) { LogI("Khởi động lại lỗi: " + ex.Message); } // Chạy lại bằng cold boot, lỗi thì ghi log
            }
            var h = Stopped; // Sao chép delegate để tránh race
            if (h != null) h(); // Phát sự kiện Stopped
        }

        // Dừng emulator: tắt mềm qua adb, quá thời gian thì kill cưỡng bức
        public void Stop()
        {
            stopping = true; // Đánh dấu dừng chủ động để OnExit không tự khởi động lại
            var p = proc; // Giữ tham chiếu tiến trình hiện tại
            EmuConsole("kill", 10000); // Tắt giả lập qua cổng console của emulator
            if (p != null && !p.HasExited && !p.WaitForExit(25000)) // Chờ tối đa 25 giây cho tiến trình tự thoát
            {
                LogI("Emulator không tự tắt, kill cưỡng bức."); // Ghi log
                Proc.KillTree(p.Id); // Diệt cả cây tiến trình (emulator + qemu con)
            }
        }
    }
}
