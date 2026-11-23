$ErrorActionPreference = 'Continue'
$exe = 'C:\Users\Administrator\Desktop\cfos\build\CfosWidget.exe'
Get-Process CfosWidget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$p1 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3
$p2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 3

$all = @(Get-Process CfosWidget -ErrorAction SilentlyContinue)
Write-Output ("p1=" + $p1.Id + " p2=" + $p2.Id)
Write-Output ("p2Exited=" + $p2.HasExited)
Write-Output ("liveCount=" + $all.Count)
foreach ($p in $all) { Write-Output ("live pid=" + $p.Id + " ws=" + [math]::Round($p.WorkingSet64/1MB,1) + "MB cpu=" + [math]::Round($p.TotalProcessorTime.TotalSeconds,2)) }

$all | Stop-Process -Force
Start-Sleep -Milliseconds 800
Write-Output ("afterKill=" + @(Get-Process CfosWidget -ErrorAction SilentlyContinue).Count)
