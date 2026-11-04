# Dsoft Android Emulator

Lightweight Android emulator launcher for Windows: runs the official Google Android Emulator (QEMU + WHPX + GPU host),
plus key mapping and a humanized auto clicker.

## Build
```bat
build.bat        -> bin\DsoftEmu.exe   (dùng csc.exe có sẵn của Windows, không cần cài .NET SDK)
```

## Chuẩn bị (1 lần)
1. Bật **Windows Hypervisor Platform** (CMD quyền Admin), rồi khởi động lại máy:
   ```bat
   dism /online /enable-feature /featurename:HypervisorPlatform /all
   ```
2. Mở `bin\DsoftEmu.exe` → bấm **① Tải SDK + image** (tự tải ~1.8 GB vào `bin\sdk`), hoặc tải tay:

| Tài nguyên | Link | Giải nén vào |
|---|---|---|
| platform-tools | https://dl.google.com/android/repository/platform-tools-latest-windows.zip | `bin\sdk\platform-tools\` |
| emulator | https://dl.google.com/android/repository/emulator-windows_x64-16740729.zip | `bin\sdk\emulator\` |
| Android 11 Play Store x86_64 (có dịch ARM) | https://dl.google.com/android/repository/sys-img/google_apis_playstore/x86_64-30_r10-windows.zip | `bin\sdk\system-images\android-30\google_apis_playstore\x86_64\` |
| scrcpy-server 2.7 (input nhanh) | https://github.com/Genymobile/scrcpy/releases/download/v2.7/scrcpy-server-v2.7 | `bin\sdk\scrcpy-server-v2.7` (giữ nguyên tên) |

   Danh sách bản mới nhất: `https://dl.google.com/android/repository/repository2-3.xml`.
   Tải tay thì tạo thêm thư mục rỗng `bin\sdk\platforms\`.

3. **▶ Chạy** → chờ boot xong → dòng "Input: scrcpy-server" màu xanh là map phím/macro dùng được.

## Cấu trúc
| File | Vai trò |
|---|---|
| `src/HostInfo.cs` | Đọc CPU/RAM/GPU, đề xuất cấu hình |
| `src/SdkInstaller.cs` | Tải và kiểm tra checksum gói SDK từ kho Google |
| `src/Emulator.cs` | Tạo AVD (không cần Java), chạy/dừng, watchdog tự restart khi crash |
| `src/Input.cs` | Bơm cảm ứng qua scrcpy-server (đa điểm) / adb input dự phòng, humanizer |
| `src/KeyMapper.cs` | Hook bàn phím → chạm, joystick WASD |
| `src/Macro.cs` | Chạy kịch bản auto click |
| `keymaps/default.txt`, `macros/*.txt` | Cú pháp có chú thích ngay trong file |
