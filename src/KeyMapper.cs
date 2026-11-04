using System; // Kiểu cơ bản: IntPtr, Exception, Action, Math...
using System.Collections.Concurrent; // BlockingCollection (hàng đợi an toàn đa luồng)
using System.Collections.Generic; // List, Dictionary, HashSet, KeyValuePair
using System.Diagnostics; // Process (lấy tên tiến trình theo PID)
using System.Globalization; // CultureInfo để parse số thập phân
using System.Runtime.InteropServices; // DllImport, Marshal, StructLayout cho gọi Win32
using System.Threading; // Thread, ThreadPriority
using System.Windows.Forms; // Enum Keys (mã phím ảo)

namespace DsoftEmu // Không gian tên chung của ứng dụng
{
    /// <summary>
    /// Map phím bàn phím -> chạm. Hook bàn phím mức thấp, chỉ hoạt động khi cửa sổ giả lập đang focus.
    /// Cú pháp file keymap (toạ độ chuẩn hoá 0..1, r theo cạnh ngắn màn hình):
    ///   tap    &lt;Key&gt; x y [r]
    ///   joy    &lt;Up&gt; &lt;Left&gt; &lt;Down&gt; &lt;Right&gt; cx cy radius
    ///   macro  &lt;Key&gt; &lt;tên_macro&gt;
    ///   toggle &lt;Key&gt;
    /// </summary>
    public class KeyMapper : IDisposable
    {
        enum Kind { Tap, Joy, Macro, Toggle } // Loại gán phím: chạm, joystick, chạy macro, bật/tắt map

        // Một dòng gán phím đã parse cùng trạng thái chạm hiện tại
        class Bind
        {
            public Kind K; // Loại gán phím
            public int[] Vk; // Danh sách mã phím ảo (joy có 4 phím: lên, trái, xuống, phải)
            public double X, Y, R; // Toạ độ tâm chuẩn hoá và bán kính
            public string Macro; // Tên macro cần bật/tắt (với loại Macro)
            public long Id; // Pointer id riêng cho chạm của gán phím này
            public bool Active; // Đang giữ chạm trên màn hình hay không
            public double CurX, CurY; // Toạ độ chạm hiện tại (để nhả đúng chỗ)
        }

        readonly TouchService touch; // Dịch vụ gửi chạm xuống Android
        volatile Dictionary<int, Bind> byVk = new Dictionary<int, Bind>(); // Tra cứu nhanh mã phím -> gán phím (thay cả bảng khi nạp lại)
        List<Bind> binds = new List<Bind>(); // Danh sách tất cả gán phím hiện tại
        readonly HashSet<int> down = new HashSet<int>(); // Phím đang được giữ (chỉ dùng trên thread Worker)
        readonly HashSet<int> hookDown = new HashSet<int>(); // Phím đã bị hook nuốt khi nhấn (để nuốt cả lúc nhả)
        readonly BlockingCollection<KeyValuePair<int, bool>> q = new BlockingCollection<KeyValuePair<int, bool>>(); // Hàng đợi sự kiện phím (mã phím, nhấn/nhả) từ hook sang Worker
        readonly Dictionary<uint, bool> emuPid = new Dictionary<uint, bool>(); // Cache PID -> có phải tiến trình giả lập không
        readonly LowLevelKeyboardProc proc; // Giữ tham chiếu delegate để GC không thu hồi khi hook còn dùng
        IntPtr hook; // Handle của hook bàn phím đã cài

        public volatile bool Enabled = true; // Map phím đang bật hay tắt
        public Action<string> ToggleMacro; // Callback bật/tắt macro theo tên (do form gán)

        // Khởi tạo: lưu dịch vụ chạm, tạo delegate hook và chạy thread xử lý phím
        public KeyMapper(TouchService t)
        {
            touch = t; // Lưu dịch vụ chạm
            proc = HookProc; // Tạo delegate trỏ tới hàm xử lý hook
            new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.AboveNormal }.Start(); // Thread nền ưu tiên cao xử lý hàng đợi phím
        }

