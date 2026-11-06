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
powershell.exe -NoProfile -Command "$ErrorActionPreference = 'Stop'; $root = (Get-Location).Path; $clients = @((Join-Path $root 'bin\JeekMusic.exe'), (Join-Path $root 'output\JeekMusic.exe')); Get-Process | Where-Object { $_.ProcessName -eq 'JeekMusic' -and $_.Path -in $clients } | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $_.WaitForExit() }"
if errorlevel 1 goto :failed

echo Building Jeek Music...
dotnet build "desktop\src\JeekMusic.App" -r win-x64 --self-contained true -p:PublishAot=false -p:JeekStageDailyBuild=true -p:JeekMigrateLegacyData=true
if errorlevel 1 goto :failed

if not exist "bin\JeekMusic.exe" (
    echo Build output is missing: bin\JeekMusic.exe
    goto :failed
)

echo Starting Jeek Music...
start "" "%CD%\bin\JeekMusic.exe"
if errorlevel 1 goto :failed
popd
exit /b 0

:failed
echo Build and run failed. See the error above.
pause
popd
exit /b 1
