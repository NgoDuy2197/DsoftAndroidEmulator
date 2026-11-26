namespace DsoftEmu
{
    /// <summary>Mẫu chèn nhanh cho trang Map phím và Auto click. Toạ độ chỉ là ví dụ - sửa bằng "Lấy toạ độ".</summary>
    public static class Templates
    {
        // ---------- Map phím: chèn vào vị trí con trỏ ----------
        public static readonly string[][] Keymap =
        {
            new[] { "Cú pháp combo / repeat (giải thích)",
@"# ===== combo / repeat =====
#   combo  <Phím> bước; bước; ...   bấm 1 lần = chạy chuỗi 1 lần (đang chạy thì bỏ qua)
#   repeat <Phím> bước; bước; ...   GIỮ phím = lặp chuỗi liên tục, NHẢ = dừng
# Bước dùng cú pháp macro: tap x y [r] | dtap x y [r] | hold x y minMs maxMs [r]
#                          swipe x1 y1 x2 y2 minMs maxMs | wait minMs [maxMs]
# Ví dụ:
#   combo  F  tap 0.82 0.88; wait 80 120; tap 0.88 0.62
#   repeat G  tap 0.90 0.78 0.01; wait 60 90
" },

            new[] { "Game hành động / MOBA (WASD + kỹ năng + combo)",
@"# ===== Hành động / MOBA =====
toggle F12                                   # bật/tắt map phím
joy W A S D   0.150 0.750 0.090              # joystick di chuyển

tap Space  0.900 0.780 0.015                 # đánh thường
tap Q      0.740 0.900                       # kỹ năng 1
tap E      0.800 0.700                       # kỹ năng 2
tap R      0.870 0.600                       # kỹ năng 3 / ulti
tap F      0.680 0.880                       # phép bổ trợ
tap B      0.600 0.900                       # về nhà / hồi máu

# Combo 1 phím: Q -> E -> R -> đánh thường x2
combo Z    tap 0.74 0.90; wait 90 140; tap 0.80 0.70; wait 90 140; tap 0.87 0.60; wait 120 180; tap 0.90 0.78; wait 70 110; tap 0.90 0.78
# Giữ X = spam đánh thường
repeat X   tap 0.90 0.78 0.01; wait 60 100
# Né: lướt từ joystick sang phải
combo C    swipe 0.15 0.75 0.25 0.75 80 120
" },

            new[] { "Game bắn súng (FPS/TPS)",
@"# ===== Bắn súng =====
toggle F12
joy W A S D   0.150 0.700 0.090

tap Space       0.920 0.620               # nhảy
tap C           0.880 0.880               # ngồi
tap Z           0.940 0.880               # nằm
tap R           0.800 0.880               # nạp đạn
tap D1          0.420 0.900               # súng 1
tap D2          0.580 0.900               # súng 2
tap G           0.720 0.880               # lựu đạn
tap F           0.640 0.560               # nhặt / tương tác
tap LShiftKey   0.150 0.450               # chạy nhanh
tap Q           0.780 0.420               # ngắm (scope)

# Giữ E = bắn tỉa liên tục (chạm nhả nhanh)
repeat E   tap 0.860 0.520 0.008; wait 90 140
# V = bắn nhanh 3 viên rồi ngồi
combo V    tap 0.86 0.52; wait 70 100; tap 0.86 0.52; wait 70 100; tap 0.86 0.52; wait 50 80; tap 0.88 0.88
" },

            new[] { "Game nhập vai / idle (nút menu + nhận thưởng)",
@"# ===== Nhập vai / idle =====
toggle F12
joy W A S D   0.150 0.750 0.090

tap Space  0.900 0.780              # tấn công
tap D1     0.700 0.900              # kỹ năng 1
tap D2     0.770 0.800              # kỹ năng 2
tap D3     0.840 0.700              # kỹ năng 3
tap H      0.600 0.900              # bình máu
tap M      0.900 0.080              # bản đồ
tap Tab    0.050 0.060              # menu
tap Escape 0.950 0.060              # đóng cửa sổ

# N = nhận thưởng: mở hộp thư -> nhận tất cả -> đóng
combo N    tap 0.95 0.18; wait 600 900; tap 0.80 0.88; wait 500 800; tap 0.95 0.06
# Giữ T = chạm liên tục giữa màn hình (bỏ qua hội thoại)
repeat T   tap 0.50 0.60 0.03; wait 150 250
# Bật/tắt macro farm trong thư mục macros (tạo ở trang Auto click)
macro F9   farm_vong_lap
" },

            new[] { "Phím một dòng: combo",       "combo F    tap 0.80 0.80; wait 80 120; tap 0.85 0.70\r\n" },
            new[] { "Phím một dòng: repeat (giữ để spam)", "repeat G   tap 0.90 0.78 0.01; wait 60 90\r\n" },
            new[] { "Phím một dòng: vuốt lên",    "combo U    swipe 0.50 0.70 0.50 0.30 150 250\r\n" },
            new[] { "Phím một dòng: giữ lâu (gồng/charge)", "combo H    hold 0.90 0.78 800 1000\r\n" },
        };

        // ---------- Auto click: tạo file macro mới theo mẫu (tên file, nội dung) ----------
        public static readonly string[][] Macros =
        {
            new[] { "farm_vong_lap", "Farm lặp vô hạn (đánh + nhặt + nghỉ)",
@"# Farm: đánh, dùng kỹ năng, nhặt đồ - lặp vô hạn, thỉnh thoảng nghỉ dài
loop 0
tap   0.900 0.780 0.015     # đánh thường
wait  250 450
tap   0.900 0.780 0.015
wait  250 450
tap   0.740 0.900           # kỹ năng 1
wait  500 900
tap   0.800 0.700           # kỹ năng 2
wait  800 1400
tap   0.640 0.560           # nhặt đồ
wait  1500 2500
break 4 15000 40000         # 4% mỗi vòng nghỉ 15-40s
" },
            new[] { "spam_cham", "Spam chạm 1 điểm",
@"# Chạm liên tục một điểm (vd nút đánh / bỏ qua hội thoại)
loop 0
tap  0.500 0.600 0.02
wait 180 320
break 2 5000 12000
" },
            new[] { "nhan_thuong", "Nhận thưởng hằng ngày (chạy 1 lần)",
@"# Mở hộp thư -> nhận tất cả -> đóng; mở sự kiện -> nhận -> đóng
loop 1
tap  0.950 0.180           # hộp thư
wait 900 1400
tap  0.800 0.880           # nhận tất cả
wait 800 1200
tap  0.500 0.800           # xác nhận
wait 600 900
tap  0.950 0.060           # đóng
wait 900 1400
tap  0.880 0.180           # sự kiện
wait 900 1400
tap  0.700 0.850           # nhận
wait 800 1200
tap  0.950 0.060           # đóng
" },
            new[] { "cuon_danh_sach", "Cuộn danh sách + chọn",
@"# Vuốt lên để cuộn, chạm mục đầu, quay lại
loop 10
swipe 0.500 0.750 0.500 0.350 300 500
wait  600 900
tap   0.500 0.300
wait  1000 1600
tap   0.050 0.060          # nút quay lại
wait  800 1200
" },
            new[] { "gong_nha", "Giữ (gồng) rồi nhả, lặp",
@"# Giữ nút 1-1.5s rồi nhả, lặp vô hạn (câu cá, gồng chiêu...)
loop 0
hold 0.900 0.780 1000 1500 0.01
wait 1500 2500
tap  0.500 0.500 0.03       # đóng kết quả
wait 800 1200
" },
            new[] { "combo_ky_nang", "Combo kỹ năng (lặp 5 lần)",
@"# Chuỗi kỹ năng 1-2-3 + đánh thường, lặp 5 lần
loop 5
tap  0.740 0.900
wait 90 140
tap  0.800 0.700
wait 90 140
tap  0.870 0.600
wait 120 180
dtap 0.900 0.780
wait 2500 3500              # chờ hồi chiêu
" },
        };
    }
}
