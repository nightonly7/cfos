$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\Administrator\Desktop\cfos\build\CfosWidget.exe'
Get-Process CfosWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$p1 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$p2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$live = @(Get-Process CfosWidget -ErrorAction SilentlyContinue)
Write-Output ('singleInstance liveCount=' + $live.Count + ' secondExited=' + $p2.HasExited)
Add-Type -Namespace W -Name U -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr res);
'@
$target = [IntPtr]::Zero
$cb = [W.U+EnumProc]{ param($h,$l); $o=0; [void][W.U]::GetWindowThreadProcessId($h,[ref]$o); if ($o -eq $p1.Id) { $script:target=$h; return $false }; return $true }
[void][W.U]::EnumWindows($cb, [IntPtr]::Zero)
$h0 = $p1.HandleCount
$p1.Refresh(); $c0 = $p1.TotalProcessorTime.TotalMilliseconds
$lat = New-Object System.Collections.ArrayList
$res = [IntPtr]::Zero
for ($i=0; $i -lt 120; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  [void][W.U]::SendMessageTimeout($script:target, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 2000, [ref]$res)
  $sw.Stop(); [void]$lat.Add($sw.Elapsed.TotalMilliseconds)
  Start-Sleep -Milliseconds 100
}
$p1.Refresh(); $c1 = $p1.TotalProcessorTime.TotalMilliseconds
$sorted = @($lat | Sort-Object); $n = $sorted.Count
Write-Output ('pump p50=' + [math]::Round($sorted[[int]($n*0.5)],2) + ' p90=' + [math]::Round($sorted[[int]($n*0.9)],2) + ' max=' + [math]::Round($sorted[$n-1],2) + ' ms')
Write-Output ('cpu_1core=' + [math]::Round((($c1-$c0)/1000)/12.0*100,2) + '% ws=' + [math]::Round($p1.WorkingSet64/1MB,1) + 'MB')
Start-Sleep -Seconds 2
$p1.Refresh()
Write-Output ('handles start=' + $h0 + ' now=' + $p1.HandleCount)
$p1 | Stop-Process -Force