@rem Tat hien lenh ra man hinh
@echo off
rem Bien moi tao chi co hieu luc trong script nay
setlocal
rem Chuyen thu muc lam viec ve thu muc chua pack.bat
cd /d "%~dp0"

rem Dong goi ban de copy sang may khac.
rem   pack.bat            -> ban lite: chi exe + keymaps + macros (~100 KB), may moi tu tai SDK + image
rem   pack.bat offline    -> them san emulator + image dang chon (~3.3 GB), khong can mang khi cai
rem Khong bao gio dong goi: du lieu may ao (avd), snapshot, log, cau hinh CPU/RAM cua may nay.

rem Che do mac dinh la lite
set "MODE=lite"
rem Tham so thu nhat la offline thi chuyen sang che do offline
if /i "%~1"=="offline" set "MODE=offline"
rem Thu muc dau ra: dist\DsoftEmu-lite hoac dist\DsoftEmu-offline
set "OUT=dist\DsoftEmu-%MODE%"

rem Goi build.bat o che do khong dung man hinh
call "%~dp0build.bat" nopause
rem Build loi thi dung
if errorlevel 1 goto :fail

rem Xoa thu muc dau ra cu neu co
if exist "%OUT%" rmdir /s /q "%OUT%"
rem Tao thu muc dau ra cung 2 thu muc con keymaps va macros
mkdir "%OUT%\keymaps" "%OUT%\macros"
rem Copy file exe vao thu muc dau ra
copy /y bin\DsoftEmu.exe "%OUT%\" >nul
rem Copy cac file keymap
copy /y keymaps\*.txt "%OUT%\keymaps\" >nul
rem Copy cac file macro
copy /y macros\*.txt "%OUT%\macros\" >nul

rem Chi giu ban Android dang chon; CPU/RAM se tu de xuat theo may moi khi mo lan dau.
rem Neu co bin\settings.ini thi loc rieng cac dong bat dau bang Api= va ImageTag= ghi sang settings.ini moi
if exist bin\settings.ini (
  findstr /b /i "Api= ImageTag=" bin\settings.ini > "%OUT%\settings.ini"
)

rem Che do offline: kiem tra da co SDK, chua co thi bao loi va dung
rem Co roi thi dung robocopy copy ca thu muc sdk, bo qua thu muc tam .tmp, khong in danh sach file
rem robocopy tra ma loi tu 8 tro len la that bai thi bao loi va dung
if /i "%MODE%"=="offline" (
  if not exist bin\sdk\emulator\emulator.exe (
    echo [LOI] Chua co bin\sdk - mo app va tai SDK truoc.
    goto :fail
  )
  echo Copy emulator + platform-tools + image ...
  robocopy bin\sdk "%OUT%\sdk" /E /NFL /NDL /NJH /NJS /NP /XD .tmp >nul
  if errorlevel 8 (
    echo [LOI] Copy sdk that bai.
    goto :fail
  )
)

rem Bao dang nen zip
echo Nen file zip ...
rem Xoa file zip cu neu co
if exist "%OUT%.zip" del /q "%OUT%.zip"
rem Dung tar co san trong Windows nen thu muc thanh file zip - tu dong chon dinh dang theo duoi .zip
tar -a -c -f "%OUT%.zip" -C dist "DsoftEmu-%MODE%"
rem Nen loi thi bao loi va dung
if errorlevel 1 (
  echo [LOI] Nen zip that bai.
  goto :fail
)

rem In dong trong
echo.
rem Bao duong dan thu muc dau ra
echo [OK] Thu muc: %~dp0%OUT%
rem In duong dan day du va kich thuoc file zip
for %%z in ("%OUT%.zip") do echo [OK] File zip: %%~fz  (%%~zz bytes)
rem Khong truyen tham so thu hai thi dung man hinh cho nguoi dung doc
if "%~2"=="" pause
rem Thoat voi ma 0 - thanh cong
exit /b 0

rem Nhan loi: dung man hinh neu khong co tham so thu hai, roi thoat voi ma 1
:fail
if "%~2"=="" pause
exit /b 1
