param([int]$AppProcess,[string]$Action='Dump',[string]$Name='',[string]$Id='',[string]$Type='',[int]$Index=0,[int]$Key=0,[int]$Modifier=0,[string]$Shot='', [string]$Value='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class TagNative {
 [StructLayout(LayoutKind.Sequential)] public struct P {public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] public struct R {public int L,T,Right,B;}
 public delegate bool EnumProc(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code,uint type);
 [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h,ref P p);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,IntPtr p);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a,uint b,bool attach);
 [DllImport("user32.dll")] public static extern bool GetKeyboardState(byte[] s);
 [DllImport("user32.dll")] public static extern bool SetKeyboardState(byte[] s);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out R r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 public static IntPtr[] Windows(uint pid) { var list=new List<IntPtr>(); EnumWindows((h,l)=>{uint p;GetWindowThreadProcessId(h,out p);if(p==pid && IsWindowVisible(h))list.Add(h);return true;},IntPtr.Zero);return list.ToArray(); }
}
"@
[TagNative]::SetProcessDPIAware()|Out-Null
$windows=[TagNative]::Windows($AppProcess)
$hwnd=$windows|Where-Object { [System.Windows.Automation.AutomationElement]::FromHandle($_).Current.Name -eq "Jeek Music" }|Select-Object -First 1
if(-not $hwnd){throw "Main window not found"}
$nodes=@(foreach($handle in $windows){
 $root=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
 $root.FindAll([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.Condition]::TrueCondition)
})
if($Action -eq 'Dump'){
 foreach($node in $nodes){$c=$node.Current;if(($c.Name -or $c.AutomationId) -and -not $c.IsOffscreen){'{0} | {1} | {2} | {3}' -f $c.ControlType.ProgrammaticName,$c.AutomationId,$c.Name,$c.BoundingRectangle}}
}
elseif($Action -eq 'Focused'){foreach($node in $nodes){if($node.Current.HasKeyboardFocus){$node.Current.AutomationId+' | '+$node.Current.Name}}}
elseif($Action -in @('Click','Right','Double','Invoke','Expand','Focus','Set','Scroll')){
 $matches=@($nodes|Where-Object {(-not $_.Current.IsOffscreen) -and ((-not $Name) -or $_.Current.Name.StartsWith($Name)) -and ((-not $Id) -or $_.Current.AutomationId -eq $Id) -and ((-not $Type) -or $_.Current.ControlType.ProgrammaticName -eq ('ControlType.'+$Type))} | Sort-Object {$_.Current.BoundingRectangle.Top},{$_.Current.BoundingRectangle.Left})
 if($Index -ge $matches.Count){throw "Target not found: $Name / $Id / $Type ($($matches.Count))"}
 $node=$matches[$Index]
 if($Action -eq 'Scroll'){$range=$node.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern);$range.SetValue($range.Current.Minimum+($range.Current.Maximum-$range.Current.Minimum)*([double]$Value/100))}
 elseif($Action -eq 'Set'){$node.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)}
 elseif($Action -eq 'Invoke'){$node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 elseif($Action -eq 'Expand'){$node.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()}
 elseif($Action -eq 'Focus'){$node.SetFocus()}
 else{
  $r=$node.Current.BoundingRectangle;$point=[TagNative+P]::new();$point.X=[int]($r.Left+[Math]::Min(18,$r.Width/2));$point.Y=[int]($r.Top+$r.Height/2)
  $handle=$hwnd
  foreach($wh in $windows){$wr=[TagNative+R]::new();[TagNative]::GetWindowRect($wh,[ref]$wr)|Out-Null;if($wh -ne $hwnd -and $point.X -ge $wr.L -and $point.X -lt $wr.Right -and $point.Y -ge $wr.T -and $point.Y -lt $wr.B){$handle=$wh;break}}
  $thread=[TagNative]::GetCurrentThreadId();$target=[TagNative]::GetWindowThreadProcessId($handle,[IntPtr]::Zero)
  [TagNative]::AttachThreadInput($thread,$target,$true)|Out-Null
  $saved=[byte[]]::new(256);[TagNative]::GetKeyboardState($saved)|Out-Null
  try{
   if($Modifier){$keys=[byte[]]$saved.Clone();$keys[$Modifier]=128;if($Modifier -eq 17){$keys[0xa2]=128};if($Modifier -eq 16){$keys[0xa0]=128};[TagNative]::SetKeyboardState($keys)|Out-Null}
   [TagNative]::ScreenToClient($handle,[ref]$point)|Out-Null
   $packed=[IntPtr](($point.Y -shl 16) -bor ($point.X -band 65535))
   $down=if($Action -eq 'Right'){0x204}else{0x201};$up=if($Action -eq 'Right'){0x205}else{0x202};$mask=if($Action -eq 'Right'){2}else{1};if($Modifier -eq 17){$mask=$mask -bor 8}
   [TagNative]::SendMessage($handle,0x200,[IntPtr]0,$packed)|Out-Null
   [TagNative]::SendMessage($handle,$down,[IntPtr]$mask,$packed)|Out-Null
   [TagNative]::SendMessage($handle,$up,[IntPtr]0,$packed)|Out-Null
   if($Action -eq 'Double'){
    [TagNative]::SendMessage($handle,0x203,[IntPtr]1,$packed)|Out-Null
    [TagNative]::SendMessage($handle,0x202,[IntPtr]0,$packed)|Out-Null
   }
  }finally{[TagNative]::SetKeyboardState($saved)|Out-Null;[TagNative]::AttachThreadInput($thread,$target,$false)|Out-Null}
 }
 "acted $Action $Name $Id"
}
elseif($Action -eq 'Key'){
 # Direct to the focused popup if present; preserve real shared keyboard state.
 $handle=$windows|Where-Object {$_ -ne $hwnd -and [System.Windows.Automation.AutomationElement]::FromHandle($_).FindFirst([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem))}|Select-Object -First 1
 if(-not $handle){$handle=$windows|Where-Object {$_ -ne $hwnd -and [System.Windows.Automation.AutomationElement]::FromHandle($_).Current.Name -in @("Settings","设置")}|Select-Object -First 1; if(-not $handle){$handle=$hwnd}}
 $thread=[TagNative]::GetCurrentThreadId();$target=[TagNative]::GetWindowThreadProcessId($handle,[IntPtr]::Zero)
 [TagNative]::AttachThreadInput($thread,$target,$true)|Out-Null
 $saved=[byte[]]::new(256);[TagNative]::GetKeyboardState($saved)|Out-Null
 try{
  if($Modifier){$keys=[byte[]]$saved.Clone();$keys[$Modifier]=128;if($Modifier -eq 17){$keys[0xa2]=128};if($Modifier -eq 16){$keys[0xa0]=128};[TagNative]::SetKeyboardState($keys)|Out-Null}
  $scan=[TagNative]::MapVirtualKey($Key,0);$keyData=1L -bor ($scan -shl 16);if($Key -in 37..40){$keyData=$keyData -bor 0x1000000L}
  [TagNative]::SendMessage($handle,0x100,[IntPtr]$Key,[IntPtr]$keyData)|Out-Null
  [TagNative]::SendMessage($handle,0x101,[IntPtr]$Key,[IntPtr]($keyData -bor 0xC0000000L))|Out-Null
 }finally{[TagNative]::SetKeyboardState($saved)|Out-Null;[TagNative]::AttachThreadInput($thread,$target,$false)|Out-Null}
 "key $Key"
}
if($Shot){
 Start-Sleep -Milliseconds 220
 $windows=[TagNative]::Windows($AppProcess)
 $main=[TagNative+R]::new();[TagNative]::GetWindowRect($hwnd,[ref]$main)|Out-Null
 foreach($handle in $windows){$r=[TagNative+R]::new();[TagNative]::GetWindowRect($handle,[ref]$r)|Out-Null;$main.L=[Math]::Min($main.L,$r.L);$main.T=[Math]::Min($main.T,$r.T);$main.Right=[Math]::Max($main.Right,$r.Right);$main.B=[Math]::Max($main.B,$r.B)}
 $bmp=[Drawing.Bitmap]::new($main.Right-$main.L,$main.B-$main.T)
 $g=[Drawing.Graphics]::FromImage($bmp)
 foreach($handle in @($hwnd)+@($windows|Where-Object {$_ -ne $hwnd}|Select-Object -Last 8)){
  $r=[TagNative+R]::new();[TagNative]::GetWindowRect($handle,[ref]$r)|Out-Null
  if($r.Right-$r.L -le 0 -or $r.B-$r.T -le 0){continue}
  $part=[Drawing.Bitmap]::new($r.Right-$r.L,$r.B-$r.T);$pg=[Drawing.Graphics]::FromImage($part);$dc=$pg.GetHdc()
  try{$ok=[TagNative]::PrintWindow($handle,$dc,2)}finally{$pg.ReleaseHdc($dc)}
  if(-not $ok){throw 'PrintWindow failed'}
  $g.DrawImageUnscaled($part,$r.L-$main.L,$r.T-$main.T);$pg.Dispose();$part.Dispose()
 }
 $bmp.Save([IO.Path]::GetFullPath($Shot));$g.Dispose();$bmp.Dispose();"saved $Shot"
}
