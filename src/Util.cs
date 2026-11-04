using System; // Kiểu cơ bản: DateTime, Action, Exception, Math...
using System.Collections.Generic; // Dictionary, IDictionary
using System.Diagnostics; // Process, ProcessStartInfo để chạy chương trình ngoài
using System.Globalization; // CultureInfo để đổi số sang chuỗi không phụ thuộc vùng
using System.IO; // File, Directory, Path, FileInfo, MemoryStream
using System.Text; // StringBuilder, Encoding

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Ghi log ra file logs\app.log và báo cho giao diện qua sự kiện Line.</summary>
    public static class Log
    {
        public static event Action<string> Line; // Sự kiện báo mỗi dòng log mới (UI đăng ký để hiển thị)
        static readonly object lk = new object(); // Khoá để nhiều luồng ghi file không đè nhau
        static string file; // Đường dẫn file log, null khi chưa Init

        /// <summary>Khởi tạo thư mục log; xoá file log cũ nếu lớn hơn 4 MB.</summary>
        public static void Init(string dir)
        {
            Directory.CreateDirectory(dir); // Tạo thư mục log nếu chưa có
            file = Path.Combine(dir, "app.log"); // Ghép đường dẫn file app.log
            try { if (File.Exists(file) && new FileInfo(file).Length > 4 * 1024 * 1024) File.Delete(file); } catch { } // File log > 4 MB thì xoá đi, lỗi thì bỏ qua
        }

        /// <summary>Ghi một dòng log kèm giờ và phát sự kiện Line.</summary>
        public static void I(string msg)
        {
            string s = DateTime.Now.ToString("HH:mm:ss") + "  " + msg; // Gắn giờ:phút:giây vào đầu dòng
            lock (lk) { try { if (file != null) File.AppendAllText(file, s + Environment.NewLine); } catch { } } // Khoá rồi nối dòng vào file log, lỗi thì bỏ qua
            var h = Line; // Chép sự kiện ra biến tạm để tránh bị gỡ giữa chừng
            if (h != null) h(s); // Có người đăng ký thì gọi báo dòng log
        }
    }

    /// <summary>Đọc/ghi file cấu hình dạng ini đơn giản: mỗi dòng key=value.</summary>
    public class Settings
    {
        readonly string path; // Đường dẫn file cấu hình
        readonly Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Bảng key -> value, không phân biệt hoa thường

        /// <summary>Nạp cấu hình từ file (nếu file tồn tại).</summary>
        public Settings(string path)
        {
            this.path = path; // Lưu lại đường dẫn để Save dùng
            if (!File.Exists(path)) return; // Chưa có file thì để trống
            foreach (var line in File.ReadAllLines(path)) // Duyệt từng dòng trong file
            {
                int i = line.IndexOf('='); // Tìm vị trí dấu '='
                if (i > 0 && !line.StartsWith("#")) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); // Dòng hợp lệ, không phải chú thích '#': tách key và value
            }
        }

        public bool Has(string k) { return d.ContainsKey(k); } // Kiểm tra key có trong cấu hình không
        public string Get(string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; } // Lấy chuỗi theo key, không có thì trả def
        public int GetInt(string k, int def) { int v; return int.TryParse(Get(k, ""), out v) ? v : def; } // Lấy số nguyên, sai định dạng thì trả def

        /// <summary>Lấy giá trị đúng/sai: "1" hoặc "true" là đúng.</summary>
        public bool GetBool(string k, bool def)
        {
            string v = Get(k, ""); // Đọc chuỗi giá trị
            if (v == "") return def; // Không có giá trị thì trả mặc định
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); // "1" hoặc "true" (không phân biệt hoa thường) là đúng
        }

        /// <summary>Gán giá trị cho key (bool lưu thành 1/0).</summary>
        public void Set(string k, object v)
        {
            d[k] = v is bool ? ((bool)v ? "1" : "0") : Convert.ToString(v, CultureInfo.InvariantCulture); // bool -> "1"/"0", kiểu khác -> chuỗi theo chuẩn bất biến (dấu chấm thập phân)
        }

        /// <summary>Ghi toàn bộ cấu hình xuống file.</summary>
        public void Save()
        {
            var sb = new StringBuilder(); // Bộ đệm tạo nội dung file
            foreach (var kv in d) sb.AppendLine(kv.Key + "=" + kv.Value); // Mỗi cặp ghi một dòng key=value
            File.WriteAllText(path, sb.ToString()); // Ghi đè file cấu hình
        }
    }

    /// <summary>Tiện ích xử lý file/thư mục (xoá được cả file chỉ đọc, chuyển thư mục khác ổ).</summary>
    public static class FileUtil
    {
        /// <summary>Xoá thư mục kể cả file ReadOnly (emulator tạo pstore.bin chỉ đọc, Directory.Delete sẽ báo Access denied).</summary>
        public static void DeleteDir(string dir)
        {
            if (!Directory.Exists(dir)) return; // Thư mục không tồn tại thì thôi
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) // Duyệt mọi file trong cây thư mục
                File.SetAttributes(f, FileAttributes.Normal); // Bỏ thuộc tính chỉ đọc/ẩn của file
            foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories)) // Duyệt mọi thư mục con
                new DirectoryInfo(d).Attributes = FileAttributes.Directory; // Đặt lại thuộc tính thư mục bình thường
            Directory.Delete(dir, true); // Xoá cả cây thư mục
        }

        /// <summary>Xoá một file kể cả khi file chỉ đọc.</summary>
        public static void DeleteFile(string f)
        {
            if (!File.Exists(f)) return; // Không có file thì thôi
            File.SetAttributes(f, FileAttributes.Normal); // Bỏ thuộc tính chỉ đọc
            File.Delete(f); // Xoá file
        }

        /// <summary>Tính tổng dung lượng (byte) mọi file trong thư mục.</summary>
        public static long DirSize(string dir)
        {
            if (!Directory.Exists(dir)) return 0; // Không có thư mục thì dung lượng 0
            long s = 0; // Biến cộng dồn số byte
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) s += new FileInfo(f).Length; // Cộng kích thước từng file
            return s; // Trả tổng
        }

        /// <summary>Kiểm tra hai đường dẫn có nằm cùng ổ đĩa không.</summary>
        public static bool SameDrive(string a, string b)
        {
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); // So sánh gốc ổ (vd "C:\") không phân biệt hoa thường
        }

        /// <summary>Chuyển thư mục: cùng ổ thì đổi tên, khác ổ thì copy (có log %) rồi xoá nguồn.</summary>
        public static void MoveDir(string src, string dst)
        {
            if (!Directory.Exists(src)) return; // Nguồn không tồn tại thì thôi
            if (Directory.Exists(dst) && Directory.GetFileSystemEntries(dst).Length > 0) // Đích đã có và không rỗng
                throw new Exception("Thư mục đích đã có dữ liệu: " + dst); // Báo lỗi để tránh ghi đè dữ liệu
            if (Directory.Exists(dst)) Directory.Delete(dst); // Đích rỗng thì xoá để Move/copy tạo lại
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dst))); // Tạo thư mục cha của đích
            if (SameDrive(src, dst)) { Directory.Move(src, dst); return; } // Cùng ổ: chỉ đổi tên, rất nhanh

            long total = Math.Max(1, DirSize(src)), done = 0; // Tổng byte cần copy (tối thiểu 1 để khỏi chia 0) và số byte đã copy
            int lastPct = -10; // Phần trăm đã log gần nhất (bắt đầu -10 để log ngay mốc 0%)
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories)) // Duyệt mọi thư mục con của nguồn
                Directory.CreateDirectory(dst + d.Substring(src.Length)); // Tạo thư mục tương ứng bên đích
            Directory.CreateDirectory(dst); // Tạo chính thư mục đích (trường hợp nguồn không có thư mục con)
            try
            {
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) // Duyệt mọi file của nguồn
                {
                    string t = dst + f.Substring(src.Length); // Đường dẫn file đích tương ứng
                    File.Copy(f, t, true); // Copy file, ghi đè nếu có
                    File.SetAttributes(t, FileAttributes.Normal); // Bỏ chỉ đọc ở bản copy
                    done += new FileInfo(f).Length; // Cộng số byte đã copy
                    int pct = (int)(done * 100 / total); // Tính phần trăm hoàn thành
                    if (pct >= lastPct + 10) { lastPct = pct; Log.I("   " + Path.GetFileName(src) + ": " + pct + "%"); } // Cứ tăng 10% thì ghi log một lần
                }
            }
            catch
            {
                // copy hỏng giữa chừng: bỏ bản copy dở, bản gốc vẫn nguyên
                try { DeleteDir(dst); } catch { } // Xoá bản copy dở, lỗi thì bỏ qua
                throw; // Ném lại lỗi gốc cho nơi gọi
            }
            DeleteDir(src); // Copy xong hết thì xoá thư mục nguồn
        }
    }

    /// <summary>Chạy tiến trình ngoài (adb, emulator, taskkill...) và lấy output.</summary>
    public static class Proc
    {
        /// <summary>Tạo cấu hình chạy tiến trình: ẩn cửa sổ, chuyển hướng stdout/stderr UTF-8, thêm biến môi trường.</summary>
        static ProcessStartInfo Psi(string exe, string args, IDictionary<string, string> env)
        {
            var psi = new ProcessStartInfo(exe, args) // Thông tin chạy: file exe + tham số
            {
                UseShellExecute = false, // Không qua shell, bắt buộc để chuyển hướng output
                RedirectStandardOutput = true, // Đọc được stdout
                RedirectStandardError = true, // Đọc được stderr
                CreateNoWindow = true, // Không hiện cửa sổ console
                StandardOutputEncoding = Encoding.UTF8, // Giải mã stdout theo UTF-8
                StandardErrorEncoding = Encoding.UTF8 // Giải mã stderr theo UTF-8
            };
            if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value; // Gán thêm từng biến môi trường nếu có
            return psi; // Trả cấu hình
        }

        /// <summary>Chạy lệnh, chờ xong, trả về stdout+stderr.</summary>
        public static string Run(string exe, string args, int timeoutMs, IDictionary<string, string> env)
        {
            var sb = new StringBuilder(); // Bộ đệm gom output
            try
            {
                using (var p = new Process { StartInfo = Psi(exe, args, env) }) // Tạo tiến trình, tự giải phóng khi xong
                {
                    DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); }; // Hàm nhận mỗi dòng output, khoá sb vì 2 luồng cùng ghi
                    p.OutputDataReceived += h; // Nhận dòng từ stdout
                    p.ErrorDataReceived += h; // Nhận dòng từ stderr
                    p.Start(); // Chạy tiến trình
                    p.BeginOutputReadLine(); // Bắt đầu đọc stdout bất đồng bộ
                    p.BeginErrorReadLine(); // Bắt đầu đọc stderr bất đồng bộ
                    if (!p.WaitForExit(timeoutMs)) // Chờ tối đa timeoutMs, quá giờ thì vào nhánh này
                    {
                        try { p.Kill(); } catch { } // Diệt tiến trình bị treo
                        lock (sb) sb.AppendLine("[timeout]"); // Ghi chú đã quá thời gian
                    }
                    else p.WaitForExit(); // Đã thoát: gọi thêm lần nữa để chờ đọc hết output
                }
            }
            catch (Exception ex) { lock (sb) sb.AppendLine("[error] " + ex.Message); } // Lỗi khi chạy (vd không thấy exe): ghi vào kết quả
            lock (sb) return sb.ToString(); // Trả toàn bộ output
        }

        /// <summary>Chạy lệnh và lấy stdout dạng nhị phân (vd: screencap).</summary>
        public static byte[] RunBytes(string exe, string args, int timeoutMs)
        {
            using (var p = new Process { StartInfo = Psi(exe, args, null) }) // Tạo tiến trình, tự giải phóng khi xong
            {
                p.ErrorDataReceived += (s, e) => { }; // Bỏ qua stderr (chỉ đọc cho khỏi đầy bộ đệm)
                p.Start(); // Chạy tiến trình
                p.BeginErrorReadLine(); // Bắt đầu đọc stderr bất đồng bộ
                var ms = new MemoryStream(); // Bộ nhớ chứa dữ liệu nhị phân
                var t = p.StandardOutput.BaseStream.CopyToAsync(ms); // Copy byte thô từ stdout vào bộ nhớ
                if (!t.Wait(timeoutMs)) { try { p.Kill(); } catch { } return null; } // Quá giờ: diệt tiến trình và trả null
                p.WaitForExit(3000); // Chờ tiến trình thoát tối đa 3 giây
                return ms.ToArray(); // Trả mảng byte đã đọc
            }
        }

        /// <summary>Chạy nền, mỗi dòng output gọi onLine.</summary>
        public static Process Start(string exe, string args, IDictionary<string, string> env, Action<string> onLine, bool stdin)
        {
            var psi = Psi(exe, args, env); // Tạo cấu hình chạy
            psi.RedirectStandardInput = stdin; // Có cần gửi lệnh qua stdin không
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true }; // Tạo tiến trình, bật sự kiện Exited
            DataReceivedEventHandler h = (s, e) => { if (e.Data != null && onLine != null) onLine(e.Data); }; // Mỗi dòng output thì gọi onLine
            p.OutputDataReceived += h; // Nhận dòng từ stdout
            p.ErrorDataReceived += h; // Nhận dòng từ stderr
            p.Start(); // Chạy tiến trình
            p.BeginOutputReadLine(); // Bắt đầu đọc stdout bất đồng bộ
            p.BeginErrorReadLine(); // Bắt đầu đọc stderr bất đồng bộ
            if (stdin) p.StandardInput.NewLine = "\n"; // Dùng xuống dòng kiểu Unix khi gửi lệnh (vd adb shell)
            return p; // Trả tiến trình cho nơi gọi quản lý
        }

        /// <summary>Diệt tiến trình và toàn bộ tiến trình con bằng taskkill.</summary>
        public static void KillTree(int pid)
        {
            Run("taskkill", "/PID " + pid + " /T /F", 10000, null); // /T: cả cây tiến trình con, /F: ép diệt, chờ tối đa 10 giây
        }
    }
}
