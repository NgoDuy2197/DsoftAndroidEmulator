using System; // Kiểu cơ bản: Exception, BitConverter...
using System.Collections.Generic; // List, HashSet
using System.IO; // File, Directory, Path
using System.IO.Compression; // ZipFile để giải nén gói tải về
using System.Linq; // Where, OrderByDescending, GroupBy... trên danh sách
using System.Net; // WebClient, HttpWebRequest để tải dữ liệu
using System.Security.Cryptography; // SHA1, SHA256 để kiểm tra checksum
using System.Xml; // XmlDocument để đọc file kho của Google

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    /// <summary>Tải platform-tools, emulator, system image trực tiếp từ kho Google (không cần Java/sdkmanager) + scrcpy-server.</summary>
    public class SdkInstaller
    {
        public const string ScrcpyVersion = "2.7"; // Phiên bản scrcpy-server dùng để hiển thị/điều khiển màn hình
        const string Repo = "https://dl.google.com/android/repository/"; // Địa chỉ gốc kho SDK của Google

        // Link tải scrcpy-server từ trang release GitHub
        public static string ScrcpyUrl
        {
            // Ghép URL: .../download/v2.7/scrcpy-server-v2.7
            get { return "https://github.com/Genymobile/scrcpy/releases/download/v" + ScrcpyVersion + "/scrcpy-server-v" + ScrcpyVersion; }
        }

        // Đường dẫn file scrcpy-server lưu trong thư mục SDK
        public static string ScrcpyFile(string sdk) { return Path.Combine(sdk, "scrcpy-server-v" + ScrcpyVersion); }

        // Thông tin một gói trong kho: đường dẫn gói, số revision, link tải, checksum, kích thước
        class Pkg { public string PkgPath; public long Rev; public string Url; public string Sum; public long Size; }

        /// <summary>Một bản Android (system image) trong kho Google hoặc đã cài trên máy.</summary>
        public class ImageInfo
        {
            public int Api; // Mức API (ví dụ 30 = Android 11)
            public string Tag, Abi, Url, Sum; // Loại image, kiến trúc, link tải, checksum
            public long Rev, SizeMB; // Revision và kích thước gói (MB)
            public bool Installed; // true nếu đã cài trên máy

            public string Version { get { return AndroidName(Api); } } // Tên phiên bản dễ đọc
            public string Kind { get { return Tag.Contains("playstore") ? "Play Store" : "Google APIs"; } } // Loại image dễ đọc

            // Đổi mức API sang tên phiên bản Android
            public static string AndroidName(int api)
            {
                switch (api) // Chọn theo mức API
                {
                    case 28: return "Android 9"; // API 28
                    case 29: return "Android 10"; // API 29
                    case 30: return "Android 11"; // API 30
                    case 31: return "Android 12"; // API 31
                    case 32: return "Android 12L"; // API 32
                    case 33: return "Android 13"; // API 33
                    case 34: return "Android 14"; // API 34
                    case 35: return "Android 15"; // API 35
                    case 36: return "Android 16"; // API 36
                    default: return "API " + api; // Chưa biết tên thì hiện số API
                }
            }
        }

        readonly string sdk; // Thư mục SDK đích

        // Hàm khởi tạo: lưu thư mục SDK và bật TLS 1.2 để tải HTTPS
        public SdkInstaller(string sdk)
        {
            this.sdk = sdk; // Lưu thư mục SDK
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // Bật TLS 1.2 (Google/GitHub yêu cầu)
        }

        // Thư mục chứa một system image: sdk\system-images\android-<api>\<tag>\<abi>
        public string ImageDir(int api, string tag, string abi) { return Path.Combine(sdk, "system-images", "android-" + api, tag, abi); }
        // true nếu đã có cả emulator.exe và adb.exe
        public bool ToolsInstalled { get { return File.Exists(Path.Combine(sdk, "emulator", "emulator.exe")) && File.Exists(Path.Combine(sdk, "platform-tools", "adb.exe")); } }

        // Cài đầy đủ: công cụ + system image của bản Android được chọn
        public void InstallAll(int api, string tag, string abi)
        {
            InstallTools(); // Cài platform-tools, emulator, scrcpy-server
            var img = ListImages(new[] { tag }, abi).FirstOrDefault(i => i.Api == api && i.Url != null); // Tìm image đúng API có link tải
            if (img == null) throw new Exception("Không tìm thấy system-images;android-" + api + ";" + tag + ";" + abi); // Không có trong kho
            InstallImage(img); // Tải và giải nén image
            Log.I("✔ Đã cài đủ tài nguyên vào " + sdk); // Ghi log hoàn tất
        }

        /// <summary>platform-tools + emulator + scrcpy-server.</summary>
        public void InstallTools()
        {
            Directory.CreateDirectory(sdk); // Tạo thư mục SDK nếu chưa có
            Directory.CreateDirectory(Path.Combine(sdk, "platforms")); // Tạo thư mục platforms (emulator cần tồn tại)

            string mainBase; // URL gốc của file kho đọc được
            // Đọc kho chính (thử repository2-3.xml trước, lỗi thì repository2-1.xml) rồi phân tích danh sách gói
            var main = Parse(FetchXml(new[] { Repo + "repository2-3.xml", Repo + "repository2-1.xml" }, out mainBase), mainBase);

            var pt = Best(main, "platform-tools"); // Lấy bản platform-tools mới nhất
            if (pt == null) pt = new Pkg { PkgPath = "platform-tools", Rev = 0, Url = Repo + "platform-tools-latest-windows.zip" }; // Dự phòng: link "latest" cố định
            InstallZip(pt, Path.Combine(sdk, "platform-tools")); // Cài platform-tools (adb)

            var em = Best(main, "emulator"); // Lấy bản emulator mới nhất
            if (em == null) throw new Exception("Không tìm thấy gói emulator cho Windows trong kho Google."); // Không có thì báo lỗi
            InstallZip(em, Path.Combine(sdk, "emulator")); // Cài emulator

            string scr = ScrcpyFile(sdk); // Đường dẫn file scrcpy-server
            if (!File.Exists(scr)) // Chưa có thì tải
            {
                Log.I("Tải scrcpy-server " + ScrcpyVersion + " ..."); // Ghi log
                Download(ScrcpyUrl, scr); // Tải từ GitHub
            }
        }

        /// <summary>Danh sách bản mới nhất mỗi API trong kho + các bản đã cài sẵn trên máy (kể cả khi mất mạng).</summary>
        public List<ImageInfo> ListImages(string[] tags, string abi)
        {
            var res = new List<ImageInfo>(); // Danh sách kết quả
            foreach (var tag in tags) // Duyệt từng loại image (playstore, google_apis...)
            {
                try
                {
                    string b, sysUrl = Repo + "sys-img/" + tag + "/"; // b: URL gốc; sysUrl: thư mục kho image của tag này
                    // Đọc kho image (sys-img2-3.xml, dự phòng sys-img2-1.xml) và phân tích gói
                    var pkgs = Parse(FetchXml(new[] { sysUrl + "sys-img2-3.xml", sysUrl + "sys-img2-1.xml" }, out b), b);
                    // Regex khớp "system-images;android-<số>;<tag>;<abi>", nhóm 1 là số API
                    var re = new System.Text.RegularExpressions.Regex("^system-images;android-(\\d+);" + tag + ";" + abi + "$");
                    // Lọc gói khớp regex rồi gom nhóm theo số API
                    foreach (var g in pkgs.Select(p => new { p, m = re.Match(p.PkgPath) }).Where(x => x.m.Success)
                                          .GroupBy(x => int.Parse(x.m.Groups[1].Value)))
                    {
                        var best = g.OrderByDescending(x => x.p.Rev).First().p; // Chọn revision mới nhất trong nhóm
                        if (g.Key < 28) continue; // Bỏ qua Android cũ hơn 9 (API 28)
                        // Thêm vào danh sách với thông tin tải
                        res.Add(new ImageInfo { Api = g.Key, Tag = tag, Abi = abi, Rev = best.Rev, SizeMB = best.Size / (1024 * 1024), Url = best.Url, Sum = best.Sum });
                    }
                }
                catch (Exception ex) { Log.I("Không tải được danh sách " + tag + ": " + ex.Message); } // Lỗi mạng: ghi log, vẫn tiếp tục

                string root = Path.Combine(sdk, "system-images"); // Thư mục image đã cài trên máy
                if (Directory.Exists(root)) // Có thư mục thì quét
                    foreach (var d in Directory.GetDirectories(root, "android-*")) // Duyệt các thư mục android-<api>
                    {
                        int api; // Số API đọc từ tên thư mục
                        if (!int.TryParse(Path.GetFileName(d).Substring(8), out api)) continue; // Cắt bỏ "android-" (8 ký tự), không phải số thì bỏ qua
                        if (!Directory.Exists(Path.Combine(d, tag, abi))) continue; // Không có đúng tag/abi thì bỏ qua
                        if (!res.Any(i => i.Api == api && i.Tag == tag)) res.Add(new ImageInfo { Api = api, Tag = tag, Abi = abi }); // Thêm bản chỉ có trên máy (không có link tải)
                    }
            }
            foreach (var i in res) i.Installed = File.Exists(Path.Combine(ImageDir(i.Api, i.Tag, i.Abi), "system.img")); // Đánh dấu đã cài nếu có system.img
            return res; // Trả về danh sách
        }

        // Tải và cài một system image
        public void InstallImage(ImageInfo i)
        {
            if (i.Url == null) throw new Exception("Không có link tải cho " + i.Version + " (" + i.Tag + ")"); // Không có link thì báo lỗi
            // Tạo gói từ thông tin image rồi tải + giải nén vào thư mục image
            InstallZip(new Pkg { PkgPath = "system-images;android-" + i.Api + ";" + i.Tag + ";" + i.Abi, Rev = i.Rev, Url = i.Url, Sum = i.Sum },
                       ImageDir(i.Api, i.Tag, i.Abi));
        }

        // Xoá một system image đã cài
        public void DeleteImage(int api, string tag, string abi)
        {
            string d = ImageDir(api, tag, abi); // Thư mục image
            FileUtil.DeleteDir(d); // Xoá thư mục image
            // dọn thư mục cha rỗng
            foreach (var p in new[] { Path.GetDirectoryName(d), Path.GetDirectoryName(Path.GetDirectoryName(d)) }) // Thư mục <tag> và android-<api>
                if (Directory.Exists(p) && Directory.GetFileSystemEntries(p).Length == 0) Directory.Delete(p); // Xoá nếu rỗng
            Log.I("Đã xoá image " + ImageInfo.AndroidName(api) + " (" + tag + ")"); // Ghi log
        }

        // Tải file XML kho, thử lần lượt từng URL; trả về XmlDocument và URL gốc của file đọc được
        XmlDocument FetchXml(string[] urls, out string baseUrl)
        {
            Exception last = null; // Lỗi cuối cùng gặp phải
            foreach (var u in urls) // Thử từng URL
            {
                try
                {
                    using (var wc = new WebClient()) // Tạo WebClient để tải chuỗi
                    {
                        var doc = new XmlDocument(); // Tài liệu XML
                        doc.LoadXml(wc.DownloadString(u)); // Tải và nạp XML
                        baseUrl = u.Substring(0, u.LastIndexOf('/') + 1); // URL gốc = phần trước dấu '/' cuối (dùng cho link tương đối)
                        Log.I("Đọc kho: " + u); // Ghi log
                        return doc; // Thành công thì trả về
                    }
                }
                catch (Exception ex) { last = ex; } // Lỗi thì nhớ lại và thử URL tiếp theo
            }
            throw new Exception("Không đọc được kho SDK: " + (last == null ? "" : last.Message)); // Tất cả URL đều lỗi
        }

        // Lấy nội dung text của node con theo tên (bỏ qua namespace), không có thì trả ""
        static string Txt(XmlNode n, string child)
        {
            var c = n.SelectSingleNode("*[local-name()='" + child + "']"); // Tìm node con theo local-name
            return c == null ? "" : c.InnerText.Trim(); // Trả text đã cắt khoảng trắng
        }

        // Phân tích XML kho thành danh sách gói (chỉ kênh stable, chỉ bản cho Windows x64)
        List<Pkg> Parse(XmlDocument doc, string baseUrl)
        {
            var stable = new HashSet<string>(); // Tập id các kênh "stable"
            foreach (XmlNode ch in doc.SelectNodes("//*[local-name()='channel']")) // Duyệt các khai báo kênh
                if (ch.InnerText.Trim() == "stable" && ch.Attributes["id"] != null) stable.Add(ch.Attributes["id"].Value); // Ghi nhận id kênh stable

            var list = new List<Pkg>(); // Danh sách gói kết quả
            foreach (XmlNode rp in doc.SelectNodes("//*[local-name()='remotePackage']")) // Duyệt từng gói
            {
                var pathAttr = rp.Attributes["path"]; // Thuộc tính path, ví dụ "emulator"
                if (pathAttr == null) continue; // Không có path thì bỏ qua
                var cref = rp.SelectSingleNode("*[local-name()='channelRef']"); // Kênh phát hành của gói
                if (cref != null && stable.Count > 0 && !stable.Contains(cref.Attributes["ref"].Value)) continue; // Bỏ gói không thuộc kênh stable (beta/dev/canary)

                long rev = 0; // Revision quy về một số để so sánh
                var r = rp.SelectSingleNode("*[local-name()='revision']"); // Node revision
                if (r != null) // Có revision thì đọc
                {
                    long ma, mi, mc; // major, minor, micro
                    long.TryParse(Txt(r, "major"), out ma); // Đọc major
                    long.TryParse(Txt(r, "minor"), out mi); // Đọc minor
                    long.TryParse(Txt(r, "micro"), out mc); // Đọc micro
                    rev = ma * 100000000L + mi * 10000L + mc; // Gộp thành một số: major.minor.micro
                }

                foreach (XmlNode ar in rp.SelectNodes(".//*[local-name()='archive']")) // Duyệt các bản tải (mỗi hệ điều hành một bản)
                {
                    string os = Txt(ar, "host-os"), arch = Txt(ar, "host-arch"); // Hệ điều hành và kiến trúc của bản tải
                    if (os != "" && os != "windows") continue; // Chỉ lấy bản Windows (hoặc không ghi OS)
                    if (arch != "" && arch != "x64") continue; // Chỉ lấy bản x64 (hoặc không ghi kiến trúc)
                    var comp = ar.SelectSingleNode("*[local-name()='complete']"); // Gói đầy đủ (không phải gói vá)
                    if (comp == null) continue; // Không có thì bỏ qua
                    string url = Txt(comp, "url"); // Link tải (có thể tương đối)
                    if (!url.StartsWith("http")) url = baseUrl + url; // Link tương đối thì ghép URL gốc
                    long size; // Kích thước gói (byte)
                    long.TryParse(Txt(comp, "size"), out size); // Đọc kích thước
                    // Thêm gói vào danh sách, checksum chuyển chữ thường để so sánh
                    list.Add(new Pkg { PkgPath = pathAttr.Value, Rev = rev, Url = url, Sum = Txt(comp, "checksum").ToLowerInvariant(), Size = size });
                }
            }
            return list; // Trả danh sách gói
        }

        // Chọn gói có path cho trước với revision cao nhất
        static Pkg Best(List<Pkg> list, string path)
        {
            return list.Where(p => p.PkgPath == path).OrderByDescending(p => p.Rev).FirstOrDefault(); // Không có thì trả null
        }

        // Tải gói zip, kiểm tra checksum, giải nén vào thư mục đích
        void InstallZip(Pkg p, string target)
        {
            string marker = Path.Combine(target, ".dsoft_rev"); // File đánh dấu lưu URL bản đã cài
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == p.Url) // Đã cài đúng bản này rồi
            {
                Log.I("Đã có " + p.PkgPath + ", bỏ qua."); // Ghi log
                return; // Không tải lại
            }
            string tmp = Path.Combine(sdk, ".tmp"); // Thư mục tạm
            FileUtil.DeleteDir(tmp); // Dọn thư mục tạm cũ
            Directory.CreateDirectory(tmp); // Tạo thư mục tạm mới

            string zip = Path.Combine(tmp, "pkg.zip"); // File zip tải về
            Log.I("Tải " + p.PkgPath + " <- " + p.Url); // Ghi log
            Download(p.Url, zip); // Tải gói
            if (p.Sum.Length > 0 && !VerifySum(zip, p.Sum)) throw new Exception("Sai checksum: " + p.PkgPath); // Có checksum mà không khớp thì báo lỗi

            Log.I("Giải nén " + p.PkgPath + " ..."); // Ghi log
            string ex = Path.Combine(tmp, "x"); // Thư mục giải nén
            ZipFile.ExtractToDirectory(zip, ex); // Giải nén
            var dirs = Directory.GetDirectories(ex); // Các thư mục con sau giải nén
            string top = dirs.Length == 1 && Directory.GetFiles(ex).Length == 0 ? dirs[0] : ex; // Zip bọc trong một thư mục duy nhất thì lấy thư mục đó

            FileUtil.DeleteDir(target); // Xoá bản cũ ở đích
            Directory.CreateDirectory(Path.GetDirectoryName(target)); // Đảm bảo thư mục cha tồn tại
            Directory.Move(top, target); // Chuyển nội dung đã giải nén vào đích
            File.WriteAllText(marker, p.Url); // Ghi file đánh dấu bản đã cài
            FileUtil.DeleteDir(tmp); // Dọn thư mục tạm
            Log.I("✔ " + p.PkgPath); // Ghi log hoàn tất
        }

        // Kiểm tra checksum file: 64 ký tự hex là SHA256, ngược lại là SHA1
        static bool VerifySum(string file, string expect)
        {
            using (var fs = File.OpenRead(file)) // Mở file để đọc
            {
                HashAlgorithm h = expect.Length == 64 ? (HashAlgorithm)SHA256.Create() : SHA1.Create(); // Chọn thuật toán theo độ dài checksum
                using (h) // Giải phóng bộ băm sau khi dùng
                {
                    string got = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant(); // Tính băm, đổi sang hex chữ thường
                    return got == expect; // So sánh với giá trị mong đợi
                }
            }
        }

        // Tải file từ URL về đĩa, ghi log tiến độ mỗi 10%
        static void Download(string url, string file)
        {
            var req = (HttpWebRequest)WebRequest.Create(url); // Tạo yêu cầu HTTP
            req.UserAgent = "DsoftEmu"; // Đặt User-Agent (GitHub yêu cầu có)
            using (var resp = req.GetResponse()) // Gửi yêu cầu, nhận phản hồi
            using (var rs = resp.GetResponseStream()) // Luồng dữ liệu tải về
            using (var fs = File.Create(file)) // Tạo file đích
            {
                long total = resp.ContentLength, done = 0; // Tổng dung lượng và số byte đã tải
                int lastPct = -10; // Phần trăm đã log lần trước (-10 để log ngay mốc 0%)
                var buf = new byte[1 << 16]; // Bộ đệm 64 KB
                int n; // Số byte đọc được mỗi lần
                while ((n = rs.Read(buf, 0, buf.Length)) > 0) // Đọc đến khi hết dữ liệu
                {
                    fs.Write(buf, 0, n); // Ghi ra file
                    done += n; // Cộng dồn số byte đã tải
                    if (total > 0) // Biết tổng dung lượng thì tính phần trăm
                    {
                        int pct = (int)(done * 100 / total); // Phần trăm hiện tại
                        if (pct >= lastPct + 10) { lastPct = pct; Log.I("   " + pct + "%  (" + done / (1024 * 1024) + "/" + total / (1024 * 1024) + " MB)"); } // Mỗi 10% ghi log một lần
                    }
                }
            }
        }
    }
}
