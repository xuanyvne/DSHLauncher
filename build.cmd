@echo off
setlocal
rem Build DSHLauncher.exe with the in-box .NET Framework C# compiler (no SDK needed).
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" ( echo csc.exe not found & exit /b 1 )
"%FW%\csc.exe" /nologo /target:winexe /out:DSHLauncher.exe /win32icon:DSHLauncher.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.Web.Extensions.dll" ^
  src\Program.cs src\AppLog.cs src\SmartLaunch.cs src\HarnessService.cs src\GitUpdater.cs src\Selftest.cs src\MainForm.cs
if errorlevel 1 ( echo BUILD FAILED & exit /b 1 )
echo BUILD OK: DSHLauncher.exe