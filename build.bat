@echo off
rem Builds TrayDiskWatch with the .NET Framework compiler.
rem Explorer gets a console window that dies with the script, so it pauses to
rem keep the errors readable. An existing shell keeps its own window and gets
rem the exit status instead, so callers can detect a failed build.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

rem Use the newest Windows SDK winmd rather than pinning a version.
set WINMD=
for /f "delims=" %%d in ('dir /b /ad /o-n "C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.*" 2^>nul') do (
  if not defined WINMD set "WINMD=C:\Program Files (x86)\Windows Kits\10\UnionMetadata\%%d\Windows.winmd"
)
if not defined WINMD (
  echo BUILD FAILED - no Windows SDK UnionMetadata found.
  set RESULT=1
  goto :done
)

set RESULT=0
%CSC% /nologo /target:winexe /win32icon:icon.ico /out:TrayDiskWatch.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.WindowsRuntime.dll ^
  /r:System.Runtime.dll /r:System.Runtime.InteropServices.WindowsRuntime.dll ^
  /r:"%WINMD%" ^
  TrayDiskWatch.cs
set RESULT=%ERRORLEVEL%

if %RESULT% NEQ 0 (
  echo.
  echo BUILD FAILED - see errors above.
) else (
  echo Build OK: TrayDiskWatch.exe
)

:done
echo "%cmdcmdline%" | find /i "%~nx0" >nul && pause
exit /b %RESULT%
