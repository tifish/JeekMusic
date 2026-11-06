param(
    [string]$Exe = "$PSScriptRoot\..\..\bin\JeekMusic.exe",
    [string]$Evidence = "$PSScriptRoot\..\artifacts\lifecycle",
    [string]$Seed = "$PSScriptRoot\..\..\bin\data",
    [int]$Runs = 2,
    [int]$SettleSeconds = 12,
    [switch]$ExitViaTray
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
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h,System.Text.StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string cls,string title);
 public static IntPtr[] TrayWindows(uint pid) { var result=new System.Collections.Generic.List<IntPtr>();EnumWindows((h,l)=> {uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&!IsWindowVisible(h))result.Add(h);return true;},IntPtr.Zero);IntPtr next=IntPtr.Zero;while((next=FindWindowEx(new IntPtr(-3),next,null,null))!=IntPtr.Zero){uint p;GetWindowThreadProcessId(next,out p);if(p==pid)result.Add(next);}return result.ToArray(); }
 public static IntPtr[] Windows(uint pid) { var windows=new System.Collections.Generic.List<IntPtr>();EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid&&IsWindowVisible(h))windows.Add(h);return true;},IntPtr.Zero);return windows.ToArray(); }
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
if ($ExitViaTray) { Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes }
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
        $helper = $null
        if ($SettleSeconds -gt 0 -and $ready.structuredContent.firewall.helper.processId -gt 0) {
            $helper = Get-Process -Id $ready.structuredContent.firewall.helper.processId
            if ($ready.structuredContent.firewall.helper.starts -ne 1) { throw 'Expected one reused firewall helper' }
        }
        $menuGoneMs = $null
        if ($ExitViaTray) {
            $trayWindows = [LifecycleWindow]::TrayWindows($process.Id)
            if ($trayWindows.Count -eq 0) { throw 'Native tray callback window not found' }
            # Avalonia's real notification-area context-menu callback (WM_USER + 1024).
            foreach ($tray in $trayWindows) { [LifecycleWindow]::PostMessage($tray,0x800,[IntPtr]1,[IntPtr]0x205) | Out-Null }
            Start-Sleep -Milliseconds 250
            $exitItem = $null; $menuWindow = [IntPtr]::Zero
            foreach ($candidate in [LifecycleWindow]::Windows($process.Id)) {
                $root = [System.Windows.Automation.AutomationElement]::FromHandle($candidate)
                foreach ($label in @(([string][char]0x9000 + [char]0x51fa),'Exit')) {
                    $condition=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$label),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))
                    $exitItem=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
                    if ($exitItem) { $menuWindow=$candidate; break }
                }
                if ($exitItem) { break }
            }
            if (-not $exitItem) {
                $dump = foreach ($candidate in [LifecycleWindow]::Windows($process.Id)) {
                    $class=New-Object Text.StringBuilder 256
                    [LifecycleWindow]::GetClassName($candidate,$class,256) | Out-Null
                    'window '+$candidate+' '+$class
                    $root=[System.Windows.Automation.AutomationElement]::FromHandle($candidate)
                    foreach($node in $root.FindAll([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.Condition]::TrueCondition)) { $node.Current.ControlType.ProgrammaticName+' | '+$node.Current.Name }
                }
                $dump | Set-Content -Encoding UTF8 "$Evidence\menu-not-found.txt"
                throw 'Exit item not found in the real tray menu'
            }
            $menuRect=New-Object LifecycleWindow+RECT
            [LifecycleWindow]::GetWindowRect($menuWindow,[ref]$menuRect) | Out-Null
            $menuImage=New-Object Drawing.Bitmap ($menuRect.R-$menuRect.L),($menuRect.B-$menuRect.T)
            $menuGraphics=[Drawing.Graphics]::FromImage($menuImage); $menuDc=$menuGraphics.GetHdc()
            try { [LifecycleWindow]::PrintWindow($menuWindow,$menuDc,2) | Out-Null } finally { $menuGraphics.ReleaseHdc($menuDc); $menuGraphics.Dispose() }
            $menuImage.Save("$Evidence\run-$run-menu-open.png"); $menuImage.Dispose()
            $watch.Restart()
            $exitItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            while ([LifecycleWindow]::IsWindowVisible($menuWindow) -and $watch.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 2 }
            $menuGoneMs=$watch.ElapsedMilliseconds
            if ([LifecycleWindow]::IsWindowVisible($menuWindow)) { throw 'Tray menu stayed visible after exit' }
            $after=New-Object Drawing.Bitmap ($menuRect.R-$menuRect.L),($menuRect.B-$menuRect.T)
            $afterGraphics=[Drawing.Graphics]::FromImage($after)
            try { $afterGraphics.CopyFromScreen($menuRect.L,$menuRect.T,0,0,$after.Size) } finally { $afterGraphics.Dispose() }
            $after.Save("$Evidence\run-$run-menu-dismissed.png"); $after.Dispose()
        } else {
            $watch.Restart()
            [LifecycleWindow]::PostMessage($window,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
        }
        if (-not $process.WaitForExit(30000)) { throw 'Client failed to exit normally within 30 seconds' }
        if ($helper -and -not $helper.WaitForExit(3000)) { throw 'Firewall helper was left running' }
        $result = [pscustomobject]@{ run=$run; windowMs=$windowMs; exitMs=$watch.ElapsedMilliseconds; menuGoneMs=$menuGoneMs; helperStopped=($null -eq $helper -or $helper.HasExited); exitCode=$process.ExitCode; mcpResponsive=$true; startingWhenQueried=$pending.structuredContent.starting; data=$data }
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
