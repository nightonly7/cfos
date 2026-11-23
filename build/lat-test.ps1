$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\Administrator\Desktop\cfos\build\CfosWidget.exe'
Get-Process CfosWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
Add-Type -Namespace W -Name U -MemberDefinition @'
public delegate bool EnumProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr res);
'@
$script:target = [IntPtr]::Zero
$cb = [W.U+EnumProc]{
  param($h, $l)
  $owner = 0
  [void][W.U]::GetWindowThreadProcessId($h, [ref]$owner)
  if ($owner -eq $p.Id) { $script:target = $h; return $false }
  return $true
}
[void][W.U]::EnumWindows($cb, [IntPtr]::Zero)
Write-Output ('hwnd=' + $script:target)
$lat = New-Object System.Collections.ArrayList
$res = [IntPtr]::Zero
Start-Sleep -Milliseconds 300
$p.Refresh()
$c0 = $p.TotalProcessorTime.TotalMilliseconds
for ($i=0; $i -lt 150; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  [void][W.U]::SendMessageTimeout($script:target, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 2000, [ref]$res)
  $sw.Stop()
  [void]$lat.Add($sw.Elapsed.TotalMilliseconds)
  Start-Sleep -Milliseconds 100
}
$p.Refresh()
$c1 = $p.TotalProcessorTime.TotalMilliseconds
$sorted = @($lat | Sort-Object)
$n = $sorted.Count
Write-Output ('samples=' + $n + ' p50=' + [math]::Round($sorted[[int]($n*0.5)],2) + ' p90=' + [math]::Round($sorted[[int]($n*0.9)],2) + ' p99=' + [math]::Round($sorted[$n-2],2) + ' max=' + [math]::Round($sorted[$n-1],2))
$elapsed = 15.0
Write-Output ('cpu_pct_1core=' + [math]::Round((($c1-$c0)/1000)/$elapsed*100,2) + '%')
$p | Stop-Process -Force