using System; // Kiểu cơ bản: String, Exception, Action...
using System.Collections.Generic; // List, Dictionary, KeyValuePair
using System.Globalization; // CultureInfo, NumberStyles để parse số
using System.Threading; // Thread, CancellationToken, Interlocked

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    /// <summary>
    /// Kịch bản auto click. Mỗi dòng một lệnh, toạ độ chuẩn hoá 0..1, thời gian ms:
    ///   loop  N                       (0 = lặp vô hạn)
    ///   tap   x y [r]
    ///   dtap  x y [r]                 (double tap)
    ///   hold  x y minMs maxMs [r]
    ///   swipe x1 y1 x2 y2 minMs maxMs
    ///   wait  minMs [maxMs]
    ///   break pct minMs maxMs         (cuối mỗi vòng, pct% cơ hội nghỉ dài như người thật)
    /// </summary>
    public class Macro // Một kịch bản đã parse: tên, số vòng, cấu hình nghỉ, danh sách bước
    {
        public string Name; // Tên macro (dùng làm khoá khi chạy/dừng)
        public int Loop = 1; // Số vòng lặp, mặc định 1; 0 = vô hạn
        public double BreakPct; // Xác suất (%) nghỉ dài cuối mỗi vòng
        public int BreakMin, BreakMax; // Thời gian nghỉ dài tối thiểu/tối đa (ms)
        // Danh sách bước: Key = tên lệnh (tap/dtap/hold/swipe/wait), Value = mảng tham số số
        public readonly List<KeyValuePair<string, double[]>> Steps = new List<KeyValuePair<string, double[]>>();

        // Bảng số tham số tối thiểu bắt buộc cho từng lệnh
        static readonly Dictionary<string, int> MinArgs = new Dictionary<string, int>
        {
            // tap/dtap cần x y; hold cần x y min max; swipe cần 6 số; wait cần 1; loop cần 1; break cần 3
            { "tap", 2 }, { "dtap", 2 }, { "hold", 4 }, { "swipe", 6 }, { "wait", 1 }, { "loop", 1 }, { "break", 3 }
        };

        // Parse văn bản kịch bản thành đối tượng Macro; lỗi từng dòng được thêm vào errs
        public static Macro Parse(string name, string text, List<string> errs)
        {
            var m = new Macro { Name = name }; // Tạo macro rỗng với tên đã cho
            string[] lines = text.Replace("\r", "").Split('\n'); // Bỏ \r rồi tách thành từng dòng
            for (int n = 0; n < lines.Length; n++) // Duyệt từng dòng, n là chỉ số dòng (từ 0)
            {
                string line = lines[n]; // Lấy nội dung dòng hiện tại
                int c = line.IndexOf('#'); // Tìm vị trí ký tự chú thích '#'
                if (c >= 0) line = line.Substring(0, c); // Cắt bỏ phần chú thích phía sau '#'
                var a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); // Tách theo dấu cách/tab, bỏ phần rỗng
                if (a.Length == 0) continue; // Dòng trống -> bỏ qua
                string op = a[0].ToLowerInvariant(); // Tên lệnh, đổi về chữ thường
                int need; // Số tham số tối thiểu của lệnh
                // Lệnh không có trong bảng -> ghi lỗi và sang dòng tiếp
                if (!MinArgs.TryGetValue(op, out need)) { errs.Add("Dòng " + (n + 1) + ": lệnh không biết '" + a[0] + "'"); continue; }
                if (a.Length - 1 < need) { errs.Add("Dòng " + (n + 1) + ": thiếu tham số"); continue; } // Thiếu tham số -> ghi lỗi
                var v = new double[a.Length - 1]; // Mảng chứa các tham số dạng số
                bool ok = true; // Cờ đánh dấu tất cả tham số parse thành công
                for (int i = 1; i < a.Length; i++) // Duyệt từng tham số sau tên lệnh
                    // Parse số theo chuẩn invariant (dấu chấm thập phân); lỗi thì đặt ok = false
                    if (!double.TryParse(a[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i - 1])) ok = false;
                if (!ok) { errs.Add("Dòng " + (n + 1) + ": số không hợp lệ"); continue; } // Có số sai -> ghi lỗi

                if (op == "loop") m.Loop = (int)v[0]; // Lệnh loop: đặt số vòng lặp
                // Lệnh break: lưu xác suất nghỉ và khoảng thời gian nghỉ
                else if (op == "break") { m.BreakPct = v[0]; m.BreakMin = (int)v[1]; m.BreakMax = (int)v[2]; }
                else m.Steps.Add(new KeyValuePair<string, double[]>(op, v)); // Các lệnh còn lại: thêm thành một bước
            }
            return m; // Trả về macro đã parse
        }
    }

    // Quản lý chạy/dừng các macro, mỗi macro chạy trên một thread riêng
    public static class MacroRunner
    {
        // Bảng macro đang chạy: tên (không phân biệt hoa/thường) -> nguồn huỷ để dừng
        static readonly Dictionary<string, CancellationTokenSource> running = new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        static long nextId = 2000; // Bộ đếm sinh id chạm (pointer id) riêng cho mỗi lần chạy macro
        public static event Action Changed; // Sự kiện báo khi danh sách macro đang chạy thay đổi

        static void Fire() { var h = Changed; if (h != null) h(); } // Gọi sự kiện Changed an toàn (chép ra biến tạm trước)

        public static bool IsRunning(string name) { lock (running) return running.ContainsKey(name); } // Kiểm tra macro có đang chạy không

        // Bắt đầu chạy macro m (bỏ qua nếu macro cùng tên đang chạy)
        public static void Start(Macro m, TouchService t)
        {
            CancellationTokenSource cts; // Nguồn huỷ cho lần chạy này
            lock (running) // Khoá bảng running để tránh xung đột giữa các thread
            {
                if (running.ContainsKey(m.Name)) return; // Đang chạy rồi -> không chạy lại
                cts = new CancellationTokenSource(); // Tạo nguồn huỷ mới
                running[m.Name] = cts; // Đăng ký macro vào bảng đang chạy
            }
            long id = Interlocked.Increment(ref nextId); // Tăng bộ đếm an toàn đa luồng để lấy id chạm mới
            // Tạo thread nền chạy macro, đặt tên thread để dễ debug
            new Thread(() => Run(m, t, id, cts)) { IsBackground = true, Name = "macro " + m.Name }.Start();
            Fire(); // Báo giao diện cập nhật trạng thái
        }

        // Yêu cầu dừng macro theo tên
        public static void Stop(string name)
        {
            lock (running) // Khoá bảng running
            {
                CancellationTokenSource c; // Nguồn huỷ của macro cần dừng
                if (running.TryGetValue(name, out c)) c.Cancel(); // Tìm thấy -> phát tín hiệu huỷ
            }
        }

        // Yêu cầu dừng tất cả macro đang chạy
        public static void StopAll()
        {
            lock (running) foreach (var c in running.Values) c.Cancel(); // Huỷ từng macro trong bảng
        }

        static double Opt(double[] v, int i, double def) { return v.Length > i ? v[i] : def; } // Lấy tham số tuỳ chọn thứ i, không có thì dùng giá trị mặc định

        // Thân thread chạy macro: lặp các bước cho đến khi hết vòng hoặc bị huỷ
        static void Run(Macro m, TouchService t, long id, CancellationTokenSource cts)
        {
            var ct = cts.Token; // Token huỷ để truyền vào các thao tác chờ
            Log.I("▶ Macro '" + m.Name + "' chạy (" + (m.Loop == 0 ? "vô hạn" : m.Loop + " vòng") + ")"); // Ghi log bắt đầu
            int iter = 0; // Số vòng đã chạy
            try
            {
                for (; m.Loop == 0 || iter < m.Loop; iter++) // Lặp vô hạn nếu Loop = 0, ngược lại đủ Loop vòng
                {
                    while (!t.Ready) TouchService.Wait(1000, ct); // Chờ đến khi kênh chạm sẵn sàng (mỗi lần 1 giây)
                    foreach (var s in m.Steps) // Thực hiện lần lượt từng bước
                    {
                        var v = s.Value; // Mảng tham số của bước
                        switch (s.Key) // Xử lý theo tên lệnh
                        {
                            case "tap": // Chạm một lần
                                t.Tap(id, v[0], v[1], Opt(v, 2, 0.015), ct); // Chạm tại (x,y), bán kính lệch mặc định 0.015
                                break;
                            case "dtap": // Chạm đúp
                                t.Tap(id, v[0], v[1], Opt(v, 2, 0.01), ct); // Lần chạm thứ nhất, bán kính lệch mặc định 0.01
                                TouchService.Wait(Human.Between(60, 130), ct); // Nghỉ ngẫu nhiên 60-130ms giữa hai lần chạm
                                t.Tap(id, v[0], v[1], 0.006, ct); // Lần chạm thứ hai, lệch rất nhỏ
                                break;
                            case "hold": // Giữ chạm
                                // Giữ tại (x,y) trong khoảng thời gian ngẫu nhiên [minMs, maxMs], bán kính mặc định 0.015
                                t.Hold(id, v[0], v[1], Opt(v, 4, 0.015), Human.Between((int)v[2], (int)v[3]), ct);
                                break;
                            case "swipe": // Vuốt
                                // Vuốt từ (x1,y1) đến (x2,y2) trong thời gian ngẫu nhiên [minMs, maxMs]
                                t.Swipe(id, v[0], v[1], v[2], v[3], Human.Between((int)v[4], (int)v[5]), ct);
                                break;
                            case "wait": // Chờ
                                int a = (int)v[0], b = (int)Opt(v, 1, v[0]); // a = min, b = max (không có max thì bằng min)
                                int ms = Human.Between(a, b); // Chọn thời gian chờ ngẫu nhiên trong [a, b]
                                if (Human.Chance(3)) ms += Human.Between(150, 600); // thỉnh thoảng chần chừ
                                TouchService.Wait(ms, ct); // Chờ (có thể bị huỷ)
                                break;
                        }
                    }
                    if (m.BreakPct > 0 && Human.Chance(m.BreakPct)) // Cuối vòng: tung xác suất nghỉ dài
                    {
                        int ms = Human.Between(m.BreakMin, m.BreakMax); // Thời gian nghỉ ngẫu nhiên
                        Log.I("Macro '" + m.Name + "' nghỉ " + ms / 1000 + "s"); // Ghi log số giây nghỉ
                        TouchService.Wait(ms, ct); // Nghỉ (có thể bị huỷ)
                    }
                }
            }
            catch (OperationCanceledException) { } // Bị dừng chủ động -> không coi là lỗi
            catch (Exception ex) { Log.I("Macro '" + m.Name + "' lỗi: " + ex.Message); } // Lỗi khác -> ghi log
            finally // Luôn chạy khi kết thúc
            {
                lock (running) running.Remove(m.Name); // Gỡ macro khỏi bảng đang chạy
                Log.I("■ Macro '" + m.Name + "' dừng sau " + iter + " vòng"); // Ghi log số vòng đã chạy
                Fire(); // Báo giao diện cập nhật
            }
        }
    }
}
