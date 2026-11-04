# Chuyen PNG (bat ky kich thuoc) thanh file .ico nhieu co cho csc /win32icon.
# Anh khong vuong duoc dem vao giua mot o vuong to bang mau goc tren-trai cua anh.
# Tham số bắt buộc: -Src (ảnh PNG nguồn) và -Dst (file .ico đích)
param([Parameter(Mandatory = $true)][string]$Src, [Parameter(Mandatory = $true)][string]$Dst)
$ErrorActionPreference = 'Stop' # Gặp lỗi bất kỳ thì dừng script ngay (exit code khác 0)
Add-Type -AssemblyName System.Drawing # Nạp thư viện xử lý ảnh System.Drawing

# --- Bước 1: đệm ảnh gốc thành ảnh vuông ---
$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Src)) # Mở ảnh nguồn (đổi sang đường dẫn tuyệt đối)
try {
    $side = [Math]::Max($img.Width, $img.Height) # Cạnh ô vuông = cạnh dài nhất của ảnh
    $bg = $img.GetPixel(0, 0) # Lấy màu điểm góc trên-trái làm màu nền
    $square = New-Object System.Drawing.Bitmap $side, $side # Tạo ảnh vuông trống
    $g = [System.Drawing.Graphics]::FromImage($square) # Lấy bút vẽ lên ảnh vuông
    $g.Clear($bg) # Tô toàn bộ bằng màu nền
    $g.DrawImage($img, [int](($side - $img.Width) / 2), [int](($side - $img.Height) / 2), $img.Width, $img.Height) # Vẽ ảnh gốc vào giữa ô vuông
    $g.Dispose() # Giải phóng bút vẽ
} finally { $img.Dispose() } # Luôn đóng ảnh gốc (nhả khoá file)

# --- Bước 2: tạo dữ liệu cho từng cỡ icon ---
$sizes = 16, 24, 32, 48, 64, 128, 256 # Các cỡ icon (pixel) sẽ đưa vào file .ico
$pngs = @() # Mảng chứa dữ liệu byte của từng cỡ
foreach ($s in $sizes) { # Lặp qua từng cỡ
    $bmp = New-Object System.Drawing.Bitmap $s, $s # Tạo ảnh trống cỡ s x s
    $g = [System.Drawing.Graphics]::FromImage($bmp) # Lấy bút vẽ lên ảnh nhỏ
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic # Thu nhỏ bằng nội suy bicubic chất lượng cao
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality # Khử răng cưa chất lượng cao
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality # Căn điểm ảnh chính xác hơn khi co
    $g.DrawImage($square, 0, 0, $s, $s) # Vẽ ảnh vuông co về cỡ s x s
    $g.Dispose() # Giải phóng bút vẽ
    $ms = New-Object System.IO.MemoryStream # Bộ nhớ chứa dữ liệu của cỡ này
    if ($s -ge 256) { # Cỡ 256 trở lên
        # 256px: PNG nen cho nhe
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png) # Lưu dạng PNG nén
    } else { # Cỡ nhỏ hơn 256
        # nho hon: DIB 32bpp (BITMAPINFOHEADER + pixel BGRA tu duoi len + AND mask) - moi trinh doc icon deu hieu
        $bw = New-Object System.IO.BinaryWriter $ms # Bộ ghi nhị phân vào bộ nhớ
        # BITMAPINFOHEADER: kích thước header 40, rộng s, cao s*2 (ảnh màu + mặt nạ AND)
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        # 1 mặt phẳng màu, 32 bit/điểm ảnh, không nén (BI_RGB = 0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4) # Số byte mỗi hàng mặt nạ AND (1 bit/điểm, làm tròn lên bội 4 byte)
        $bw.Write([UInt32]($s * $s * 4 + $maskStride * $s)) # Kích thước dữ liệu ảnh = pixel màu + mặt nạ
        # Độ phân giải X/Y = 0, số màu bảng màu = 0, số màu quan trọng = 0
        $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { # Ghi các hàng từ dưới lên (chuẩn DIB)
            for ($x = 0; $x -lt $s; $x++) { # Duyệt từng điểm trong hàng từ trái sang phải
                $c = $bmp.GetPixel($x, $y) # Lấy màu điểm ảnh
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) # Ghi theo thứ tự B, G, R, A
            }
        }
        $bw.Write((New-Object byte[] ($maskStride * $s))) # Ghi mặt nạ AND toàn 0 (độ trong suốt đã nằm ở kênh A)
        $bw.Flush() # Đẩy hết dữ liệu xuống bộ nhớ
    }
    $bmp.Dispose() # Giải phóng ảnh nhỏ
    $pngs += , $ms.ToArray() # Thêm mảng byte vào danh sách (dấu phẩy để thêm nguyên mảng làm 1 phần tử)
}
$square.Dispose() # Giải phóng ảnh vuông

# ICONDIR + ICONDIRENTRY (16 byte/anh), du lieu moi anh la PNG (Windows Vista tro len ho tro)
New-Item -ItemType Directory -Force (Split-Path -Parent ([System.IO.Path]::GetFullPath($Dst))) | Out-Null # Tạo thư mục chứa file đích nếu chưa có
$out = New-Object System.IO.MemoryStream # Bộ nhớ chứa toàn bộ file .ico
$w = New-Object System.IO.BinaryWriter $out # Bộ ghi nhị phân cho file .ico
# ICONDIR: reserved = 0, kiểu = 1 (icon), số ảnh trong file
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count # Vị trí dữ liệu ảnh đầu tiên = 6 byte ICONDIR + 16 byte mỗi entry
for ($i = 0; $i -lt $sizes.Count; $i++) { # Ghi một ICONDIRENTRY cho mỗi cỡ
    $s = $sizes[$i] # Cỡ ảnh hiện tại
    # Ghi chiều rộng và chiều cao (1 byte mỗi giá trị)
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 ghi la 0
    # Số màu bảng màu = 0, byte dự phòng = 0
    $w.Write([byte]0); $w.Write([byte]0)
    # Số mặt phẳng màu = 1, 32 bit/điểm ảnh
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    # Kích thước dữ liệu ảnh và vị trí bắt đầu của nó trong file
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length # Dời vị trí cho ảnh kế tiếp
}
foreach ($p in $pngs) { $w.Write($p) } # Ghi lần lượt dữ liệu từng ảnh sau phần entry
$w.Flush() # Đẩy hết dữ liệu xuống bộ nhớ
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Dst), $out.ToArray()) # Ghi toàn bộ ra file .ico đích
$w.Dispose() # Giải phóng bộ ghi
