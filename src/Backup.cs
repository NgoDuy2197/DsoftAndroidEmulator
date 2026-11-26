using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DsoftEmu
{
    /// <summary>Sao lưu / khôi phục cấu hình: settings.ini + keymaps\*.txt + macros\*.txt trong 1 file zip.</summary>
    public static class ConfigBackup
    {
        // Khoá gắn với máy này (đường dẫn, máy ảo, cổng, bản Android đã cài) - mặc định KHÔNG khôi phục đè
        public static readonly HashSet<string> MachineKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "SdkDir", "AvdHome", "AvdName", "Instances", "Port", "AdbServerPort", "Api", "Abi", "ImageTag" };

        /// <summary>Tạo zip. withHook = kèm Discord webhook. Trả số file đã đưa vào.</summary>
        public static int Create(string zipPath, string baseDir, Settings s, bool withHook)
        {
            s.Save();
            string ini = Path.Combine(baseDir, "settings.ini");
            string tmp = zipPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            int n = 0;
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                if (File.Exists(ini))
                {
                    var lines = File.ReadAllLines(ini).Where(l => withHook || !l.TrimStart().StartsWith("DiscordWebhook=", StringComparison.OrdinalIgnoreCase));
                    using (var w = new StreamWriter(z.CreateEntry("settings.ini").Open(), new UTF8Encoding(false)))
                        foreach (var l in lines) w.WriteLine(l);
                    n++;
                }
                foreach (var dir in new[] { "keymaps", "macros" })
                {
                    string d = Path.Combine(baseDir, dir);
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.txt")) { z.CreateEntryFromFile(f, dir + "/" + Path.GetFileName(f)); n++; }
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
            File.Move(tmp, zipPath);
            return n;
        }

        /// <summary>Khôi phục: ghi đè keymap/macro trùng tên, gộp settings vào s (bỏ khoá của máy này nếu keepMachine,
        /// webhook rỗng không đè webhook đang có). Trả mô tả ngắn.</summary>
        public static string Restore(string zipPath, string baseDir, Settings s, bool keepMachine)
        {
            int files = 0, keys = 0, skipped = 0;
            using (var z = ZipFile.OpenRead(zipPath))
            {
                foreach (var en in z.Entries)
                {
                    string name = en.FullName.Replace('\\', '/');
                    if (name.Equals("settings.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var r = new StreamReader(en.Open(), Encoding.UTF8))
                        {
                            string line;
                            while ((line = r.ReadLine()) != null)
                            {
                                int i = line.IndexOf('=');
                                if (i <= 0 || line.StartsWith("#")) continue;
                                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                                if (keepMachine && (MachineKeys.Contains(k) || v.Contains(":\\"))) { skipped++; continue; }
                                if (k.Equals("DiscordWebhook", StringComparison.OrdinalIgnoreCase) && v.Length == 0) continue;
                                s.Set(k, v); keys++;
                            }
                        }
                        continue;
                    }
                    // Chỉ nhận keymaps/<tên>.txt và macros/<tên>.txt (chặn đường dẫn lạ như ../)
                    var parts = name.Split('/');
                    if (parts.Length != 2 || (parts[0] != "keymaps" && parts[0] != "macros") || !parts[1].EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                        || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || parts[1].StartsWith(".")) continue;
                    string dst = Path.Combine(baseDir, parts[0], parts[1]);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    en.ExtractToFile(dst, true);
                    files++;
                }
            }
            s.Save();
            return keys + " mục cấu hình, " + files + " file keymap/macro" + (skipped > 0 ? ", giữ nguyên " + skipped + " mục riêng của máy này" : "");
        }
    }
}
