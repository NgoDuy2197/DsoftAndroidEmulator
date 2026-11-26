using System;  // IntPtr, Func, Math
using System.Collections.Generic;  // Dictionary
using System.Drawing;  // Color
using System.Runtime.InteropServices;  // DllImport, StructLayout
using System.Windows.Forms;  // Form, Timer

namespace DsoftEmu
{
    /// <summary>
    /// Lớp phủ trên cửa sổ giả lập: làm tối và/hoặc chuyển đen trắng.
    /// Chỉ thay đổi hình hiển thị trên Windows: chuột/phím xuyên qua, Android và ảnh chụp gửi Discord không bị ảnh hưởng.
    /// - Chỉ làm tối: cửa sổ đen trong suốt một phần (rất nhẹ).
    /// - Đen trắng: dùng Magnification API của Windows vẽ lại vùng cửa sổ giả lập qua ma trận màu (tốn thêm chút CPU/GPU).
    /// </summary>
    public sealed class ScreenFilter : Form
    {
        static readonly Dictionary<int, ScreenFilter> on = new Dictionary<int, ScreenFilter>();  // Lớp phủ đang bật, theo cổng máy
        static int magRefs;  // Số lớp phủ đang dùng Magnification API (MagInitialize 1 lần cho cả app)

        readonly Func<IntPtr> target;  // Hàm tìm cửa sổ giả lập (cửa sổ có thể đổi khi máy khởi động lại)
        readonly Timer tick = new Timer();  // Theo vị trí cửa sổ giả lập + vẽ lại khi đen trắng
        readonly float dim;  // 0..0.9: mức làm tối
        readonly bool gray;  // true = đen trắng
        IntPtr mag;  // Cửa sổ Magnifier con

        public static bool IsOn(int port) { return on.ContainsKey(port); }

        /// <summary>Bật/tắt lớp phủ cho máy có cổng port. dimPct 0..90, gray = đen trắng. Trả về trạng thái sau khi bấm.</summary>
        public static bool Toggle(int port, Func<IntPtr> target, int dimPct, bool gray)
        {
            ScreenFilter f;
            if (on.TryGetValue(port, out f)) { f.Close(); return false; }
            f = new ScreenFilter(target, dimPct, gray);
            on[port] = f;
            f.FormClosed += (s, e) => on.Remove(port);
            // Tạo cửa sổ ở chế độ DPI theo từng màn hình: toạ độ luôn là pixel thật, khớp với khung cửa sổ giả lập
            // (app chỉ "system DPI aware": khi RDP / đổi scale màn hình, toạ độ bị Windows quy đổi -> lớp phủ lệch)
            IntPtr old = PerMonitor();
            try { f.Show(); } finally { Restore(old); }
            return true;
        }

        // Chuyển luồng hiện tại sang DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 (Win10 1703+); trả ngữ cảnh cũ để khôi phục
        static IntPtr PerMonitor()
        {
            try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { return IntPtr.Zero; }  // Windows cũ: không có hàm -> bỏ qua
        }
        static void Restore(IntPtr old)
        {
            if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { }
        }

        /// <summary>Tắt lớp phủ của một máy (máy dừng) hoặc tất cả (port &lt; 0).</summary>
        public static void Off(int port)
        {
            foreach (var kv in new List<KeyValuePair<int, ScreenFilter>>(on))
                if (port < 0 || kv.Key == port) kv.Value.Close();
        }

        ScreenFilter(Func<IntPtr> target, int dimPct, bool gray)
        {
            this.target = target;
            dim = Math.Max(0, Math.Min(90, dimPct)) / 100f;
            this.gray = gray;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Bounds = new Rectangle(-32000, -32000, 1, 1);  // Chưa hiện ra cho tới khi bám được cửa sổ giả lập
            tick.Interval = gray ? 33 : 50;  // Đen trắng cần vẽ lại liên tục (~30 hình/giây); làm tối chỉ cần bám vị trí/thứ tự nhanh
            tick.Tick += (s, e) => { IntPtr old = PerMonitor(); try { Follow(); } finally { Restore(old); } };
        }

        bool logged;  // Đã ghi log lần đo đầu tiên

        protected override bool ShowWithoutActivation { get { return true; } }  // Không cướp focus của cửa sổ giả lập

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000;  // WS_EX_LAYERED | WS_EX_TRANSPARENT (chuột xuyên qua) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (gray && magRefs++ == 0 && !MagInitialize()) { magRefs--; }
            if (gray && magRefs > 0)
            {
                SetLayeredWindowAttributes(Handle, 0, 255, 2);  // LWA_ALPHA: hiện đủ, màu do Magnifier vẽ
                mag = CreateWindowEx(0, "Magnifier", "DsoftFilter", 0x40000000 | 0x10000000, 0, 0, 1, 1, Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);  // WS_CHILD | WS_VISIBLE
                if (mag != IntPtr.Zero)
                {
                    var hs = new[] { Handle };
                    try { MagSetWindowFilterList(mag, 0, 1, hs); } catch { }  // MW_FILTERMODE_EXCLUDE: không chụp chính lớp phủ
                    var fx = new MAGCOLOREFFECT { t = new float[25] };
                    float k = 1f - dim;  // Độ sáng còn lại
                    float[] w = { 0.299f, 0.587f, 0.114f };  // Trọng số độ sáng R, G, B
                    for (int row = 0; row < 3; row++)  // Dòng = kênh vào, cột = kênh ra: R'=G'=B'= độ sáng * k
                        for (int col = 0; col < 3; col++) fx.t[row * 5 + col] = w[row] * k;
                    fx.t[18] = 1; fx.t[24] = 1;  // Giữ alpha, hệ số tịnh tiến
                    MagSetColorEffect(mag, ref fx);
                }
            }
            if (mag == IntPtr.Zero) SetLayeredWindowAttributes(Handle, 0, (byte)(dim * 255), 2);  // Chỉ làm tối (hoặc Magnifier lỗi): lớp đen mờ
            Follow();
            tick.Start();
        }

