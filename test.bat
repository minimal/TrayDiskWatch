@echo off
rem Builds and runs the TrayDiskWatch test suite.
rem Compiles TrayDiskWatch.cs together with TrayDiskWatch.Tests.cs under
rem /define:TESTS, which swaps the tray entry point for the test runner.
rem No test framework, no packages. Exits non-zero if any check fails.
setlocal
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set EXE=%~dp0TrayDiskWatch.Tests.exe
%CSC% /nologo /define:TESTS /target:exe /out:"%EXE%" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.WindowsRuntime.dll ^
  /r:System.Runtime.dll /r:System.Runtime.InteropServices.WindowsRuntime.dll ^
  /r:"C:\Program Files (x86)\Windows Kits\10\UnionMetadata\10.0.26100.0\Windows.winmd" ^
  "%~dp0TrayDiskWatch.cs" "%~dp0TrayDiskWatch.Tests.cs"
if errorlevel 1 (
  echo.
  echo BUILD FAILED - tests did not run.
  exit /b 1
)
"%EXE%"
set RESULT=%ERRORLEVEL%
echo.
if %RESULT% NEQ 0 (
  echo TESTS FAILED
) else (
  echo TESTS PASSED
)
exit /b %RESULT%
