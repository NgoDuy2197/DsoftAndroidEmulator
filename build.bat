@rem Tat hien lenh ra man hinh
@echo off
rem Bien moi tao chi co hieu luc trong script nay
setlocal
rem Chuyen thu muc lam viec ve thu muc chua build.bat
cd /d "%~dp0"

rem Dung csc.exe co san trong Windows (.NET Framework 4.8) - khong can cai them gi.
rem Chay "build.bat nopause" de khong dung man hinh khi xong.
rem Kiem tra .NET Framework 4.8: chua co thi tu tai + cai, khong duoc thi in huong dan roi dung
call :ensure_net48
if errorlevel 1 goto :fail

rem Duong dan csc.exe ban 64-bit
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
rem Khong co ban 64-bit thi dung ban 32-bit
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
rem Van khong thay csc.exe thi bao loi va dung
if not exist "%CSC%" (
  echo [LOI] Khong tim thay csc.exe. Hay cai .NET Framework 4.8.
  goto :fail
)

rem Kiem tra DsoftEmu.exe co dang chay khong - neu co thi file exe bi khoa, khong ghi de duoc
tasklist /FI "IMAGENAME eq DsoftEmu.exe" 2>nul | find /I "DsoftEmu.exe" >nul
rem errorlevel 0 nghia la tim thay tien trinh: bao loi va dung
if not errorlevel 1 (
  echo [LOI] DsoftEmu.exe dang chay - tat app roi build lai.
  goto :fail
)

rem Tao thu muc bin neu chua co
if not exist bin mkdir bin

rem Icon: chuyen ico.png -> obj\app.ico (nhieu co) roi nhung vao exe
rem Mac dinh khong co tham so icon
set "ICONARG="
rem Neu co ico.png: goi make-ico.ps1 tao obj\app.ico; that bai thi canh bao va build khong icon, thanh cong thi them tham so /win32icon
if exist ico.png (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\make-ico.ps1" -Src ico.png -Dst obj\app.ico
  if errorlevel 1 (
    echo [CANH BAO] Khong tao duoc icon tu ico.png, build khong icon.
  ) else (
    set "ICONARG=/win32icon:obj\app.ico"
  )
)

rem Bien dich tat ca src\*.cs thanh bin\DsoftEmu.exe: UTF-8, app cua so, x64, toi uu, kem icon va cac thu vien .NET can dung
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ /warn:3 %ICONARG% ^
  /out:bin\DsoftEmu.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  src\*.cs
rem Bien dich loi thi bao loi va dung
if errorlevel 1 (
  echo [LOI] Build that bai.
  goto :fail
)

rem Tao thu muc bin\keymaps neu chua co
if not exist bin\keymaps mkdir bin\keymaps
rem Tao thu muc bin\macros neu chua co
if not exist bin\macros mkdir bin\macros
rem Copy keymap mac dinh sang bin neu chua co - khong ghi de ban nguoi dung da sua
if not exist bin\keymaps\default.txt copy /y keymaps\default.txt bin\keymaps\ >nul
rem Copy tung macro mau sang bin\macros neu file do chua co
for %%f in (macros\*.txt) do if not exist "bin\macros\%%~nxf" copy /y "%%f" bin\macros\ >nul

rem In dong trong
echo.
rem Bao build thanh cong kem duong dan exe
echo [OK] Da build: %~dp0bin\DsoftEmu.exe
rem Khong truyen tham so thi dung man hinh cho nguoi dung doc
if "%~1"=="" pause
rem Thoat voi ma 0 - thanh cong
exit /b 0

rem Nhan loi: dung man hinh neu khong co tham so, roi thoat voi ma 1
:fail
if "%~1"=="" pause
exit /b 1

rem ============================================================
rem Ham kiem tra / tu cai .NET Framework 4.8. Tra ve 0 = san sang, 1 = chua dung duoc
rem ============================================================
:ensure_net48
rem Doc Release trong registry; >= 528040 nghia la da co .NET Framework 4.8 tro len (Microsoft quy dinh)
set "NETREL=0"
for /f "tokens=3" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| find /i "Release"') do set /a "NETREL=%%a"
rem Da du phien ban thi thoat thanh cong
if %NETREL% GEQ 528040 exit /b 0

echo [THONG BAO] Chua co .NET Framework 4.8 - dang tai bo cai dat offline tu Microsoft (~120 MB) ...
rem Link offline installer chinh thuc cua Microsoft
set "NETURL=https://go.microsoft.com/fwlink/?linkid=2088631"
rem Luu file cai vao thu muc tam cua Windows
set "NETEXE=%TEMP%\ndp48-x86-x64-allos-enu.exe"
rem Tai bang PowerShell, bat TLS 1.2 cho Windows cu
powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; $ProgressPreference='SilentlyContinue'; try { Invoke-WebRequest -Uri '%NETURL%' -OutFile '%NETEXE%' -UseBasicParsing } catch { exit 1 }"
rem Tai loi (khong co mang, bi chan...) thi chuyen sang huong dan cai tay
if errorlevel 1 (
  echo [LOI] Tai bo cai dat that bai.
  goto :net48_help
)

rem Kiem tra chu ky so: file phai hop le va do Microsoft ky, tranh chay file bi tao gia
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=Get-AuthenticodeSignature '%NETEXE%'; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { exit 1 }"
if errorlevel 1 (
  echo [LOI] File cai dat khong co chu ky hop le cua Microsoft - da huy.
  del /q "%NETEXE%" 2>nul
  goto :net48_help
)

echo [THONG BAO] Dang cai .NET Framework 4.8 - bam Yes khi Windows hoi quyen Admin, cho vai phut ...
rem Chay ngam (/q), khong tu khoi dong lai (/norestart), xin quyen Admin qua UAC; tu choi UAC tra ve 1602
powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $p=Start-Process -FilePath '%NETEXE%' -ArgumentList '/q','/norestart' -Verb RunAs -Wait -PassThru; exit $p.ExitCode } catch { exit 1602 }"
rem Luu ma ket qua cua bo cai
set "NETRC=%errorlevel%"
rem Xoa file cai sau khi dung xong
del /q "%NETEXE%" 2>nul

rem 3010 / 1641: cai xong nhung can khoi dong lai may
if "%NETRC%"=="3010" goto :net48_reboot
if "%NETRC%"=="1641" goto :net48_reboot
rem 1602: nguoi dung bam No o hop thoai UAC
if "%NETRC%"=="1602" (
  echo [LOI] Da huy cai dat - can quyen Admin.
  goto :net48_help
)
rem Ma khac 0 la loi
if not "%NETRC%"=="0" (
  echo [LOI] Cai dat that bai, ma loi: %NETRC%
  goto :net48_help
)
echo [OK] Da cai .NET Framework 4.8.
exit /b 0

:net48_reboot
echo [OK] Da cai .NET Framework 4.8 - hay KHOI DONG LAI MAY roi chay lai build.bat.
exit /b 1

:net48_help
echo.
echo ===== Huong dan cai tay .NET Framework 4.8 =====
echo  1. Mo trang: https://dotnet.microsoft.com/download/dotnet-framework/net48
echo  2. Tai muc "Runtime" ban Offline installer, chay bang quyen Admin.
echo  3. Khoi dong lai may neu duoc yeu cau, roi chay lai build.bat.
echo  Luu y: Windows 10 (tu ban 1903) va Windows 11 da co san .NET 4.8.
echo  Neu van loi, mo "Turn Windows features on or off" va tick ".NET Framework 4.8 Advanced Services".
echo ================================================
exit /b 1
