param(
    [string]$Exe = "$PSScriptRoot\..\..\bin\JeekMusic.exe",
    [string]$Evidence = "$PSScriptRoot\..\artifacts\lifecycle",
    [string]$Seed = "$PSScriptRoot\..\..\bin\data",
    [int]$Runs = 2,
    [int]$SettleSeconds = 12
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$Evidence = [IO.Path]::GetFullPath($Evidence)
New-Item -ItemType Directory -Force $Evidence | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class LifecycleWindow {
 public delegate bool EnumProc(IntPtr h,IntPtr l);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 public static IntPtr Find(uint pid) { IntPtr result=IntPtr.Zero; EnumWindows((h,l)=> { uint p; GetWindowThreadProcessId(h,out p); if(p==pid && IsWindowVisible(h)) { result=h; return false; } return true; },IntPtr.Zero); return result; }
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[LifecycleWindow]::SetProcessDPIAware() | Out-Null
$results = @()
function Rpc($bridge, $request) {
    $bridge.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 30 -Compress))
    $bridge.StandardInput.Flush()
    $response = $bridge.StandardOutput.ReadLineAsync()
    if (-not $response.Wait(15000)) { throw 'MCP response timed out while firewall startup was pending' }
    if ($null -eq $response.Result) { throw 'MCP bridge exited unexpectedly' }
    $value = $response.Result | ConvertFrom-Json
    if ($value.error -or $value.result.isError) { throw 'MCP call failed' }
    return $value.result
}
for ($run = 1; $run -le $Runs; $run++) {
    $data = Join-Path $Evidence ("data-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $data | Out-Null
    foreach ($file in @('library.db', 'preferences.db', 'snapshot.json')) {
        if (Test-Path "$Seed\$file") { Copy-Item -LiteralPath "$Seed\$file" -Destination $data }
    }
    # Preserve the library and queue, but never write music or contact paired devices.
    $settings = Get-Content -Raw -Encoding UTF8 "$Seed\settings.json" | ConvertFrom-Json
    foreach ($entry in @{ closeAction='Exit'; onlineLyrics=$false; onlineCovers=$false; coverSaveMode='HiddenFolder'; lyricsSaveLocation='AppCache'; coversEmbedded=$true; lyricsEmbedded=$true; analysisMode='Manual'; autoUpdateCheck=$false; globalHotkeysEnabled=$false; desktopLyricsVisible=$false }.GetEnumerator()) {
        $settings | Add-Member NoteProperty $entry.Key $entry.Value -Force
    }
    [IO.File]::WriteAllText("$data\settings.json", ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText("$data\lan-sync.json", '{"enabled":true,"port":0,"bindAddress":"0.0.0.0"}', [Text.UTF8Encoding]::new($false))
    $start = New-Object Diagnostics.ProcessStartInfo ([IO.Path]::GetFullPath($Exe))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.Arguments='--background'
    $start.EnvironmentVariables['JEEKMUSIC_DATA']=$data
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $bridge = $null
    try {
        do { Start-Sleep -Milliseconds 20; $window = [LifecycleWindow]::Find($process.Id) } while ($window -eq [IntPtr]::Zero -and -not $process.HasExited -and $watch.Elapsed.TotalSeconds -lt 40)
        if ($process.HasExited -or $window -eq [IntPtr]::Zero) { throw 'Client did not open a window' }
        $windowMs = $watch.ElapsedMilliseconds
        $bridgeStart = New-Object Diagnostics.ProcessStartInfo ([IO.Path]::GetFullPath($Exe))
        $bridgeStart.UseShellExecute=$false; $bridgeStart.CreateNoWindow=$true; $bridgeStart.Arguments='--mcp'
        $bridgeStart.RedirectStandardInput=$true; $bridgeStart.RedirectStandardOutput=$true; $bridgeStart.RedirectStandardError=$true
        $bridgeStart.EnvironmentVariables['JEEKMUSIC_DATA']=$data
        $bridge = [Diagnostics.Process]::Start($bridgeStart)
        Rpc $bridge @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-11-25';capabilities=@{};clientInfo=@{name='lifecycle-test';version='1'}}} | Out-Null
        $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}'); $bridge.StandardInput.Flush()
        $diagnostics = Rpc $bridge @{jsonrpc='2.0';id=2;method='tools/call';params=@{name='get_diagnostics';arguments=@{}}}
        $diagnostics.structuredContent | ConvertTo-Json -Depth 60 | Set-Content -Encoding UTF8 "$Evidence\run-$run-diagnostics.json"
        if ($null -eq $diagnostics.structuredContent.lifecycle.startup.windowOpened) { throw 'Missing production lifecycle diagnostics' }
        $pending = Rpc $bridge @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='lan_sync_status';arguments=@{}}}
        $startupDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not $pending.structuredContent.starting -and -not $pending.structuredContent.enabled -and [DateTime]::UtcNow -lt $startupDeadline) {
            Start-Sleep -Milliseconds 100
            $pending = Rpc $bridge @{jsonrpc='2.0';id=3;method='tools/call';params=@{name='lan_sync_status';arguments=@{}}}
        }
        $pending.structuredContent | ConvertTo-Json -Depth 30 | Set-Content -Encoding UTF8 "$Evidence\run-$run-lan-pending.json"
        # Wait for MCP and automatic LAN startup to settle, then capture the real window.
        if ($SettleSeconds -gt 0) {
            Start-Sleep -Seconds $SettleSeconds
            $ready = Rpc $bridge @{jsonrpc='2.0';id=4;method='tools/call';params=@{name='lan_sync_status';arguments=@{}}}
            $readyDeadline = [DateTime]::UtcNow.AddSeconds(30)
            while ($ready.structuredContent.starting -and [DateTime]::UtcNow -lt $readyDeadline) {
                Start-Sleep -Milliseconds 200
                $ready = Rpc $bridge @{jsonrpc='2.0';id=4;method='tools/call';params=@{name='lan_sync_status';arguments=@{}}}
            }
            $ready.structuredContent | ConvertTo-Json -Depth 30 | Set-Content -Encoding UTF8 "$Evidence\run-$run-lan-ready.json"
            if (-not $ready.structuredContent.enabled -or $ready.structuredContent.firewall.state -ne 'configured') { throw 'LAN did not become ready after firewall verification' }
        }
        $rect = New-Object LifecycleWindow+RECT
        [LifecycleWindow]::GetWindowRect($window,[ref]$rect) | Out-Null
        $bitmap = New-Object Drawing.Bitmap ($rect.R-$rect.L),($rect.B-$rect.T)
        $graphics = [Drawing.Graphics]::FromImage($bitmap); $dc=$graphics.GetHdc()
        try { if (-not [LifecycleWindow]::PrintWindow($window,$dc,2)) { throw 'PrintWindow failed' } }
        finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
        $bitmap.Save("$Evidence\run-$run.png"); $bitmap.Dispose()
        $watch.Restart()
        [LifecycleWindow]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
        if (-not $process.WaitForExit(30000)) { throw 'Client failed to exit normally within 30 seconds' }
        $result = [pscustomobject]@{ run=$run; windowMs=$windowMs; exitMs=$watch.ElapsedMilliseconds; exitCode=$process.ExitCode; mcpResponsive=$true; startingWhenQueried=$pending.structuredContent.starting; data=$data }
        $results += $result; $result | ConvertTo-Json -Compress
    } finally {
        if ($bridge) { if (-not $bridge.HasExited) { $bridge.StandardInput.Close(); if (-not $bridge.WaitForExit(3000)) { $bridge.Kill() } }; $bridge.Dispose() }
        if (-not $process.HasExited) {
            $window = [LifecycleWindow]::Find($process.Id)
            [LifecycleWindow]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
            if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 "$Evidence\results.json"