        static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); } // Parse số thực theo chuẩn dấu chấm

        // Đổi tên phím (vd "W", "Space") sang mã phím ảo
        static int Vk(string name)
        {
            Keys k; // Biến nhận giá trị enum Keys
            if (!Enum.TryParse(name, true, out k)) throw new Exception("phím không hợp lệ '" + name + "'"); // Tên sai -> báo lỗi
            return (int)k; // Trả về mã phím ảo dạng số
        }

        // Nạp nội dung file keymap; trả về danh sách thông báo lỗi theo dòng
        public List<string> Load(string text)
        {
            var errs = new List<string>(); // Danh sách lỗi
            var list = new List<Bind>(); // Danh sách gán phím mới
            var map = new Dictionary<int, Bind>(); // Bảng mã phím -> gán phím mới
            long nextId = 10; // Pointer id bắt đầu từ 10, mỗi gán phím một id
            string[] lines = text.Replace("\r", "").Split('\n'); // Bỏ \r và tách dòng
            for (int n = 0; n < lines.Length; n++) // Duyệt từng dòng
            {
                string line = lines[n]; // Nội dung dòng hiện tại
                int c = line.IndexOf('#'); // Tìm ký tự chú thích '#'
                if (c >= 0) line = line.Substring(0, c); // Cắt bỏ phần chú thích
                var a = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); // Tách các từ theo dấu cách/tab
                if (a.Length == 0) continue; // Dòng trống -> bỏ qua
                try
                {
                    var b = new Bind { Id = nextId++ }; // Tạo gán phím mới với id kế tiếp
                    switch (a[0].ToLowerInvariant()) // Xử lý theo tên lệnh (chữ thường)
                    {
                        case "tap": // tap <Key> x y [r]
                            b.K = Kind.Tap; b.Vk = new[] { Vk(a[1]) }; b.X = D(a[2]); b.Y = D(a[3]); // Loại chạm, 1 phím, toạ độ x y
                            b.R = a.Length > 4 ? D(a[4]) : 0.012; // Bán kính lệch, mặc định 0.012
                            break;
                        case "joy": // joy <Up> <Left> <Down> <Right> cx cy radius
                            b.K = Kind.Joy; b.Vk = new[] { Vk(a[1]), Vk(a[2]), Vk(a[3]), Vk(a[4]) }; // Loại joystick, 4 phím hướng
                            b.X = D(a[5]); b.Y = D(a[6]); b.R = D(a[7]); // Tâm joystick và bán kính kéo
                            break;
                        case "macro": // macro <Key> <tên_macro>
                            b.K = Kind.Macro; b.Vk = new[] { Vk(a[1]) }; b.Macro = a[2]; // Loại macro, phím và tên macro
                            break;
                        case "toggle": // toggle <Key>
                            b.K = Kind.Toggle; b.Vk = new[] { Vk(a[1]) }; // Phím bật/tắt toàn bộ map phím
                            break;
                        default: // Lệnh lạ
                            throw new Exception("lệnh không biết '" + a[0] + "'"); // Báo lỗi lệnh không hỗ trợ
                    }
                    foreach (int vk in b.Vk) // Đăng ký từng phím của gán này vào bảng
                    {
                        // Phím đã được gán trước đó -> cảnh báo, gán sau sẽ đè gán trước
                        if (map.ContainsKey(vk)) errs.Add("Dòng " + (n + 1) + ": phím " + (Keys)vk + " bị gán trùng, dùng gán sau.");
                        map[vk] = b; // Ghi (hoặc ghi đè) gán phím cho mã phím này
                    }
                    list.Add(b); // Thêm vào danh sách gán phím
                }
                catch (Exception ex) // Lỗi khi parse dòng
                {
                    // Thiếu phần tử mảng nghĩa là thiếu tham số, còn lại lấy thông báo lỗi gốc
                    errs.Add("Dòng " + (n + 1) + ": " + (ex is IndexOutOfRangeException ? "thiếu tham số" : ex.Message));
                }
            }
            q.Add(new KeyValuePair<int, bool>(-1, false)); // nhả hết chạm cũ
            binds = list; // Thay danh sách gán phím
            byVk = map; // Thay bảng tra cứu (gán nguyên tử, hook đọc ngay bảng mới)
            Log.I("Keymap: " + list.Count + " gán phím" + (errs.Count > 0 ? ", " + errs.Count + " lỗi" : "")); // Ghi log số gán phím và số lỗi
            return errs; // Trả về danh sách lỗi
        }

        // Cài hook bàn phím mức thấp toàn hệ thống (chỉ cài một lần)
        public void Install()
        {
            if (hook != IntPtr.Zero) return; // Đã cài rồi -> thoát
            hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, proc, GetModuleHandle(null), 0); // Cài hook WH_KEYBOARD_LL cho mọi thread
            if (hook == IntPtr.Zero) Log.I("⚠ Không cài được keyboard hook: " + Marshal.GetLastWin32Error()); // Thất bại -> ghi mã lỗi Win32
        }

        // Hàm Windows gọi cho mỗi sự kiện phím; trả về 1 để nuốt phím, ngược lại chuyển tiếp
        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0) // nCode < 0 thì phải chuyển tiếp ngay, không được xử lý
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT)); // Đọc thông tin phím từ con trỏ lParam
                if ((k.flags & 0x10) == 0) // bỏ qua phím do phần mềm khác bơm
                {
                    int vk = (int)k.vkCode; // Mã phím ảo
                    bool isUp = (k.flags & 0x80) != 0; // Bit 0x80 (LLKHF_UP) = sự kiện nhả phím
                    Bind b; // Gán phím tương ứng (nếu có)
                    if (byVk.TryGetValue(vk, out b)) // Chỉ xử lý phím có trong keymap
                    {
                        // Nhả phím luôn được xử lý (kể cả khi đã alt-tab) để không bị kẹt chạm.
                        if (isUp && hookDown.Remove(vk)) { q.Add(new KeyValuePair<int, bool>(vk, false)); return (IntPtr)1; }
                        // Nhấn phím: chỉ xử lý khi map đang bật (hoặc là phím toggle) và cửa sổ giả lập đang ở trước
                        if (!isUp && (Enabled || b.K == Kind.Toggle) && EmuForeground())
                        {
                            hookDown.Add(vk); // Ghi nhớ phím đã nuốt để nuốt cả lúc nhả
                            q.Add(new KeyValuePair<int, bool>(vk, true)); // Đẩy sự kiện nhấn vào hàng đợi cho Worker
                            return (IntPtr)1; // nuốt phím, không cho xuống giả lập
                        }
                    }
                }
            }
            return CallNextHookEx(hook, nCode, wParam, lParam); // Chuyển sự kiện cho hook tiếp theo / ứng dụng
        }

        // Kiểm tra cửa sổ đang foreground có thuộc tiến trình giả lập (qemu-system*/emulator) không
        bool EmuForeground()
        {
            uint pid; // PID của tiến trình sở hữu cửa sổ foreground
            GetWindowThreadProcessId(GetForegroundWindow(), out pid); // Lấy PID từ handle cửa sổ đang active
            bool r; // Kết quả
            lock (emuPid) // Khoá cache PID
            {
                if (emuPid.TryGetValue(pid, out r)) return r; // Có trong cache -> trả luôn
                if (emuPid.Count > 256) emuPid.Clear(); // Cache quá lớn -> xoá để tránh phình
                try
                {
                    string n = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); // Lấy tên tiến trình (chữ thường)
                    r = n.StartsWith("qemu-system") || n == "emulator"; // Là giả lập nếu tên bắt đầu qemu-system hoặc là emulator
                }
                catch { r = false; } // Tiến trình đã thoát / không truy cập được -> coi là không phải
                emuPid[pid] = r; // Lưu kết quả vào cache
            }
            return r; // Trả kết quả
        }

        // Thread nền: lấy sự kiện phím từ hàng đợi và xử lý tuần tự
        void Worker()
        {
            foreach (var ev in q.GetConsumingEnumerable()) // Chờ và lấy lần lượt từng sự kiện (chặn khi hàng đợi rỗng)
            {
                try { Handle(ev.Key, ev.Value); } // Xử lý sự kiện (mã phím, nhấn/nhả)
                catch (Exception ex) { Log.I("Keymap lỗi: " + ex.Message); } // Lỗi -> ghi log, không làm chết thread
            }
        }

        // Nhả tất cả chạm đang giữ và xoá trạng thái phím
        void ReleaseAll()
        {
            foreach (var b in binds) // Duyệt mọi gán phím
                if (b.Active) { touch.Raw(1, b.Id, b.CurX, b.CurY); b.Active = false; } // Đang chạm -> gửi sự kiện nhả (action 1)
            down.Clear(); // Xoá danh sách phím đang giữ
        }

        // Xử lý một sự kiện phím: vk = mã phím (-1 = nhả hết), isDown = nhấn hay nhả
        void Handle(int vk, bool isDown)
        {
            if (vk == -1) { ReleaseAll(); return; } // Lệnh đặc biệt: nhả hết chạm
            bool was = down.Contains(vk); // Phím đã đang được giữ trước đó chưa
            if (isDown && was) return; // auto-repeat
            if (isDown) down.Add(vk); else down.Remove(vk); // Cập nhật tập phím đang giữ

            Bind b; // Gán phím tương ứng
            if (!byVk.TryGetValue(vk, out b)) return; // Không có gán (keymap vừa đổi) -> bỏ qua
            switch (b.K) // Xử lý theo loại gán
            {
                case Kind.Toggle: // Phím bật/tắt map
                    if (!isDown) return; // Chỉ phản ứng khi nhấn
                    Enabled = !Enabled; // Đảo trạng thái bật/tắt
                    if (!Enabled) ReleaseAll(); // Vừa tắt -> nhả hết chạm đang giữ
                    Log.I("Map phím: " + (Enabled ? "BẬT" : "TẮT")); // Ghi log trạng thái mới
                    break;
                case Kind.Macro: // Phím chạy/dừng macro
                    if (isDown && ToggleMacro != null) ToggleMacro(b.Macro); // Khi nhấn -> gọi callback bật/tắt macro
                    break;
                case Kind.Tap: // Phím chạm
                    if (!touch.Ready) return; // Kênh chạm chưa sẵn sàng -> bỏ qua
                    if (isDown) // Nhấn phím -> đặt ngón xuống
                    {
                        touch.Jittered(b.X, b.Y, b.R, out b.CurX, out b.CurY); // Tính toạ độ có lệch ngẫu nhiên
                        touch.Raw(0, b.Id, b.CurX, b.CurY); // Gửi sự kiện chạm xuống (action 0)
                        b.Active = true; // Đánh dấu đang chạm
                    }
                    else if (b.Active) // Nhả phím và đang chạm -> nhấc ngón
                    {
                        touch.Raw(1, b.Id, b.CurX, b.CurY); // Gửi sự kiện nhả (action 1) tại vị trí cũ
                        b.Active = false; // Bỏ đánh dấu đang chạm
                    }
                    break;
                case Kind.Joy: // Phím joystick
                    if (touch.Ready) Joy(b); // Kênh chạm sẵn sàng -> cập nhật vị trí joystick
                    break;
            }
        }

        // Cập nhật joystick ảo theo tổ hợp 4 phím hướng đang giữ
        void Joy(Bind b)
        {
            double dx = 0, dy = 0; // Hướng di chuyển (-1..1 mỗi trục)
            if (down.Contains(b.Vk[0])) dy -= 1; // Phím lên -> đi lên (y giảm)
            if (down.Contains(b.Vk[1])) dx -= 1; // Phím trái -> đi trái
            if (down.Contains(b.Vk[2])) dy += 1; // Phím xuống -> đi xuống
            if (down.Contains(b.Vk[3])) dx += 1; // Phím phải -> đi phải

            if (dx == 0 && dy == 0) // Không còn hướng nào (nhả hết hoặc hai phím ngược nhau)
            {
                if (b.Active) { touch.Raw(1, b.Id, b.CurX, b.CurY); b.Active = false; } // Đang chạm -> nhả ngón
                return; // Kết thúc
            }
            double len = Math.Sqrt(dx * dx + dy * dy); // Độ dài vector hướng (để chuẩn hoá đường chéo)
            double rx, ry; // Bán kính quy đổi theo trục X/Y chuẩn hoá
            touch.Radius(b.R, out rx, out ry); // Đổi bán kính joystick sang đơn vị chuẩn hoá từng trục
            double k = 0.9 + Human.U() * 0.1; // Hệ số kéo ngẫu nhiên 90%-100% bán kính
            double tx = b.X + dx / len * rx * k, ty = b.Y + dy / len * ry * k; // Điểm đích của ngón trên vòng joystick

            if (!b.Active) // Chưa chạm -> đặt ngón ở tâm trước
            {
                touch.Jittered(b.X, b.Y, 0.006, out b.CurX, out b.CurY); // Tâm có lệch nhỏ ngẫu nhiên
                touch.Raw(0, b.Id, b.CurX, b.CurY); // Gửi chạm xuống
                b.Active = true; // Đánh dấu đang chạm
                Thread.Sleep(Human.Between(6, 14)); // Chờ ngắn 6-14ms trước khi kéo
            }
            double sx = b.CurX, sy = b.CurY; // Điểm xuất phát của lần kéo này
            for (int i = 1; i <= 3; i++) // Kéo tới đích qua 3 bước nhỏ cho mượt
            {
                b.CurX = sx + (tx - sx) * i / 3; // Nội suy toạ độ X
                b.CurY = sy + (ty - sy) * i / 3; // Nội suy toạ độ Y
                touch.Raw(2, b.Id, b.CurX, b.CurY); // Gửi sự kiện di chuyển (action 2)
                Thread.Sleep(Human.Between(3, 7)); // Nghỉ 3-7ms giữa các bước
            }
        }

        // Gỡ hook bàn phím khi huỷ đối tượng
        public void Dispose()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } // Có hook -> gỡ và đặt lại handle rỗng
        }

        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam); // Kiểu hàm callback cho hook bàn phím mức thấp

        // Cấu trúc Windows mô tả sự kiện phím mức thấp (bố cục tuần tự giống C)
        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; } // Mã phím ảo, mã quét, cờ, thời điểm, dữ liệu phụ

        // SetWindowsHookEx: cài một hook của Windows (ở đây là hook bàn phím mức thấp), trả về handle hook
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h); // UnhookWindowsHookEx: gỡ hook đã cài
        // CallNextHookEx: chuyển sự kiện cho hook kế tiếp trong chuỗi hook
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name); // GetModuleHandle(null): lấy handle module của chính tiến trình
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow(); // GetForegroundWindow: lấy handle cửa sổ đang active
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid); // GetWindowThreadProcessId: lấy PID tiến trình sở hữu cửa sổ
    }
}
