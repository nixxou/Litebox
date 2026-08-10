@echo off
setlocal
cd /d "%~dp0"

if exist "external\minhook\include\MinHook.h" (
    echo MinHook already present.
    exit /b 0
)

echo Downloading MinHook source...
if not exist external mkdir external
cd external

where curl >nul 2>nul
if errorlevel 1 (
    echo curl not found. Install curl or place MinHook source manually under external\minhook\.
    exit /b 1
)

curl -L -o minhook.zip https://github.com/TsudaKageyu/minhook/archive/refs/heads/master.zip
if errorlevel 1 (
    echo Failed to download MinHook.
    exit /b 1
)

tar -xf minhook.zip
if errorlevel 1 (
    echo Failed to extract MinHook.
    exit /b 1
)

if exist minhook rmdir /s /q minhook
ren minhook-master minhook
del minhook.zip

echo MinHook installed under external\minhook\.
exit /b 0
