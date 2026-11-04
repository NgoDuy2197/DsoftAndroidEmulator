using System; // Kiểu cơ bản: Math, Convert, Environment
using System.Management; // WMI: ManagementObjectSearcher để hỏi thông tin phần cứng

namespace DsoftEmu // Không gian tên chung của app
{
    /// <summary>Đọc thông số máy thật và đề xuất cấu hình giả lập.</summary>
    public class HostInfo
    {
        public string Cpu = "?"; // Tên CPU, "?" khi chưa đọc được
        public int Cores; // Số nhân vật lý
        public int Threads; // Số luồng logic
        public long RamMB; // Tổng RAM (MB)
        public string Gpu = "?"; // Tên card đồ hoạ, "?" khi chưa đọc được
        public long VramMB; // Bộ nhớ card đồ hoạ (MB)
        public bool Hypervisor; // Windows đang chạy hypervisor hay không

        /// <summary>Dò CPU, RAM, GPU qua WMI; lỗi phần nào thì bỏ qua phần đó.</summary>
        public static HostInfo Detect()
        {
            var h = new HostInfo { Threads = Environment.ProcessorCount }; // Tạo đối tượng, số luồng lấy từ .NET
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name,NumberOfCores FROM Win32_Processor")) // Truy vấn WMI thông tin CPU
                    foreach (ManagementObject o in s.Get()) // Duyệt từng CPU (máy nhiều socket có nhiều dòng)
                    {
                        h.Cpu = Convert.ToString(o["Name"]).Trim(); // Lấy tên CPU, bỏ khoảng trắng thừa
                        h.Cores += Convert.ToInt32(o["NumberOfCores"]); // Cộng dồn số nhân
                    }
            }
            catch { } // Lỗi WMI thì bỏ qua
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory,HypervisorPresent FROM Win32_ComputerSystem")) // Truy vấn WMI RAM và hypervisor
                    foreach (ManagementObject o in s.Get()) // Duyệt kết quả (thường chỉ 1 dòng)
                    {
                        h.RamMB = Convert.ToInt64(o["TotalPhysicalMemory"]) / (1024 * 1024); // Đổi byte sang MB
                        h.Hypervisor = Convert.ToBoolean(o["HypervisorPresent"]); // Có hypervisor đang chạy không
                    }
            }
            catch { } // Lỗi WMI thì bỏ qua
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT Name,AdapterRAM FROM Win32_VideoController")) // Truy vấn WMI các card đồ hoạ
                    foreach (ManagementObject o in s.Get()) // Duyệt từng card
                    {
                        long vram = o["AdapterRAM"] == null ? 0 : Convert.ToInt64(o["AdapterRAM"]) / (1024 * 1024); // VRAM (MB), không có thì 0
                        if (h.Gpu == "?" || vram > h.VramMB) { h.Gpu = Convert.ToString(o["Name"]); h.VramMB = vram; } // Giữ card có VRAM lớn nhất
                    }
            }
            catch { } // Lỗi WMI thì bỏ qua
            if (h.Cores == 0) h.Cores = Math.Max(1, h.Threads / 2); // Không đọc được số nhân thì ước lượng = nửa số luồng
            return h; // Trả thông tin máy
        }

        /// <summary>Có card đồ hoạ rời (NVIDIA/Radeon/Arc, VRAM >= 2000 MB) hay không.</summary>
        public bool DiscreteGpu
        {
            get
            {
                string g = Gpu.ToLowerInvariant(); // Đưa tên GPU về chữ thường để so sánh
                return (g.Contains("nvidia") || g.Contains("radeon") || g.Contains("arc")) && VramMB >= 2000; // Đúng hãng card rời và đủ VRAM
            }
        }

        /// <summary>Ghi cấu hình giả lập đề xuất (nhân, RAM, độ phân giải, DPI, GPU) vào Settings.</summary>
        public void ApplyRecommended(Settings s)
        {
            // Giữ lại một nửa luồng cho Windows để không giật.
            int cores = Math.Max(2, Math.Min(6, Math.Min(Cores, Threads / 2))); // Số nhân cho giả lập: trong khoảng 2..6
            int ram = RamMB >= 15000 ? 4096 : RamMB >= 7500 ? 3072 : 2048; // RAM máy >= 16 GB -> 4 GB, >= 8 GB -> 3 GB, còn lại 2 GB
            s.Set("Cores", cores); // Lưu số nhân
            s.Set("RamMB", ram); // Lưu RAM
            s.Set("Width", DiscreteGpu ? 1600 : 1280); // Chiều rộng màn hình: có card rời thì cao hơn
            s.Set("Height", DiscreteGpu ? 900 : 720); // Chiều cao màn hình tương ứng
            s.Set("Dpi", 240); // Mật độ điểm ảnh
            s.Set("Gpu", "host"); // Dùng GPU máy thật để dựng hình
        }

        /// <summary>Chuỗi mô tả thông số máy để hiển thị trên giao diện.</summary>
        public override string ToString()
        {
            return "CPU: " + Cpu + "  (" + Cores + " nhân / " + Threads + " luồng)\r\n" + // Dòng 1: CPU, số nhân/luồng
                   "RAM: " + (RamMB / 1024.0).ToString("0.0") + " GB    GPU: " + Gpu + " (" + VramMB + " MB)\r\n" + // Dòng 2: RAM (GB) và GPU
                   "Hypervisor của Windows: " + (Hypervisor ? "đang chạy" : "chưa chạy") + "  (trạng thái WHPX xem ở mục 'Ảo hoá phần cứng' bên trên)"; // Dòng 3: trạng thái hypervisor
        }
    }
}
