@echo off
setlocal
rem ---------------------------------------------------------------------------
rem  build-release.bat - double-click to build the LiteBox release artifacts.
rem
rem  Produces, under this folder's release\ (git-ignored), named by the LiteBox version:
rem    LiteBox-Setup-<ver>.exe            the ONE universal installer (works on net9 13.27 AND net10 13.28+)
rem    light\LiteBox-<ver>-net9.zip       manual "extract into Core" alternative (LB 13.27)
rem    light\LiteBox-<ver>-net10.zip      manual "extract into Core" alternative (LB 13.28+)
rem
rem  Uses the PowerShell script's defaults:
rem    net9  SDK ref / version = G:\LB          (a .NET 9 LaunchBox on disk)
rem    net10 SDK ref / version = ..\..\..\LB    (the primary LaunchBox beside the repo)
rem  Override by passing args straight through, e.g.:
rem    build-release.bat -Lb9Root "D:\LB-net9" -Lb10Root "D:\LB-net10"
rem
rem  Requires the .NET SDK 10.x (targets both net9.0-windows and net10.0-windows).
rem  See BUILD-RELEASE.md for the full explanation.
rem ---------------------------------------------------------------------------

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo Build finished. Artifacts are in "%~dp0release".
) else (
  echo BUILD FAILED ^(exit code %RC%^). See the messages above.
)
echo.
pause
exit /b %RC%
