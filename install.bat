@echo off
rem ---------------------------------------------------------------------------
rem  Cai Radmin Viewer (neu chua co) + cai va dang ky RadminAuto.exe
rem
rem  De file nay CUNG THU MUC voi RadminAuto.exe roi double-click.
rem  Neu co san file Radmin_Viewer_*.msi trong thu muc nay thi dung file do,
rem  khong thi tai tu RADMIN_MSI_URL ben duoi.
rem
rem  Chay binh thuong (KHONG chon "Run as administrator"): link radminauto://
rem  duoc dang ky cho nguoi dung dang dang nhap; chi buoc cai Radmin moi hoi
rem  quyen Administrator.
rem
rem  Tham so: /nopause  khong dung lai o cuoi (dung khi trien khai tu dong)
rem ---------------------------------------------------------------------------
setlocal EnableExtensions

set "RADMIN_MSI_URL=https://www.radmin.com/download/Radmin_Viewer_3.5.2.1_EN.msi"
set "INSTALL_DIR=%LOCALAPPDATA%\RadminAuto"
set "HERE=%~dp0"
set "RESULT=1"

echo.
echo [1/3] Kiem tra Radmin Viewer...
call :find_radmin
if defined RADMIN_EXE goto radmin_ok

set "MSI="
for %%F in ("%HERE%Radmin_Viewer*.msi") do set "MSI=%%~fF"
if defined MSI goto have_msi

set "MSI=%TEMP%\Radmin_Viewer_setup.msi"
echo       Dang tai Radmin Viewer: %RADMIN_MSI_URL%
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -UseBasicParsing -Uri '%RADMIN_MSI_URL%' -OutFile '%MSI%'"
if errorlevel 1 goto download_failed
if not exist "%MSI%" goto download_failed

:have_msi
echo       Cai dat tu: "%MSI%"
echo       Windows se hoi quyen Administrator, hay chon Yes.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Start-Process msiexec.exe -ArgumentList '/i \"%MSI%\" /qn /norestart' -Verb RunAs -Wait -PassThru; exit $p.ExitCode"
set "MSI_EXIT=%ERRORLEVEL%"
if "%MSI_EXIT%"=="3010" set "MSI_EXIT=0"
if not "%MSI_EXIT%"=="0" goto install_failed

call :find_radmin
if not defined RADMIN_EXE goto install_failed

:radmin_ok
echo       OK: "%RADMIN_EXE%"

echo.
echo [2/3] Chep RadminAuto.exe vao "%INSTALL_DIR%"...
if not exist "%HERE%RadminAuto.exe" goto no_radminauto
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
copy /y "%HERE%RadminAuto.exe" "%INSTALL_DIR%\RadminAuto.exe" >nul
if errorlevel 1 goto copy_failed
echo       OK

echo.
echo [3/3] Dang ky link radminauto://...
"%INSTALL_DIR%\RadminAuto.exe" --register
if errorlevel 1 goto register_failed

echo.
echo HOAN TAT. Bam link radminauto://... tren web de mo Radmin va tu dang nhap.
set "RESULT=0"
goto end

:download_failed
echo.
echo LOI: Khong tai duoc Radmin Viewer.
echo      Tai file .msi tu https://www.radmin.com/download/ , de cung thu muc voi
echo      file .bat nay (ten bat dau bang Radmin_Viewer) roi chay lai.
goto end

:install_failed
echo.
echo LOI: Cai Radmin Viewer khong thanh cong (ma loi msiexec: %MSI_EXIT%).
echo      Neu vua bam No o hop thoai quyen Administrator thi chay lai va chon Yes.
goto end

:no_radminauto
echo.
echo LOI: Khong thay "%HERE%RadminAuto.exe". De file .bat cung thu muc voi RadminAuto.exe.
goto end

:copy_failed
echo.
echo LOI: Khong chep duoc RadminAuto.exe (dang chay?). Dong RadminAuto roi chay lai.
goto end

:register_failed
echo.
echo LOI: Dang ky link radminauto:// khong thanh cong.
goto end

:find_radmin
set "RADMIN_EXE="
if exist "%ProgramFiles(x86)%\Radmin Viewer 3\Radmin.exe" set "RADMIN_EXE=%ProgramFiles(x86)%\Radmin Viewer 3\Radmin.exe"
if defined RADMIN_EXE exit /b 0
if exist "%ProgramFiles%\Radmin Viewer 3\Radmin.exe" set "RADMIN_EXE=%ProgramFiles%\Radmin Viewer 3\Radmin.exe"
exit /b 0

:end
echo.
if /i not "%~1"=="/nopause" pause
exit /b %RESULT%