        // Bám theo cửa sổ giả lập: cùng vị trí/kích thước, ẩn khi thu nhỏ/không thấy, vẽ lại khi đen trắng
        void Follow()
        {
            IntPtr h = target();
            RECT r;
            if (h == IntPtr.Zero || IsIconic(h) || !VisibleRect(h, out r) || r.R - r.L < 10)
            {
                if (Visible) SetWindowPos(Handle, IntPtr.Zero, -32000, -32000, 1, 1, 0x10 | 0x4);  // Giấu đi, giữ nguyên trạng thái bật
                return;
            }
            KeepAbove(h);
            int w = r.R - r.L, hh = r.B - r.T;
            // So với vị trí THẬT của lớp phủ (đọc lại từ Windows, không dùng Left/Top WinForms lưu sẵn) -> lệch vì lý do gì cũng tự đo lại
            RECT me; GetWindowRect(Handle, out me);
            if (me.L != r.L || me.T != r.T || me.R - me.L != w || me.B - me.T != hh)
            {
                SetWindowPos(Handle, IntPtr.Zero, r.L, r.T, w, hh, 0x10 | 0x4);  // NOACTIVATE | NOZORDER
                if (mag != IntPtr.Zero) SetWindowPos(mag, IntPtr.Zero, 0, 0, w, hh, 0x10 | 0x4);
                if (!logged)
                {
                    logged = true;
                    try { Log.I("Lớp phủ: cửa sổ giả lập " + r.L + "," + r.T + " " + w + "x" + hh + " (DPI " + GetDpiForWindow(h) + ")"); } catch { }
                }
            }
            if (mag != IntPtr.Zero)
            {
                MagSetWindowSource(mag, r);  // Vùng màn hình cần vẽ lại = đúng cửa sổ giả lập (tỉ lệ 1:1)
                InvalidateRect(mag, IntPtr.Zero, false);
            }
        }

        // Khung nhìn thấy được của cửa sổ: bỏ viền vô hình ~8px (vùng kéo giãn) mà GetWindowRect tính cả vào
        static bool VisibleRect(IntPtr h, out RECT r)
        {
            try { if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))) == 0 && r.R > r.L) return true; } catch { }  // DWMWA_EXTENDED_FRAME_BOUNDS
            return GetWindowRect(h, out r);
        }

        // Bấm vào cửa sổ giả lập thì nó được đưa lên trên cùng -> đặt lại lớp phủ nằm ngay trên nó (không đổi thứ tự cửa sổ khác)
        void KeepAbove(IntPtr h)
        {
            IntPtr prev = GetWindow(h, 3);  // GW_HWNDPREV: cửa sổ nằm ngay trên cửa sổ giả lập
            if (prev == Handle) return;  // Đã đúng chỗ
            bool emuTop = (GetWindowLong(h, -20) & 0x8) != 0, meTop = (GetWindowLong(Handle, -20) & 0x8) != 0;  // WS_EX_TOPMOST
            const uint F = 0x1 | 0x2 | 0x10;  // NOSIZE | NOMOVE | NOACTIVATE
            if (emuTop != meTop) SetWindowPos(Handle, emuTop ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0, F);  // Cùng nhóm "luôn trên cùng" với cửa sổ giả lập
            bool prevTop = prev != IntPtr.Zero && (GetWindowLong(prev, -20) & 0x8) != 0;
            IntPtr after = prev == IntPtr.Zero || prevTop != emuTop ? (emuTop ? new IntPtr(-1) : IntPtr.Zero) : prev;  // Đầu nhóm (HWND_TOPMOST/HWND_TOP) hoặc ngay dưới cửa sổ phía trên
            SetWindowPos(Handle, after, 0, 0, 0, 0, F);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop(); tick.Dispose();
            base.OnFormClosed(e);
            if (gray && magRefs > 0 && --magRefs == 0) MagUninitialize();
        }

        // ---------- Win32 ----------
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MAGCOLOREFFECT { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] t; }

        [DllImport("Magnification.dll")] static extern bool MagInitialize();
        [DllImport("Magnification.dll")] static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] static extern bool MagSetWindowSource(IntPtr h, RECT r);
        [DllImport("Magnification.dll")] static extern bool MagSetColorEffect(IntPtr h, ref MAGCOLOREFFECT e);
        [DllImport("Magnification.dll")] static extern bool MagSetWindowFilterList(IntPtr h, int mode, int count, IntPtr[] list);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    }
}
