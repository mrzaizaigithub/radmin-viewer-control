@echo off
rem Builds bin\RadminAuto.exe with the C# compiler that ships with .NET Framework 4.x.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo csc.exe not found. Install .NET Framework 4.x.
    exit /b 1
)
cd /d "%~dp0"
if not exist bin mkdir bin
"%CSC%" /nologo /target:exe /platform:anycpu /optimize+ /out:bin\RadminAuto.exe src\RadminAuto.cs
if errorlevel 1 exit /b 1
copy /y install.bat bin\ >nul
echo Built bin\RadminAuto.exe (+ bin\install.bat)
