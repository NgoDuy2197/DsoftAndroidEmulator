using System; // Kiểu cơ bản: AppDomain, STAThread
using System.IO; // Path
using System.Runtime.InteropServices; // DllImport để gọi hàm Win32
using System.Threading; // Mutex
using System.Windows.Forms; // Application, MessageBox

[assembly: System.Reflection.AssemblyTitle("Dsoft Android Emulator")] // Tên hiển thị trong thuộc tính file exe
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] // Phiên bản assembly

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Điểm vào chương trình: chặn chạy 2 bản, ghi log lỗi, bật DPI và mở cửa sổ chính.</summary>
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware(); // Hàm Win32 báo app tự xử lý DPI (chữ không bị mờ)

        static System.Drawing.Icon appIcon; // Bộ nhớ đệm icon, nạp một lần

        /// <summary>Icon nhúng trong exe (từ ico.png lúc build), dùng cho thanh tiêu đề cửa sổ.</summary>
        public static System.Drawing.Icon AppIcon
        {
            get
            {
                if (appIcon == null) // Chưa nạp icon
                    try { appIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } // Lấy icon từ chính file exe, lỗi thì để null
                return appIcon; // Trả icon (có thể null)
            }
        }

        /// <summary>Hàm Main: khởi động app.</summary>
        [STAThread] // WinForms bắt buộc luồng chính kiểu STA
        static void Main()
        {
            bool created; // true nếu mutex vừa được tạo mới (chưa có bản nào chạy)
            using (new Mutex(true, "DsoftEmu_single_instance", out created)) // Tạo mutex theo tên để chỉ cho chạy một bản
            {
                if (!created) { MessageBox.Show("Dsoft Emulator đang chạy rồi."); return; } // Đã có bản khác chạy: báo và thoát

                Log.Init(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")); // Khởi tạo log ở thư mục logs cạnh exe
                // Lỗi không lường trước chỉ ghi log, không làm sập app.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); // Lỗi ở luồng UI đi vào sự kiện ThreadException
                Application.ThreadException += (s, e) => Log.I("✖ Lỗi UI: " + e.Exception); // Ghi log lỗi luồng giao diện
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.I("✖ Lỗi nghiêm trọng: " + e.ExceptionObject); // Ghi log lỗi ở luồng khác

                try { SetProcessDPIAware(); } catch { } // Bật nhận biết DPI, lỗi thì bỏ qua
                Application.EnableVisualStyles(); // Dùng giao diện control kiểu Windows hiện đại
                Application.SetCompatibleTextRenderingDefault(false); // Vẽ chữ bằng GDI (TextRenderer) mặc định
                Application.Run(new MainForm()); // Mở cửa sổ chính và chạy vòng lặp sự kiện
            }
        }
    }
}
