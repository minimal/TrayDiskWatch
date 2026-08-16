@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% /nologo /target:winexe /win32icon:icon.ico /out:TrayDiskWatch.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.WindowsRuntime.dll ^
  /r:System.Runtime.dll /r:System.Runtime.InteropServices.WindowsRuntime.dll ^
  /r:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd" ^
  TrayDiskWatch.cs
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see errors above.
) else (
  echo Build OK: TrayDiskWatch.exe
)
pause
