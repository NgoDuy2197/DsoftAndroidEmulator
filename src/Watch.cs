using System; // DateTime, TimeSpan, Func, Action
using System.Collections.Generic; // List, Dictionary
using System.Drawing; // Bitmap, Graphics
using System.Drawing.Drawing2D; // InterpolationMode
using System.Drawing.Imaging; // ImageFormat, ImageCodecInfo
using System.IO; // File, Directory, Path, DriveInfo, MemoryStream
using System.Linq; // FirstOrDefault, Any
using System.Net; // HttpWebRequest, WebException, ServicePointManager
using System.Net.NetworkInformation; // Ping
using System.Runtime.InteropServices; // DllImport
using System.Text; // Encoding, StringBuilder
using System.Threading; // Thread

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Gửi tin nhắn (kèm 1 ảnh nếu có) tới Discord webhook. Không cần thư viện ngoài.</summary>
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

        /// <summary>Gửi; trả null nếu thành công, ngược lại trả mô tả lỗi. retryAfterSec > 0 khi bị giới hạn tốc độ (429).
        /// fileName quyết định kiểu ảnh: .png hoặc .jpg</summary>
        public static string Send(string url, string text, byte[] img, string fileName, out int httpCode, out double retryAfterSec)
        {
            httpCode = 0; retryAfterSec = 0;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; // .NET 4.8 mặc định có thể chưa bật TLS 1.2
            if (text != null && text.Length > 1900) text = text.Substring(0, 1900) + "…"; // Discord giới hạn 2000 ký tự
            string b = "----dsoft" + DateTime.Now.Ticks.ToString("x"); // Ranh giới multipart
            var ms = new MemoryStream();
            Action<string> w = t => { var x = Encoding.UTF8.GetBytes(t); ms.Write(x, 0, x.Length); };
            w("--" + b + "\r\nContent-Disposition: form-data; name=\"payload_json\"\r\nContent-Type: application/json\r\n\r\n");
            w("{\"content\":" + Json(text ?? "") + "}\r\n");
            if (img != null && img.Length > 0)
            {
                string fn = fileName ?? "screen.png";
                string ct = fn.EndsWith(".jpg") ? "image/jpeg" : "image/png";
                w("--" + b + "\r\nContent-Disposition: form-data; name=\"files[0]\"; filename=\"" + fn + "\"\r\nContent-Type: " + ct + "\r\n\r\n");
                ms.Write(img, 0, img.Length);
                w("\r\n");
            }
            w("--" + b + "--\r\n");

            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + b;
                req.UserAgent = "DsoftEmu-Watch";
                req.Timeout = req.ReadWriteTimeout = 30000;
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
    /// Giám sát nhẹ + gửi Discord cho TẤT CẢ máy. Mỗi WatchSec giây kiểm tra:
    ///  - Chung: mạng PC (ping từ Windows, mất / chậm), RAM trống, ổ đĩa trống
    ///  - Từng máy đang chạy: emulator còn phản hồi (lệnh console "ping", không cần ADB),
    ///    mạng trong Android (CHỈ khi máy đó bật ADB), boot quá lâu; crash / bị đóng (theo sự kiện)
    /// Chỉ báo khi trạng thái ĐỔI (mất -> có lại), không spam.
    ///
    /// Gửi tách khỏi chụp: chụp xong chỉ bỏ tin (ảnh byte[] trong RAM) vào hàng đợi.
    /// Luồng gửi (ưu tiên thấp) quét mỗi SendScanSec giây; mọi tin đã tới hạn được GỘP thành 1 tin Discord,
    /// ảnh của các tin/máy được GHÉP thành 1 ảnh lưới có nhãn "Máy N".
    /// Gửi lỗi -> thử lại theo RetryMins (mặc định 1, 3, 5 phút), hết lượt thì bỏ.
    /// Đang biết PC mất mạng thì tạm dừng gửi, không tính lượt thử -> có mạng lại thì gửi bù.
    /// </summary>
    public class Watchdog
    {
        readonly Settings S;
        readonly Func<IList<EmulatorController>> machines; // Danh sách máy hiện tại
        readonly Func<EmulatorController, bool> adbReady; // Máy đó đang dùng được ADB (để ping trong Android)
        volatile bool quit;

        class Shot { public string Label; public byte[] Png; }
        class Msg
        {
            public DateTime At; public string Icon, Text; // Một dòng tin
            public List<Shot> Shots = new List<Shot>();   // Ảnh kèm (mỗi máy một ảnh)
            public int Tries; public DateTime NextTry;    // Lịch thử lại
            public string Body; public byte[] Img; public string ImgName; // Đã gộp sẵn (khi gửi lỗi giữ nguyên để thử lại)
        }
        readonly List<Msg> queue = new List<Msg>(); // Hàng đợi trong RAM
        const int MaxQueue = 30;
        readonly object qlk = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false); // Đánh thức luồng gửi sớm (chụp tay)

        // Trạng thái từng mục: số lần lỗi liên tiếp, đang báo lỗi chưa, từ lúc nào
        class Probe { public int Bad, Good; public bool Down; public DateTime Since; }
        readonly Probe pcNet = new Probe(), pcSlow = new Probe(), ram = new Probe(), disk = new Probe();
        class Inst { public Probe Console = new Probe(), DroidNet = new Probe(); public bool Crashed, BootSlowSent; }
        readonly Dictionary<EmulatorController, Inst> inst = new Dictionary<EmulatorController, Inst>();
        DateTime nextCheck, nextShot = DateTime.MaxValue;
        int shotMinApplied = -1;

        public Watchdog(Settings s, Func<IList<EmulatorController>> machines, Func<EmulatorController, bool> adbReady)
        { S = s; this.machines = machines; this.adbReady = adbReady; }

        // Cấu hình (đọc trực tiếp settings mỗi lần -> đổi trên UI là áp dụng ngay)
        public string Url { get { return S.Get("DiscordWebhook", "").Trim(); } }
        public bool Enabled { get { return Discord.ValidUrl(Url); } }
        string Name { get { string n = S.Get("DiscordName", "").Trim(); return n.Length > 0 ? n : Environment.MachineName; } }
        bool Alerts { get { return S.GetBool("WatchAlerts", true); } }
        bool AlertShot { get { return S.GetBool("WatchAlertShot", true); } }
        int CheckSec { get { return Math.Max(10, S.GetInt("WatchSec", 30)); } }
        int ShotMin { get { return Math.Max(0, S.GetInt("ShotMin", 30)); } }
        int SendScanSec { get { return Math.Max(2, S.GetInt("SendScanSec", 10)); } }
        int SlowMs { get { return Math.Max(50, S.GetInt("WatchSlowMs", 300)); } }
        string PingHost { get { return S.Get("PingHost", "8.8.8.8"); } }
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
        IList<EmulatorController> All { get { return machines().ToList(); } }
        bool Multi { get { return EmulatorController.Count > 1; } }
        string Who(EmulatorController e) { return Multi ? e.Label + ": " : ""; } // Tiền tố tên máy khi có nhiều máy

        Inst St(EmulatorController e)
        {
            lock (inst) { Inst s; if (!inst.TryGetValue(e, out s)) inst[e] = s = new Inst(); return s; }
        }

        /// <summary>Theo dõi sự kiện của một máy (gọi khi tạo máy).</summary>
        public void Attach(EmulatorController e)
        {
            e.Exited += (code, unexpected) => OnExit(e, code, unexpected);
            e.Booted += () => OnBooted(e);
        }

        public void Start()
        {
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
            var all = All;

            // Đổi chu kỳ chụp trên UI -> tính lại mốc chụp
            if (ShotMin != shotMinApplied) { shotMinApplied = ShotMin; nextShot = ShotMin > 0 ? now.AddMinutes(ShotMin) : DateTime.MaxValue; }
            if (ShotMin > 0 && now >= nextShot)
            {
                nextShot = now.AddMinutes(ShotMin);
                if (all.Any(e => e.IsBooted)) SendShot("Ảnh định kỳ (mỗi " + ShotMin + " phút)", false);
            }

            if (now < nextCheck) return;
            nextCheck = now.AddSeconds(CheckSec);
            var running = all.Where(e => e.Running).ToList();
            // Chỉ giám sát khi có máy đang chạy; ngừng giám sát thì bỏ cờ "PC mất mạng" để luồng gửi không bị chặn mãi
            if (!Alerts || running.Count == 0) { if (pcNet.Down) Reset(pcNet); return; }
            CheckPcNet();
            CheckHost();
            foreach (var e in running)
            {
                CheckConsole(e);
                CheckDroidNet(e);
                var s = St(e);
                if (!e.IsBooted && !s.BootSlowSent && (now - e.StartedAt).TotalMinutes > 6)
                {
                    s.BootSlowSent = true;
                    Alert("🟠", Who(e) + "Android chưa boot xong sau 6 phút (xem logs)", e, false);
                }
            }
        }

        // ---------- Các phép kiểm tra ----------

        void CheckPcNet()
        {
            long ms = PingHostMs();
            Track(pcNet, ms < 0, 2, 1,
                () => Alert("🔴", "PC mất mạng (ping " + PingHost + " không phản hồi). Tin sẽ gửi bù khi có mạng lại.", null, false),
                d => Alert("🟢", "PC có mạng lại · mất " + Dur(d), null, false));
            if (ms >= 0)
                Track(pcSlow, ms >= SlowMs, 3, 3,
                    () => Alert("🟠", "Mạng PC chậm: ping " + ms + " ms (ngưỡng " + SlowMs + " ms)", null, false),
                    d => Alert("🟢", "Mạng PC hết chậm: ping " + ms + " ms · kéo dài " + Dur(d), null, false));
        }

        void CheckConsole(EmulatorController e)
        {
            string r = e.EmuConsole("ping", 6000) ?? "";
            Track(St(e).Console, !r.Contains("OK"), 2, 1,
                () => Alert("🔴", Who(e) + "Emulator không phản hồi (có thể bị treo)", e, true),
                d => Alert("🟢", Who(e) + "Emulator phản hồi lại · treo " + Dur(d), null, false));
        }

        void CheckDroidNet(EmulatorController e)
        {
            var s = St(e);
            // Chỉ khi máy bật ADB, Android đã boot và PC đang có mạng (PC mất mạng thì Android mất theo, không báo trùng)
            if (!adbReady(e) || !e.IsBooted || pcNet.Down || s.Console.Down) { if (s.DroidNet.Down && !adbReady(e)) Reset(s.DroidNet); return; }
            string r = e.NewAdb().Shell("\"ping -c1 -W2 " + PingHost + " >/dev/null && echo NET_OK\"", 8000) ?? "";
            Track(s.DroidNet, !r.Contains("NET_OK"), 2, 1,
                () => Alert("🔴", Who(e) + "Android mất mạng (PC vẫn có mạng). Thử ⇄ Làm mới mạng.", e, false),
                d => Alert("🟢", Who(e) + "Android có mạng lại · mất " + Dur(d), null, false));
        }

        void CheckHost()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
            if (GlobalMemoryStatusEx(ref m))
            {
                long freeMB = (long)(m.ullAvailPhys / 1048576);
                Track(ram, freeMB < 700, 2, 2,
                    () => Alert("🟠", "RAM máy sắp hết: còn " + freeMB + " MB trống (dùng " + m.dwMemoryLoad + "%). Giả lập có thể giật/crash.", null, false),
                    d => Alert("🟢", "RAM ổn lại: còn " + freeMB + " MB trống", null, false));
            }
            try
            {
                var any = All.FirstOrDefault();
                if (any == null) return;
                var drv = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(any.AvdHome)));
                double gb = drv.AvailableFreeSpace / 1073741824.0;
                Track(disk, gb < 2, 1, 1,
                    () => Alert("🟠", "Ổ " + drv.Name + " sắp đầy: còn " + gb.ToString("0.0") + " GB. Lưu trạng thái / Quick Boot có thể lỗi.", null, false),
                    d => Alert("🟢", "Ổ " + drv.Name + " đã có chỗ trống: " + gb.ToString("0.0") + " GB", null, false));
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

        void OnExit(EmulatorController e, int code, bool unexpected)
        {
            var s = St(e);
            Reset(s.Console); Reset(s.DroidNet);
            if (!unexpected || !Enabled || !Alerts) return;
            bool restart = code != 0 && S.GetBool("AutoRestart", true);
            s.Crashed = code != 0;
            if (code == 0) Alert("⚪", Who(e) + "Emulator đã bị đóng (không phải từ nút Dừng của app)", null, false);
            else Alert("🔴", Who(e) + "Emulator crash (mã " + code + ")" + (restart ? " · đang tự khởi động lại" : ""), null, false);
        }

        void OnBooted(EmulatorController e)
        {
            var s = St(e);
            s.BootSlowSent = false;
            if (!Enabled || !Alerts) return;
            Alert("🟢", Who(e) + (s.Crashed ? "Android đã chạy lại sau crash" : "Android đã sẵn sàng"), null, false);
            s.Crashed = false;
        }

        // ---------- Hàng đợi + gửi ----------

        // Thêm tin cảnh báo; shotOf = máy cần chụp kèm (nếu bật tuỳ chọn), windowOnly = emulator treo -> chụp cửa sổ
        void Alert(string icon, string text, EmulatorController shotOf, bool windowOnly)
        {
            Log.I("Giám sát: " + text);
            var m = new Msg { At = DateTime.Now, Icon = icon, Text = text };
            if (shotOf != null && AlertShot && shotOf.Running)
            {
                var png = TakeShot(shotOf, windowOnly);
                if (png != null) m.Shots.Add(new Shot { Label = shotOf.Label, Png = png });
            }
            Enqueue(m, false);
        }

        /// <summary>Chụp mọi máy đã sẵn sàng rồi bỏ 1 tin vào hàng đợi (nút trên UI / định kỳ). Chạy ở luồng nền.
        /// now = đánh thức luồng gửi ngay.</summary>
        public bool SendShot(string caption, bool now)
        {
            var m = new Msg { At = DateTime.Now, Icon = "📷", Text = caption };
            foreach (var e in All.Where(x => x.IsBooted))
            {
                var png = TakeShot(e, false);
                if (png != null) m.Shots.Add(new Shot { Label = e.Label, Png = png });
                else Log.I("Không chụp được màn hình " + e.Label + ".");
            }
            if (m.Shots.Count == 0) return false;
            Enqueue(m, now);
            return true;
        }

        /// <summary>Gửi thử trực tiếp (không qua hàng đợi), trả null nếu thành công.</summary>
        public string SendTest()
        {
            if (!Enabled) return "Webhook không hợp lệ (cần dạng https://discord.com/api/webhooks/...)";
            var m = new Msg { At = DateTime.Now, Icon = "✅", Text = "Gửi thử từ DsoftEmu" };
            foreach (var e in All.Where(x => x.IsBooted))
            {
                var png = TakeShot(e, false);
                if (png != null) m.Shots.Add(new Shot { Label = e.Label, Png = png });
            }
            if (m.Shots.Count == 0) m.Text += " (chưa có máy nào chạy nên không kèm ảnh)";
            Build(new List<Msg> { m }, m);
            int code; double ra;
            return Discord.Send(Url, m.Body, m.Img, m.ImgName, out code, out ra);
        }

        /// <summary>Số tin đang chờ gửi.</summary>
        public int Pending { get { lock (qlk) return queue.Count; } }

        void Enqueue(Msg m, bool now)
        {
            lock (qlk)
            {
                queue.Add(m);
                while (queue.Count > MaxQueue) queue.RemoveAt(0); // Đầy thì bỏ tin cũ nhất (ảnh trong RAM tự được thu hồi)
            }
            if (now) wake.Set();
        }

        // Gộp các tin vào "into": 1 nội dung + 1 ảnh (ghép lưới nếu nhiều ảnh)
        void Build(List<Msg> parts, Msg into)
        {
            var sb = new StringBuilder();
            sb.Append("**").Append(Name).Append("** · ").Append(parts[0].At.ToString("HH:mm:ss dd/MM"));
            foreach (var p in parts)
            {
                sb.Append('\n').Append(p.Icon).Append(' ');
                if (parts.Count > 1) sb.Append('`').Append(p.At.ToString("HH:mm:ss")).Append("` ");
                sb.Append(p.Text);
            }
            var shots = parts.SelectMany(p => p.Shots).ToList();
            into.Body = sb.ToString();
            if (shots.Count == 1 && !Multi) { into.Img = shots[0].Png; into.ImgName = "screen.png"; } // 1 máy: ảnh gốc PNG
            else if (shots.Count > 0) { into.Img = Compose(shots); into.ImgName = into.Img != null ? "screens.jpg" : null; }
            into.Shots = new List<Shot>(); // Đã gộp vào Img -> giải phóng ảnh lẻ
        }

        // Gửi: lấy mọi tin đã tới hạn ở đầu hàng đợi, gộp thành 1 tin; lỗi thì giữ bản gộp để thử lại.
        void Flush()
        {
            if (!Enabled) return;
            while (!quit)
            {
                if (pcNet.Down) return; // Đang biết PC mất mạng: chờ, không tốn lượt thử
                Msg m;
                lock (qlk)
                {
                    var now = DateTime.Now;
                    if (queue.Count == 0 || queue[0].NextTry > now) return; // Tin đầu chưa tới giờ (giữ thứ tự)
                    if (queue[0].Body != null) m = queue[0]; // Bản đã gộp từ lần trước (đang thử lại) -> gửi y nguyên
                    else
                    {
                        // Gộp tối đa 15 tin mới liên tiếp (giữ đúng thứ tự mất -> có lại), nội dung không quá ~1700 ký tự
                        int n = 0, len = 0;
                        while (n < queue.Count && n < 15 && queue[n].Body == null && (len += queue[n].Text.Length + 20) < 1700) n++;
                        if (n == 0) n = 1;
                        var parts = queue.GetRange(0, n);
                        m = new Msg { At = parts[0].At };
                        Build(parts, m);
                        queue.RemoveRange(0, n);
                        queue.Insert(0, m);
                    }
                }
                int code; double ra;
                string err = Discord.Send(Url, m.Body, m.Img, m.ImgName, out code, out ra);
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
                if (drop) lock (qlk) queue.Remove(m);
                Thread.Sleep(1000); // Còn tin tới hạn (vd vượt giới hạn gộp) thì cách 1 giây mới gửi tiếp, tránh 429
            }
        }

        // ---------- Ảnh ----------

        /// <summary>Ghép nhiều ảnh thành lưới (1 hàng nếu ≤ 3 ảnh), mỗi ô có nhãn tên máy. Trả JPEG (nhẹ hơn PNG nhiều).</summary>
        static byte[] Compose(List<Shot> shots)
        {
            var bmps = new List<Bitmap>();
            try
            {
                foreach (var s in shots) using (var ms = new MemoryStream(s.Png)) bmps.Add(new Bitmap(ms));
                int n = bmps.Count;
                int cols = n <= 3 ? n : (n <= 4 ? 2 : 3), rows = (n + cols - 1) / cols;
                // Ô rộng tối đa 960px (đủ đọc trên Discord, ảnh tổng không quá lớn)
                int tw = Math.Min(960, bmps.Max(b => b.Width));
                int th = (int)Math.Round(tw * (double)bmps[0].Height / bmps[0].Width);
                const int gap = 6;
                using (var outBmp = new Bitmap(cols * tw + (cols - 1) * gap, rows * th + (rows - 1) * gap))
                using (var g = Graphics.FromImage(outBmp))
                using (var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                {
                    g.Clear(Color.FromArgb(30, 31, 36));
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    for (int i = 0; i < n; i++)
                    {
                        int x = (i % cols) * (tw + gap), y = (i / cols) * (th + gap);
                        var b = bmps[i];
                        double k = Math.Min((double)tw / b.Width, (double)th / b.Height); // Giữ tỉ lệ (máy ngang/dọc khác nhau)
                        int w = (int)(b.Width * k), h = (int)(b.Height * k);
                        g.DrawImage(b, x + (tw - w) / 2, y + (th - h) / 2, w, h);
                        var sz = g.MeasureString(shots[i].Label, font);
                        g.FillRectangle(bg, x + 8, y + 8, sz.Width + 12, sz.Height + 4);
                        g.DrawString(shots[i].Label, font, Brushes.White, x + 14, y + 10);
                    }
                    var jpg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    using (var ms = new MemoryStream()) { outBmp.Save(ms, jpg, ep); return ms.ToArray(); }
                }
            }
            catch (Exception ex) { Log.I("Ghép ảnh lỗi: " + ex.Message); return shots.Count > 0 ? shots[0].Png : null; }
            finally { foreach (var b in bmps) b.Dispose(); }
        }

        /// <summary>Chụp màn hình Android của 1 máy, trả PNG trong RAM (file tạm xoá ngay); null nếu không chụp được.</summary>
        public byte[] TakeShot(EmulatorController e, bool windowOnly)
        {
            string dir = Path.Combine(Path.GetTempPath(), "dsoft_shot", DateTime.Now.Ticks.ToString() + "_" + e.Index);
            try
            {
                Directory.CreateDirectory(dir);
                // Cách 1: console emulator -> ảnh đúng độ phân giải gốc, không cần ADB trong Android
                // (đường dẫn có dấu cách thì console tách sai tham số -> bỏ qua, dùng cách 2)
                if (!windowOnly && !dir.Contains(" "))
                {
                    for (int i = 0; i < 2; i++)
                    {
                        e.EmuConsole("screenrecord screenshot " + dir, 8000);
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
                using (var bmp = CaptureWindow(e.Port))
                {
                    if (bmp == null) return null;
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); return ms.ToArray(); }
                }
            }
            catch { return null; }
            finally { try { Directory.Delete(dir, true); } catch { } } // Không để lại file tạm
        }

        /// <summary>Tìm cửa sổ emulator theo cổng ("Android Emulator - &lt;avd&gt;:&lt;port&gt;"), IntPtr.Zero nếu không thấy.</summary>
        public static IntPtr FindEmuWindow(int port)
        {
            string suffix = ":" + port;
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, l) =>
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                string t = sb.ToString();
                if (IsWindowVisible(h) && t.StartsWith("Android Emulator - ") && t.EndsWith(suffix)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Chụp nội dung cửa sổ emulator
        static Bitmap CaptureWindow(int port)
        {
            IntPtr found = FindEmuWindow(port);
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
