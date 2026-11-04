using System; // Kiểu cơ bản: Exception, Action, DateTime, StringComparison...
using System.Collections.Generic; // Dictionary dùng cho biến môi trường
using System.Diagnostics; // Process, ProcessPriorityClass để chạy/quản lý tiến trình
using System.IO; // Path, File, Directory, DriveInfo để thao tác file/thư mục
using System.Text; // StringBuilder để ghép chuỗi tham số và config.ini
using System.Threading; // Thread, Thread.Sleep cho luồng theo dõi boot

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    // Lớp bọc adb.exe: chạy lệnh adb nhắm vào một thiết bị (serial) cụ thể
    public class Adb
    {
        public readonly string Exe; // Đường dẫn tới adb.exe
        public readonly string Serial; // Serial thiết bị, ví dụ "emulator-5554"

        // Hàm khởi tạo: lưu đường dẫn adb và serial thiết bị
        public Adb(string exe, string serial) { Exe = exe; Serial = serial; }

        // Chạy "adb -s <serial> <args>" và trả về output dạng chuỗi, có timeout
        public string Run(string args, int timeoutMs) { return Proc.Run(Exe, "-s " + Serial + " " + args, timeoutMs, null); }
        // Chạy một lệnh shell trên Android qua "adb shell <cmd>"
        public string Shell(string cmd, int timeoutMs) { return Run("shell " + cmd, timeoutMs); }
        // Chụp màn hình thiết bị (PNG thô) qua "adb exec-out screencap -p", timeout 15 giây
        public byte[] Screencap() { return Proc.RunBytes(Exe, "-s " + Serial + " exec-out screencap -p", 15000); }

        // Kiểm tra thiết bị có đang online trong "adb devices" không
        public bool Online()
        {
            // Tìm dòng "<serial>\tdevice" (trạng thái device = online, không phải offline)
            return Proc.Run(Exe, "devices", 8000, null).Contains(Serial + "\tdevice");
        }

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

        // Hàm khởi tạo: nhận cấu hình và thư mục gốc
        public EmulatorController(Settings s, string baseDir) { S = s; this.baseDir = baseDir; }

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
                Log.I("Chuyển " + (need / 1073741824.0).ToString("0.0") + " GB sang " + newRoot + " ..."); // Ghi log bắt đầu chuyển
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
            Log.I("✔ Nơi lưu mới: " + newRoot); // Ghi log hoàn tất
        }
        // Mỗi bản Android một AVD riêng: dùng chung userdata giữa các bản sẽ không boot được.
        // Tên AVD đang dùng: lấy từ cấu hình, nếu không có thì sinh theo API + loại image
        public string AvdName { get { return S.Get("AvdName", AvdNameFor(Api, Tag)); } }

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
            Log.I("Đã xoá máy ảo " + n + " (toàn bộ app/dữ liệu)"); // Ghi log
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
            Log.I("Đã xoá trắng dữ liệu " + AvdNameFor(api, tag) + ". Lần chạy tới sẽ như máy mới."); // Ghi log
        }

        public volatile bool IsBooted; // true khi Android đã boot xong
        public DateTime StartedAt; // Thời điểm bắt đầu chạy emulator
        public int Port { get { return S.GetInt("Port", 5554); } } // Cổng console của emulator (mặc định 5554)
        public string Serial { get { return "emulator-" + Port; } } // Serial adb tương ứng, ví dụ "emulator-5554"
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
        public Adb NewAdb() { return new Adb(AdbExe, Serial); } // Tạo đối tượng Adb nhắm vào emulator này

        // Biến môi trường truyền cho emulator.exe để nó tìm thấy SDK và AVD
        public Dictionary<string, string> Env()
        {
            return new Dictionary<string, string> // Tạo bảng biến môi trường
            {
                { "ANDROID_SDK_ROOT", SdkDir }, // Thư mục SDK (tên biến kiểu cũ)
                { "ANDROID_HOME", SdkDir }, // Thư mục SDK (tên biến kiểu mới)
                { "ANDROID_AVD_HOME", AvdHome } // Thư mục chứa các AVD
            };
        }

        // Ghi file config.ini và <tên>.ini cho AVD theo cấu hình hiện tại
        public void CreateAvd()
        {
            string sys = Path.Combine(SdkDir, SysImgRel); // Đường dẫn tuyệt đối tới system image
            if (!Directory.Exists(sys)) throw new Exception("Chưa có system image: " + sys); // Báo lỗi nếu chưa tải image

            string avdDir = Path.Combine(AvdHome, AvdName + ".avd"); // Thư mục máy ảo
            Directory.CreateDirectory(avdDir); // Tạo thư mục nếu chưa có
            int w = S.GetInt("Width", 1280), h = S.GetInt("Height", 720); // Độ phân giải màn hình
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
            kv("hw.lcd.density", S.GetInt("Dpi", 240)); // Mật độ điểm ảnh (DPI)
            kv("hw.initialOrientation", w > h ? "landscape" : "portrait"); // Hướng màn hình: ngang nếu rộng > cao
            kv("hw.gpu.enabled", "yes"); // Bật tăng tốc GPU
            kv("hw.gpu.mode", S.Get("Gpu", "host")); // Chế độ GPU (host = dùng card đồ hoạ thật)
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
            Log.I("✔ Đã ghi AVD '" + AvdName + "' (" + w + "x" + h + ", " + S.GetInt("Cores", 4) + " nhân, " + S.GetInt("RamMB", 4096) + " MB)");
        }

        // Kiểm tra máy có hỗ trợ tăng tốc phần cứng (WHPX/AEHD...) hay không
        public string AccelCheck()
        {
            return Proc.Run(EmulatorExe, "-accel-check", 30000, Env()); // Chạy "emulator -accel-check" và trả về kết quả
        }

        // Khởi động emulator; cold=true thì bỏ qua snapshot (khởi động nguội)
        public void Start(bool cold)
        {
            if (Running) { Log.I("Giả lập đang chạy."); return; } // Đang chạy rồi thì thoát
            if (!File.Exists(EmulatorExe)) throw new Exception("Chưa có emulator.exe, bấm 'Tải SDK + image' trước."); // Chưa tải SDK
            CreateAvd(); // luôn ghi lại config.ini theo cấu hình hiện tại (không đụng dữ liệu)

            Proc.Run(AdbExe, "start-server", 20000, null); // Khởi động adb server trước

            var a = new StringBuilder(); // Bộ đệm tham số dòng lệnh cho emulator.exe
            a.Append("-avd ").Append(AvdName) // -avd: tên máy ảo cần chạy
             .Append(" -port ").Append(Port) // -port: cổng console (adb serial = emulator-<port>)
             .Append(" -gpu ").Append(S.Get("Gpu", "host")) // -gpu: chế độ render đồ hoạ
             .Append(" -cores ").Append(S.GetInt("Cores", 4)) // -cores: số nhân CPU ảo
             .Append(" -memory ").Append(S.GetInt("RamMB", 4096)) // -memory: RAM (MB)
             .Append(" -netdelay none -netspeed full"); // -netdelay none: không giả lập độ trễ mạng; -netspeed full: tốc độ mạng tối đa
            if (!S.GetBool("BootAnim", true)) a.Append(" -no-boot-anim"); // -no-boot-anim: tắt hoạt ảnh khởi động cho boot nhanh hơn
            string wipe = Path.Combine(AvdHome, AvdName + ".avd", WipeMarker); // Đường dẫn file đánh dấu xoá trắng
            bool wiping = File.Exists(wipe); // Có yêu cầu xoá trắng đang chờ không
            if (wiping) { a.Append(" -wipe-data"); cold = true; } // -wipe-data: xoá dữ liệu người dùng, buộc cold boot
            if (!S.GetBool("QuickBoot", true)) a.Append(" -no-snapshot"); // -no-snapshot: không nạp và không lưu snapshot
            else if (cold) a.Append(" -no-snapshot-load"); // -no-snapshot-load: không nạp snapshot (vẫn lưu khi tắt)
            if (S.GetBool("NoAudio", false)) a.Append(" -no-audio"); // -no-audio: tắt âm thanh
            if (S.GetBool("DisableVulkan", true)) a.Append(" -feature -Vulkan"); // -feature -Vulkan: tắt tính năng Vulkan (tránh lỗi driver)
            string extra = S.Get("ExtraArgs", "").Trim(); // Tham số bổ sung do người dùng nhập
            if (extra.Length > 0) a.Append(' ').Append(extra); // Nối thêm nếu có

            string logFile = Path.Combine(baseDir, "logs", "emulator.log"); // File ghi log đầu ra của emulator
            var logLock = new object(); // Khoá đồng bộ khi ghi file log từ nhiều luồng
            Log.I("emulator " + a); // Ghi log dòng lệnh sẽ chạy
            stopping = false; // Xoá cờ dừng chủ động
            // Chạy emulator.exe với tham số và biến môi trường, xử lý từng dòng output
            proc = Proc.Start(EmulatorExe, a.ToString(), Env(), line =>
            {
                lock (logLock) { try { File.AppendAllText(logFile, line + Environment.NewLine); } catch { } } // Ghi dòng vào emulator.log, bỏ qua lỗi
                string u = line.ToUpperInvariant(); // Chuyển chữ hoa để so khớp không phân biệt hoa thường
                if (u.Contains("ERROR") || u.Contains("FATAL") || u.Contains("PANIC")) Log.I("[emu] " + line); // Chỉ đẩy dòng lỗi lên log giao diện
            }, false);
            proc.Exited += OnExit; // Đăng ký xử lý khi emulator thoát
            if (wiping) File.Delete(wipe); // Đã thêm -wipe-data thì xoá file đánh dấu
            IsBooted = false; // Chưa boot xong
            StartedAt = DateTime.Now; // Ghi nhận thời điểm bắt đầu

            var p = proc; // Giữ tham chiếu tiến trình hiện tại cho luồng theo dõi
            new Thread(() => BootWatch(p)) { IsBackground = true }.Start(); // Chạy luồng nền theo dõi quá trình boot
        }

        // Luồng nền: nâng ưu tiên CPU cho qemu và chờ Android boot xong
        void BootWatch(Process p)
        {
            Thread.Sleep(6000); // Chờ 6 giây cho tiến trình qemu khởi tạo
            if (S.GetBool("HighPriority", true)) // Nếu bật tuỳ chọn ưu tiên cao
                foreach (var q in Process.GetProcesses()) // Duyệt mọi tiến trình đang chạy
                {
                    try
                    {
                        // Tìm tiến trình lõi qemu-system-* của emulator
                        if (q.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
                        {
                            q.PriorityClass = ProcessPriorityClass.AboveNormal; // Đặt mức ưu tiên CPU trên trung bình
                            Log.I("Đặt ưu tiên CPU AboveNormal cho " + q.ProcessName + " (pid " + q.Id + ")"); // Ghi log
                        }
                    }
                    catch { } // Bỏ qua tiến trình không có quyền truy cập
                }

            var adb = NewAdb(); // Tạo adb nhắm vào emulator
            var t0 = DateTime.Now; // Mốc thời gian bắt đầu chờ
            while ((DateTime.Now - t0).TotalSeconds < 400 && !p.HasExited) // Chờ tối đa 400 giây khi emulator còn chạy
            {
                // adb hay rớt "offline" ngay sau boot_completed lần đầu -> cần 2 lần OK liên tiếp
                if (adb.Booted() && Sleep(3000) && adb.Booted()) // Kiểm tra 2 lần cách nhau 3 giây
                {
                    autoRestarts = 0; // Boot thành công -> đặt lại bộ đếm tự khởi động lại
                    IsBooted = true; // Đánh dấu đã boot
                    Log.I("✔ Android đã boot xong sau " + (int)(DateTime.Now - t0).TotalSeconds + "s"); // Ghi log thời gian boot
                    var h = Booted; // Sao chép delegate để tránh race khi gọi
                    if (h != null) h(); // Phát sự kiện Booted
                    return; // Kết thúc luồng
                }
                Thread.Sleep(2000); // Chưa xong, chờ 2 giây rồi thử lại
            }
            if (!p.HasExited) Log.I("⚠ Quá thời gian chờ boot. Xem logs\\emulator.log"); // Hết giờ mà emulator vẫn chạy -> cảnh báo
        }

        // Ngủ ms mili-giây rồi trả về true (để dùng được trong biểu thức &&)
        static bool Sleep(int ms) { Thread.Sleep(ms); return true; }

        // Xử lý khi tiến trình emulator thoát: tự khởi động lại nếu crash
        void OnExit(object sender, EventArgs e)
        {
            int code = -1; // Mã thoát mặc định khi không đọc được
            try { code = ((Process)sender).ExitCode; } catch { } // Đọc mã thoát của tiến trình
            IsBooted = false; // Không còn ở trạng thái đã boot
            Log.I("Emulator thoát, mã " + code); // Ghi log mã thoát
            // mã 0 = người dùng tự đóng cửa sổ, không phải crash
            // Tự khởi động lại khi: không phải dừng chủ động, mã khác 0, bật AutoRestart và chưa quá 3 lần
            if (!stopping && code != 0 && S.GetBool("AutoRestart", true) && autoRestarts < 3)
            {
                autoRestarts++; // Tăng bộ đếm số lần tự khởi động lại
                Log.I("⟳ Emulator dừng bất thường, khởi động lại (cold boot) lần " + autoRestarts + "/3 ..."); // Ghi log
                Thread.Sleep(3000); // Chờ 3 giây trước khi chạy lại
                try { Start(true); return; } catch (Exception ex) { Log.I("Khởi động lại lỗi: " + ex.Message); } // Chạy lại bằng cold boot, lỗi thì ghi log
            }
            var h = Stopped; // Sao chép delegate để tránh race
            if (h != null) h(); // Phát sự kiện Stopped
        }

        // Dừng emulator: tắt mềm qua adb, quá thời gian thì kill cưỡng bức
        public void Stop()
        {
            stopping = true; // Đánh dấu dừng chủ động để OnExit không tự khởi động lại
            var p = proc; // Giữ tham chiếu tiến trình hiện tại
            Proc.Run(AdbExe, "-s " + Serial + " emu kill", 10000, null); // Tắt giả lập qua cổng console của emulator
            if (p != null && !p.HasExited && !p.WaitForExit(25000)) // Chờ tối đa 25 giây cho tiến trình tự thoát
            {
                Log.I("Emulator không tự tắt, kill cưỡng bức."); // Ghi log
                Proc.KillTree(p.Id); // Diệt cả cây tiến trình (emulator + qemu con)
            }
        }
    }
}
