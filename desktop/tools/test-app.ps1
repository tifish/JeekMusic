# 在作者的电脑上跑桌面端，不打扰正在用电脑的人（memory: no-focus-steal；tech-spike.md"用法"一节）：
#   - 用 --background 启动：窗口不激活、不进任务栏、放到所有窗口后面
#   - CreateNoWindow，不弹控制台
#   - PrintWindow 截图（窗口在别的窗口后面也能截）
#   - 全程检查前台窗口是不是被测试程序抢走了
#
# 用法：
#   pwsh tools/test-app.ps1 -Seconds 15 -Shot app.png -Args 'D:\Mobile\Song'
#   pwsh tools/test-app.ps1 -Fresh -Seconds 20        # 用全新的数据目录
param(
    [string]$Exe = "$PSScriptRoot\..\src\JeekMusic.App\bin\Debug\net10.0-windows10.0.19041.0\JeekMusic.exe",
    [string]$Data = "$env:TEMP\jm-test",
    [switch]$Fresh,
    [int]$Seconds = 10,
    [string]$Shot,
    [string[]]$Args = @(),
    [int]$LogLines = 15,
    [switch]$KeepRunning
)
if ($Fresh) { Remove-Item -Recurse -Force $Data -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $Data | Out-Null
Add-Type -AssemblyName System.Drawing
if (-not ("JmTest" -as [type])) {
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class JmTest {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  public static List<IntPtr> Windows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowText(h, sb, 256); return sb.ToString(); }
}
'@
}
[JmTest]::SetProcessDPIAware() | Out-Null
$psi = New-Object System.Diagnostics.ProcessStartInfo (Resolve-Path $Exe)
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$psi.Environment["JEEKMUSIC_DATA"] = $Data
$psi.ArgumentList.Add('--background')
foreach ($a in $Args) { $psi.ArgumentList.Add($a) }
$p = [System.Diagnostics.Process]::Start($psi)
$stole = 0; $samples = 0
$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep -Milliseconds 200
    $fp = 0; [JmTest]::GetWindowThreadProcessId([JmTest]::GetForegroundWindow(), [ref]$fp) | Out-Null
    $samples++; if ($fp -eq $p.Id) { $stole++ }
}
"exited: $($p.HasExited)  foreground samples owned by app: $stole / $samples"
if (-not $p.HasExited -and $Shot) {
    $main = [JmTest]::Windows($p.Id) | Where-Object { [JmTest]::Title($_) -eq 'JeekMusic' } | Select-Object -First 1
    if ($main) {
        $r = New-Object JmTest+RECT; [JmTest]::GetWindowRect($main, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc(); [JmTest]::PrintWindow($main, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc)
        $bmp.Save($Shot); "screenshot $($bmp.Width)x$($bmp.Height) -> $Shot"
    } else { "no main window found" }
}
if (-not $KeepRunning -and -not $p.HasExited) { $p.Kill(); $p.WaitForExit(3000) | Out-Null }
if (Test-Path "$Data\logs\app.log") { "--- app.log"; Get-Content "$Data\logs\app.log" -Tail $LogLines }
