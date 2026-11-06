@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo Please install the .NET 10 SDK, then run this script again.
    goto :failed
)

echo Stopping the previous daily client...
powershell.exe -NoProfile -Command "$ErrorActionPreference = 'Stop'; $exe = Join-Path (Get-Location) 'output\JeekMusic.exe'; Get-Process | Where-Object { $_.ProcessName -eq 'JeekMusic' -and $_.Path -eq $exe } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $_.WaitForExit() }"
if errorlevel 1 goto :failed

echo Building Jeek Music...
dotnet build "desktop\src\JeekMusic.App"
if errorlevel 1 goto :failed

if not exist "output\JeekMusic.exe" (
    echo Build output is missing: output\JeekMusic.exe
    goto :failed
)

echo Starting Jeek Music...
start "" "%CD%\output\JeekMusic.exe"
if errorlevel 1 goto :failed
popd
exit /b 0

:failed
echo Build and run failed. See the error above.
pause
popd
exit /b 1
