using System; // DateTime, IntPtr, Environment
using System.Diagnostics; // Process
using System.Runtime.InteropServices; // DllImport, StructLayout
using System.Text; // StringBuilder
using System.Windows.Forms; // NativeWindow, Message, SystemInformation
using Microsoft.Win32; // SystemEvents

namespace DsoftEmu
{
    /// <summary>Ghi vào app.log các sự kiện Windows (kết nối/ngắt từ xa, khoá máy, ngủ/thức, đăng xuất)
    /// và chụp trạng thái lúc emulator bị đóng ngoài ý muốn - để đối chiếu nguyên nhân.</summary>
    public static class SysLog
    {
        const int WM_WTSSESSION_CHANGE = 0x02B1;
        static Sink sink; // Cửa sổ ẩn nhận WM_WTSSESSION_CHANGE
        static volatile string lastEvt = "không có"; // Sự kiện phiên gần nhất
        static DateTime lastEvtAt = DateTime.MinValue;

        public static void Init()
        {
            try
            {
                sink = new Sink();
                sink.CreateHandle(new CreateParams());
                if (!WTSRegisterSessionNotification(sink.Handle, 1)) Log.I("[sys] Không đăng ký được sự kiện phiên Windows.");
            }
            catch (Exception ex) { Log.I("[sys] Lỗi đăng ký sự kiện phiên: " + ex.Message); }
            SystemEvents.PowerModeChanged += (s, e) => Note("Nguồn: " + (e.Mode == PowerModes.Suspend ? "NGỦ (sleep/hibernate)" : e.Mode == PowerModes.Resume ? "THỨC DẬY" : "đổi chế độ pin/sạc"));
            SystemEvents.SessionEnding += (s, e) => Note("Windows sắp " + (e.Reason == SessionEndReasons.Logoff ? "ĐĂNG XUẤT" : "TẮT/KHỞI ĐỘNG LẠI"));
            SystemEvents.DisplaySettingsChanged += (s, e) => Note("Đổi màn hình/độ phân giải: " + System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width + "x" + System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height);
            Log.I("[sys] Phiên Windows: " + (SystemInformation.TerminalServerSession ? "ĐIỀU KHIỂN TỪ XA (RDP)" : "tại máy") + ", session " + Process.GetCurrentProcess().SessionId);
        }

        static void Note(string what)
        {
            lastEvt = what; lastEvtAt = DateTime.Now;
            Log.I("[sys] " + what);
        }

        static string Wts(int code)
        {
            switch (code)
            {
                case 1: return "Kết nối tại máy (console connect)";
                case 2: return "Ngắt tại máy (console disconnect)";
                case 3: return "Kết nối từ xa RDP (remote connect)";
                case 4: return "Ngắt kết nối từ xa RDP (remote disconnect)";
                case 5: return "Đăng nhập (logon)";
                case 6: return "Đăng xuất (logoff)";
                case 7: return "KHOÁ máy (lock)";
                case 8: return "Mở khoá (unlock)";
                case 9: return "Đổi chế độ điều khiển từ xa (remote control)";
                default: return "Sự kiện phiên " + code;
            }
        }

        /// <summary>Trạng thái máy tại thời điểm hiện tại: lần thao tác chuột/phím gần nhất, cửa sổ đang active, phiên.</summary>
        public static string Snapshot()
        {
            var sb = new StringBuilder();
            try
            {
                var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                if (GetLastInputInfo(ref li)) sb.Append("thao tác chuột/phím gần nhất ").Append(((uint)Environment.TickCount - li.dwTime) / 1000.0).Append("s trước");
            }
            catch { }
            try
            {
                IntPtr fg = GetForegroundWindow();
                var t = new StringBuilder(256); GetWindowText(fg, t, t.Capacity);
                uint pid; GetWindowThreadProcessId(fg, out pid);
                string pn = "?"; try { pn = Process.GetProcessById((int)pid).ProcessName; } catch { }
                sb.Append("; cửa sổ active: \"").Append(t).Append("\" (").Append(pn).Append(")");
            }
            catch { }
            sb.Append("; phiên: ").Append(SystemInformation.TerminalServerSession ? "từ xa (RDP)" : "tại máy");
            if (lastEvtAt != DateTime.MinValue) sb.Append("; sự kiện Windows gần nhất: ").Append(lastEvt).Append(" lúc ").Append(lastEvtAt.ToString("dd/MM HH:mm:ss"));
            return sb.ToString();
        }

        class Sink : NativeWindow
        {
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_WTSSESSION_CHANGE) Note(Wts(m.WParam.ToInt32()));
                base.WndProc(ref m);
            }
        }

        [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
    }
}
