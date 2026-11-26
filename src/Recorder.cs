using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DsoftEmu
{
    /// <summary>Ghi macro: bắt chuột trái trên vùng hiển thị của cửa sổ giả lập (không chặn, thao tác vẫn tới giả lập)
    /// rồi đổi thành tap / hold / swipe + wait đúng khoảng nghỉ thật. Cài/gỡ ở luồng giao diện.</summary>
    public sealed class MacroRecorder : IDisposable
    {
        class Act { public string Op; public double X, Y, X2, Y2; public DateTime Start, End; }

        readonly Func<IntPtr> target;
        readonly List<Act> acts = new List<Act>();
        readonly HookProc proc;
        IntPtr hook;
        Act cur; // Đang nhấn chuột
        public event Action Changed; // Thêm được 1 thao tác
        public int Count { get { return acts.Count; } }
        public DateTime StartedAt { get; private set; }

        public MacroRecorder(Func<IntPtr> target) { this.target = target; proc = Proc; }

        public bool Start()
        {
            StartedAt = DateTime.Now;
            hook = SetWindowsHookEx(14 /*WH_MOUSE_LL*/, proc, GetModuleHandle(null), 0);
            return hook != IntPtr.Zero;
        }

        public void Dispose() { if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; } }

        IntPtr Proc(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0)
            {
                try
                {
                    var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(MSLLHOOKSTRUCT));
                    int msg = w.ToInt32();
                    if ((m.flags & 1) == 0) // Bỏ qua chuột do phần mềm bơm
                    {
                        if (msg == 0x201) // WM_LBUTTONDOWN
                        {
                            double x, y;
                            if (Norm(m.pt, false, out x, out y)) cur = new Act { X = x, Y = y, Start = DateTime.Now };
                        }
                        else if (msg == 0x202 && cur != null) // WM_LBUTTONUP
                        {
                            double x, y;
                            if (!Norm(m.pt, true, out x, out y)) { x = cur.X; y = cur.Y; }
                            cur.End = DateTime.Now;
                            double ms = (cur.End - cur.Start).TotalMilliseconds;
                            if (Math.Abs(x - cur.X) + Math.Abs(y - cur.Y) > 0.02) { cur.Op = "swipe"; cur.X2 = x; cur.Y2 = y; }
                            else cur.Op = ms >= 350 ? "hold" : "tap";
                            acts.Add(cur); cur = null;
                            var h = Changed; if (h != null) h();
                        }
                    }
                }
                catch { }
            }
            return CallNextHookEx(hook, n, w, l);
        }

        // Toạ độ màn hình -> 0..1 trong vùng hiển thị của cửa sổ giả lập; clamp = điểm ngoài vùng thì kẹp vào mép
        bool Norm(POINT pt, bool clamp, out double x, out double y)
        {
            x = y = 0;
            IntPtr h = target();
            if (h == IntPtr.Zero || IsIconic(h)) return false;
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { } // Toạ độ hook là pixel thật -> đo cửa sổ cũng theo pixel thật
            try
            {
                RECT rc; POINT p = new POINT();
                if (!GetClientRect(h, out rc) || rc.R < 10 || rc.B < 10 || !ClientToScreen(h, ref p)) return false;
                x = (pt.X - p.X) / (double)rc.R; y = (pt.Y - p.Y) / (double)rc.B;
                if (x < 0 || y < 0 || x > 1 || y > 1)
                {
                    if (!clamp) return false;
                    x = Math.Max(0, Math.Min(1, x)); y = Math.Max(0, Math.Min(1, y));
                }
                return true;
            }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }

        static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
        static int I(double v) { return (int)Math.Round(v); }

        /// <summary>Nội dung macro (loop 1). Khoảng nghỉ giữa 2 thao tác = thời gian thật ±10%.</summary>
        public string Build()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Ghi lúc " + StartedAt.ToString("HH:mm dd/MM") + " - " + acts.Count + " thao tác. Sửa wait / toạ độ / loop tuỳ ý.");
            sb.AppendLine("loop 1");
            DateTime? prevEnd = null;
            foreach (var a in acts)
            {
                if (prevEnd.HasValue)
                {
                    double g = (a.Start - prevEnd.Value).TotalMilliseconds;
                    if (g >= 40) sb.AppendLine("wait  " + I(g * 0.9) + " " + I(g * 1.1));
                }
                double d = Math.Max(60, (a.End - a.Start).TotalMilliseconds);
                if (a.Op == "tap") sb.AppendLine("tap   " + F(a.X) + " " + F(a.Y) + " 0.01");
                else if (a.Op == "hold") sb.AppendLine("hold  " + F(a.X) + " " + F(a.Y) + " " + I(d * 0.9) + " " + I(d * 1.1) + " 0.01");
                else sb.AppendLine("swipe " + F(a.X) + " " + F(a.Y) + " " + F(a.X2) + " " + F(a.Y2) + " " + I(Math.Min(d, 3000) * 0.9) + " " + I(Math.Min(d, 3000) * 1.1));
                prevEnd = a.End;
            }
            return sb.ToString();
        }

        delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    }
}
