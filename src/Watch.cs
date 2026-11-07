using System; // DateTime, TimeSpan, Func, Action
using System.Collections.Generic; // Queue
using System.Drawing; // Bitmap, Graphics
using System.Drawing.Imaging; // ImageFormat
using System.IO; // File, Directory, Path, DriveInfo, MemoryStream
using System.Linq; // FirstOrDefault, OrderByDescending
using System.Net; // HttpWebRequest, WebException, ServicePointManager
using System.Net.NetworkInformation; // Ping
using System.Runtime.InteropServices; // DllImport
using System.Text; // Encoding, StringBuilder
using System.Threading; // Thread

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Gửi tin nhắn (kèm ảnh PNG nếu có) tới Discord webhook. Không cần thư viện ngoài.</summary>
    public static class Discord
    {
        /// <summary>Webhook hợp lệ: https://discord.com/api/webhooks/... (hoặc discordapp.com, ptb., canary.)</summary>
        public static bool ValidUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u) || u.Scheme != "https") return false;
            string h = u.Host.ToLowerInvariant();
            bool host = h == "discord.com" || h == "discordapp.com" || h.EndsWith(".discord.com");
            return host && u.AbsolutePath.StartsWith("/api/webhooks/");
        }

        /// <summary>Gửi; trả null nếu thành công, ngược lại trả mô tả lỗi. retryAfterSec > 0 khi bị giới hạn tốc độ (429).</summary>
        public static string Send(string url, string text, byte[] png, out int httpCode, out double retryAfterSec)
        {
            httpCode = 0; retryAfterSec = 0;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // .NET 4.8 mặc định có thể chưa bật TLS 1.2
            if (text != null && text.Length > 1900) text = text.Substring(0, 1900) + "…"; // Discord giới hạn 2000 ký tự
            string b = "----dsoft" + DateTime.Now.Ticks.ToString("x"); // Ranh giới multipart
            var ms = new MemoryStream();
            Action<string> w = t => { var x = Encoding.UTF8.GetBytes(t); ms.Write(x, 0, x.Length); };
            w("--" + b + "\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n");
            w("{\"content\":" + Json(text ?? "") + "}\r\n");
            if (png != null && png.Length > 0)
            {
                w("--" + b + "\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=\"screen.png\"\r\nContent-Type: image/png\r\n\r\n");
                ms.Write(png, 0, png.Length);
                w("\r\n");
            }
            w("--" + b + "--\r\n");

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + b;
                req.UserAgent = "DsoftEmu-Watch";
                req.Timeout = req.ReadWriteTimeout = 20000;
                req.ContentLength = ms.Length;
                using (var rs = req.GetRequestStream()) ms.WriteTo(rs);
                using (var r = (HttpWebResponse)req.GetResponse()) { httpCode = (int)r.StatusCode; return null; }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r == null) return ex.Message; // Lỗi mạng (không tới được Discord)
                httpCode = (int)r.StatusCode;
                string body = "";
                try { using (var sr = new StreamReader(r.GetResponseStream())) body = sr.ReadToEnd(); } catch { }
                if (httpCode == 429) { double.TryParse(r.Headers["Retry-After"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out retryAfterSec); if (retryAfterSec <= 0) retryAfterSec = 5; }
                return "HTTP " + httpCode + " " + (body.Length > 200 ? body.Substring(0, 200) : body);
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Chuỗi JSON an toàn
        static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') { }
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// Giám sát nhẹ + gửi Discord. Một luồng nền, mỗi WatchSec giây kiểm tra:
    ///  - Mạng PC: ping PingHost từ Windows (mất / chậm)
    ///  - Emulator còn phản hồi: lệnh console "ping" (không cần ADB trong Android)
    ///  - Mạng trong Android: ping trong Android - CHỈ khi ADB đang bật
    ///  - RAM trống, ổ đĩa trống
    ///  - Emulator crash / bị đóng / boot quá lâu (theo sự kiện)
    /// Chỉ gửi khi trạng thái ĐỔI (mất -> có lại), không spam.
    /// Chụp màn hình định kỳ mỗi ShotMin phút qua console emulator (không cần ADB); lỗi thì chụp cửa sổ.
    ///
    /// Gửi tách khỏi chụp: chụp xong chỉ bỏ tin (ảnh dạng byte[] trong RAM) vào hàng đợi.
    /// Một luồng gửi riêng (ưu tiên thấp) quét hàng đợi mỗi SendScanSec giây và gửi lần lượt.
    /// Gửi lỗi -> thử lại theo RetryMins (mặc định sau 1, 3, 5 phút), hết lượt thì bỏ tin.
    /// Khi đang phát hiện PC mất mạng thì tạm dừng gửi, không tính lượt thử -> có mạng lại thì gửi bù.
    /// </summary>
    public class Watchdog
    {
        readonly Settings S;
        readonly EmulatorController emu;
        readonly Func<bool> adbReady; // ADB đang dùng được (để ping trong Android)
        volatile bool quit;

        class Msg { public string Text; public byte[] Png; public int Tries; public DateTime NextTry; }
        readonly Queue<Msg> queue = new Queue<Msg>(); // Hàng đợi trong RAM, giữ tối đa MaxQueue tin
        const int MaxQueue = 30;
        readonly object qlk = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false); // Đánh thức luồng gửi sớm (chụp tay)

        // Trạng thái từng mục: số lần lỗi liên tiếp, đang báo lỗi chưa, từ lúc nào
        class Probe { public int Bad, Good; public bool Down; public DateTime Since; }
        readonly Probe pcNet = new Probe(), pcSlow = new Probe(), console = new Probe(), droidNet = new Probe(), ram = new Probe(), disk = new Probe();
        DateTime nextCheck, nextShot = DateTime.MaxValue;
        int shotMinApplied = -1;
        bool crashed, bootSlowSent;

        public Watchdog(Settings s, EmulatorController e, Func<bool> adbReady) { S = s; emu = e; this.adbReady = adbReady; }

        // Cấu hình (đọc trực tiếp settings mỗi lần -> đổi trên UI là áp dụng ngay)
        public string Url { get { return S.Get("DiscordWebhook", "").Trim(); } }
        public bool Enabled { get { return Discord.ValidUrl(Url); } }
        string Name { get { string n = S.Get("DiscordName", "").Trim(); return n.Length > 0 ? n : Environment.MachineName; } }
        bool Alerts { get { return S.GetBool("WatchAlerts", true); } }
        bool AlertShot { get { return S.GetBool("WatchAlertShot", true); } }
        int CheckSec { get { return Math.Max(10, S.GetInt("WatchSec", 30)); } }
        int ShotMin { get { return Math.Max(0, S.GetInt("ShotMin", 30)); } }
        int SendScanSec { get { return Math.Max(2, S.GetInt("SendScanSec", 10)); } }
        // Lịch thử lại (phút), vd "1,3,5" = thử lại 3 lần sau 1, 3, 5 phút; rỗng = không thử lại
        int[] RetryMins
        {
            get
            {
                var r = new List<int>();
                foreach (var p in S.Get("RetryMins", "1,3,5").Split(',', ';', ' '))
                { int v; if (int.TryParse(p.Trim(), out v) && v > 0) r.Add(Math.Min(v, 1440)); }
                return r.ToArray();
            }
        }
        int SlowMs { get { return Math.Max(50, S.GetInt("WatchSlowMs", 300)); } }
        string PingHost { get { return S.Get("PingHost", "8.8.8.8"); } }

        public void Start()
        {
            emu.Exited += OnExit;
            emu.Booted += OnBooted;
            new Thread(Loop) { IsBackground = true, Name = "watchdog", Priority = ThreadPriority.BelowNormal }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "discord-send", Priority = ThreadPriority.BelowNormal }.Start();
        }

        public void Stop() { quit = true; wake.Set(); }

        // Luồng kiểm tra + chụp: thức mỗi giây chỉ để so mốc thời gian (gần như không tốn CPU)
        void Loop()
        {
            while (!quit)
            {
                try { Tick(); } catch (Exception ex) { Log.I("Giám sát lỗi: " + ex.Message); }
                Thread.Sleep(1000);
            }
        }

        // Luồng gửi: ngủ SendScanSec giây (hoặc tới khi được đánh thức) rồi quét hàng đợi
        void SendLoop()
        {
            while (!quit)
            {
                wake.WaitOne(SendScanSec * 1000);
                if (quit) return;
                try { Flush(); } catch (Exception ex) { Log.I("Gửi Discord lỗi: " + ex.Message); }
            }
        }

        void Tick()
        {
            if (!Enabled) return;
            var now = DateTime.Now;

            // Đổi chu kỳ chụp trên UI -> tính lại mốc chụp
            if (ShotMin != shotMinApplied) { shotMinApplied = ShotMin; nextShot = ShotMin > 0 ? now.AddMinutes(ShotMin) : DateTime.MaxValue; }
            if (ShotMin > 0 && now >= nextShot)
            {
                nextShot = now.AddMinutes(ShotMin);
                if (emu.IsBooted) SendShot("Ảnh định kỳ (mỗi " + ShotMin + " phút)", false);
            }

            if (now < nextCheck) return;
            nextCheck = now.AddSeconds(CheckSec);
            // Chỉ giám sát khi giả lập đang chạy; ngừng giám sát thì bỏ cờ "PC mất mạng" để luồng gửi không bị chặn mãi
            if (!Alerts || !emu.Running) { if (pcNet.Down) Reset(pcNet); return; }
            CheckPcNet();
            CheckConsole();
            CheckDroidNet();
            CheckHost();
            // Boot quá lâu
            if (!emu.IsBooted && !bootSlowSent && (now - emu.StartedAt).TotalMinutes > 6)
            {
                bootSlowSent = true;
                Alert("🟠", "Android chưa boot xong sau 6 phút (xem logs\\emulator.log)", true, false);
            }
        }

        // ---------- Các phép kiểm tra ----------

        void CheckPcNet()
        {
            long ms = PingHostMs();
            Track(pcNet, ms < 0, 2, 1,
                () => Alert("🔴", "PC mất mạng (ping " + PingHost + " không phản hồi). Tin sẽ gửi bù khi có mạng lại.", false, false),
                d => Alert("🟢", "PC có mạng lại · mất " + Dur(d), false, false));
            if (ms >= 0)
                Track(pcSlow, ms >= SlowMs, 3, 3,
                    () => Alert("🟠", "Mạng PC chậm: ping " + ms + " ms (ngưỡng " + SlowMs + " ms)", false, false),
                    d => Alert("🟢", "Mạng PC hết chậm: ping " + ms + " ms · kéo dài " + Dur(d), false, false));
        }

        void CheckConsole()
        {
            string r = emu.EmuConsole("ping", 6000) ?? "";
            Track(console, !r.Contains("OK"), 2, 1,
                () => Alert("🔴", "Emulator không phản hồi (có thể bị treo)", true, true),
                d => Alert("🟢", "Emulator phản hồi lại · treo " + Dur(d), false, false));
        }

        void CheckDroidNet()
        {
            // Chỉ khi ADB bật, Android đã boot và PC đang có mạng (PC mất mạng thì Android mất theo, không báo trùng)
            if (!adbReady() || !emu.IsBooted || pcNet.Down || console.Down) { if (droidNet.Down && !adbReady()) Reset(droidNet); return; }
            string r = emu.NewAdb().Shell("\"ping -c1 -W2 " + PingHost + " >/dev/null && echo NET_OK\"", 8000) ?? "";
            Track(droidNet, !r.Contains("NET_OK"), 2, 1,
                () => Alert("🔴", "Android mất mạng (PC vẫn có mạng). Thử ⇄ Làm mới mạng.", true, false),
                d => Alert("🟢", "Android có mạng lại · mất " + Dur(d), false, false));
        }

        void CheckHost()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m))
            {
                long freeMB = (long)(m.ullAvailPhys / 1048576);
                Track(ram, freeMB < 700, 2, 2,
                    () => Alert("🟠", "RAM máy sắp hết: còn " + freeMB + " MB trống (dùng " + m.dwMemoryLoad + "%). Giả lập có thể giật/crash.", false, false),
                    d => Alert("🟢", "RAM ổn lại: còn " + freeMB + " MB trống", false, false));
            }
            try
            {
                var drv = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(emu.AvdHome)));
                double gb = drv.AvailableFreeSpace / 1073741824.0;
                Track(disk, gb < 2, 1, 1,
                    () => Alert("🟠", "Ổ " + drv.Name + " sắp đầy: còn " + gb.ToString("0.0") + " GB. Lưu trạng thái / Quick Boot có thể lỗi.", false, false),
                    d => Alert("🟢", "Ổ " + drv.Name + " đã có chỗ trống: " + gb.ToString("0.0") + " GB", false, false));
            }
            catch { }
        }

        // Cập nhật trạng thái một mục: lỗi liên tiếp >= badN -> báo; tốt liên tiếp >= goodN -> báo hồi phục
        static void Track(Probe p, bool bad, int badN, int goodN, Action onDown, Action<TimeSpan> onUp)
        {
            if (bad) { p.Good = 0; if (++p.Bad == 1 && !p.Down) p.Since = DateTime.Now; if (!p.Down && p.Bad >= badN) { p.Down = true; onDown(); } }
            else { p.Bad = 0; if (p.Down && ++p.Good >= goodN) { p.Down = false; p.Good = 0; onUp(DateTime.Now - p.Since); } }
        }

        static void Reset(Probe p) { p.Bad = p.Good = 0; p.Down = false; }

        // ---------- Sự kiện emulator ----------

        void OnExit(int code, bool unexpected)
        {
            foreach (var p in new[] { console, droidNet }) Reset(p);
            if (!unexpected || !Enabled || !Alerts) return;
            bool restart = code != 0 && S.GetBool("AutoRestart", true);
            crashed = code != 0;
            if (code == 0) Alert("⚪", "Emulator đã bị đóng (không phải từ nút Dừng của app)", false, false);
            else Alert("🔴", "Emulator crash (mã " + code + ")" + (restart ? " · đang tự khởi động lại" : ""), false, false);
        }

        void OnBooted()
        {
            bootSlowSent = false;
            if (ShotMin > 0) nextShot = DateTime.Now.AddMinutes(ShotMin);
            if (!Enabled || !Alerts) return;
            Alert("🟢", crashed ? "Android đã chạy lại sau crash" : "Android đã sẵn sàng", false, false);
            crashed = false;
        }

        // ---------- Gửi ----------

        // Thêm tin cảnh báo vào hàng đợi; shot = kèm ảnh (nếu bật tuỳ chọn), windowOnly = emulator treo -> chụp cửa sổ
        void Alert(string icon, string text, bool shot, bool windowOnly)
        {
            Log.I("Giám sát: " + text);
            byte[] png = shot && AlertShot && emu.Running ? TakeShot(windowOnly) : null;
            Enqueue(icon + " **" + Name + "** · " + DateTime.Now.ToString("HH:mm:ss dd/MM") + "\n" + text, png, false);
        }

        /// <summary>Chụp rồi bỏ vào hàng đợi (nút trên UI / định kỳ). Chạy ở luồng nền. now = đánh thức luồng gửi ngay.</summary>
        public bool SendShot(string caption, bool now)
        {
            byte[] png = TakeShot(false);
            if (png == null) { Log.I("Không chụp được màn hình Android."); return false; }
            Enqueue("📷 **" + Name + "** · " + DateTime.Now.ToString("HH:mm:ss dd/MM") + "\n" + caption, png, now);
            return true;
        }

        /// <summary>Gửi thử trực tiếp (không qua hàng đợi), trả null nếu thành công.</summary>
        public string SendTest()
        {
            if (!Enabled) return "Webhook không hợp lệ (cần dạng https://discord.com/api/webhooks/...)";
            byte[] png = emu.IsBooted ? TakeShot(false) : null;
            int code; double ra;
            return Discord.Send(Url, "✅ **" + Name + "** · " + DateTime.Now.ToString("HH:mm:ss dd/MM") + "\nGửi thử từ DsoftEmu" + (png == null ? " (giả lập chưa chạy nên không kèm ảnh)" : ""), png, out code, out ra);
        }

        /// <summary>Số tin đang chờ gửi.</summary>
        public int Pending { get { lock (qlk) return queue.Count; } }

        void Enqueue(string text, byte[] png, bool now)
        {
            lock (qlk)
            {
                queue.Enqueue(new Msg { Text = text, Png = png, NextTry = DateTime.MinValue });
                while (queue.Count > MaxQueue) queue.Dequeue(); // Đầy thì bỏ tin cũ nhất (ảnh trong RAM tự được thu hồi)
            }
            if (now) wake.Set();
        }

        // Gửi các tin tới hạn theo thứ tự (giữ đúng thứ tự mất -> có lại).
        void Flush()
        {
            if (!Enabled) return;
            if (pcNet.Down) return; // Đang biết PC mất mạng: chờ, không tốn lượt thử
            while (!quit)
            {
                Msg m;
                lock (qlk) { if (queue.Count == 0) return; m = queue.Peek(); }
                if (DateTime.Now < m.NextTry) return; // Tin đầu chưa tới giờ thử lại
                int code; double ra;
                string err = Discord.Send(Url, m.Text, m.Png, out code, out ra);
                bool drop = false;
                if (err == null) drop = true;
                else if (code == 429) { m.NextTry = DateTime.Now.AddSeconds(Math.Max(1, ra)); return; } // Bị giới hạn tốc độ: chờ đúng Retry-After, không tính lượt
                else if (code >= 400 && code < 500) { Log.I("Discord từ chối tin (" + err + "), bỏ tin."); drop = true; } // Webhook sai/bị xoá: thử lại cũng vô ích
                else
                {
                    var plan = RetryMins;
                    if (m.Tries >= plan.Length) { Log.I("Bỏ tin sau " + m.Tries + " lần thử lại: " + err); drop = true; }
                    else
                    {
                        m.NextTry = DateTime.Now.AddMinutes(plan[m.Tries]);
                        m.Tries++;
                        Log.I("Chưa gửi được Discord (" + err + "), thử lại lần " + m.Tries + "/" + plan.Length + " sau " + plan[m.Tries - 1] + " phút.");
                        return;
                    }
                }
                if (drop) lock (qlk) { if (queue.Count > 0 && queue.Peek() == m) queue.Dequeue(); }
                Thread.Sleep(500); // Giãn các tin liên tiếp (gửi bù) để tránh 429
            }
        }

        // ---------- Chụp màn hình ----------

        /// <summary>Chụp màn hình Android, trả PNG trong RAM (file tạm xoá ngay); null nếu không chụp được.</summary>
        public byte[] TakeShot(bool windowOnly)
        {
            string dir = Path.Combine(Path.GetTempPath(), "dsoft_shot", DateTime.Now.Ticks.ToString());
            try
            {
                Directory.CreateDirectory(dir);
                // Cách 1: console emulator -> ảnh đúng độ phân giải gốc, không cần ADB trong Android
                // (đường dẫn có dấu cách thì console tách sai tham số -> bỏ qua, dùng cách 2)
                if (!windowOnly && !dir.Contains(" "))
                {
                    for (int i = 0; i < 2; i++)
                    {
                        emu.EmuConsole("screenrecord screenshot " + dir, 8000);
                        for (int k = 0; k < 15; k++)
                        {
                            string f = Directory.GetFiles(dir, "*.png").FirstOrDefault();
                            if (f != null && new FileInfo(f).Length > 0)
                            {
                                Thread.Sleep(200); // Chờ emulator ghi xong file
                                return File.ReadAllBytes(f);
                            }
                            Thread.Sleep(200);
                        }
                    }
                }
                // Cách 2: chụp cửa sổ emulator, mã hoá PNG thẳng vào RAM
                using (var bmp = CaptureWindow())
                {
                    if (bmp == null) return null;
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
                }
            }
            catch { return null; }
            finally { try { Directory.Delete(dir, true); } catch { } } // Không để lại file tạm
        }

        // Tìm cửa sổ "Android Emulator - <avd>:<port>" và chụp nội dung
        Bitmap CaptureWindow()
        {
            string suffix = ":" + emu.Port;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                string t = sb.ToString();
                if (IsWindowVisible(h) && t.StartsWith("Android Emulator - ") && t.EndsWith(suffix)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero || IsIconic(found)) return null; // Không thấy hoặc đang thu nhỏ (ảnh sẽ đen)
            RECT r;
            if (!GetClientRect(found, out r) || r.R - r.L < 10 || r.B - r.T < 10) return null;
            var bmp = new Bitmap(r.R - r.L, r.B - r.T);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { PrintWindow(found, hdc, 0x1 | 0x2); } // PW_CLIENTONLY | PW_RENDERFULLCONTENT (cần cho cửa sổ vẽ bằng GPU)
                finally { g.ReleaseHdc(hdc); }
            }
            return bmp;
        }

        // ---------- Tiện ích ----------

        long PingHostMs()
        {
            for (int i = 0; i < 2; i++) // Thử 2 gói, 1 gói rớt không tính là mất mạng
            {
                try
                {
                    using (var p = new Ping())
                    {
                        var r = p.Send(PingHost, 2000);
                        if (r.Status == IPStatus.Success) return r.RoundtripTime;
                    }
                }
                catch { }
            }
            return -1;
        }

        static string Dur(TimeSpan d)
        {
            if (d.TotalSeconds < 60) return (int)d.TotalSeconds + " giây";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " phút " + d.Seconds + " giây";
            return (int)d.TotalHours + " giờ " + d.Minutes + " phút";
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        struct RECT { public int L, T, R, B; }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    }
}
