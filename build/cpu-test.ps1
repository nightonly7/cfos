$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\Administrator\Desktop\cfos\build\CfosWidget.exe'
Get-Process CfosWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 4
$c0 = $p.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Seconds 10
$p.Refresh()
$c1 = $p.TotalProcessorTime.TotalMilliseconds
$n = [Environment]::ProcessorCount
Write-Output ("cores=" + $n)
Write-Output ("cpu10s_ms=" + [math]::Round($c1 - $c0,0) + " ~pct=" + [math]::Round((($c1-$c0)/10000/$n)*100,2) + "%")
Write-Output ("ws=" + [math]::Round($p.WorkingSet64/1MB,1) + "MB")
Write-Output ("responding=" + $p.Responding)
# responsiveness: how long does a WM_NULL-ish round trip take? use SendMessageTimeout via Add-Type
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr res);
'@
$max = 0
for ($i=0; $i -lt 12; $i++) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $out = [IntPtr]::Zero
  [void][W.U]::SendMessageTimeout($p.MainWindowHandle, 0, [IntPtr]::Zero, [IntPtr]::Zero, 2, 2000, [ref]$out)
  $sw.Stop()
  if ($sw.ElapsedMilliseconds -gt $max) { $max = $sw.ElapsedMilliseconds }
  Start-Sleep -Milliseconds 400
}
Write-Output ("mainHwnd=" + $p.MainWindowHandle + " worstPumpMs=" + $max)
$p | Stop-Process -Force
