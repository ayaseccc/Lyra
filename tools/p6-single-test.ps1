$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $root
$exe = Join-Path $repo 'publish\Player.exe'
$cfgPath = Join-Path $repo 'publish\data\config.json'
$bak = Join-Path $root 'p6-bak.json'
Copy-Item $cfgPath $bak -Force
Get-Process Player -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$p1 = Start-Process $exe -PassThru
Start-Sleep -Seconds 5
Write-Output ('FIRST_PID=' + $p1.Id)

$sample = 'D:\music\konomi\Aimer - カタオモイ.flac'
$p2 = Start-Process $exe -ArgumentList ('"' + $sample + '"') -PassThru
Start-Sleep -Seconds 4
$p2.Refresh()
Write-Output ('SECOND_EXITED=' + $p2.HasExited)
Start-Sleep -Seconds 3
$log = Get-ChildItem (Join-Path $repo 'publish\data\logs') | Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName | Select-String '外部|文件打开' | Select-Object -Last 4 | ForEach-Object { Write-Output ('LOG: ' + $_.Line.Substring(0, [Math]::Min(130, $_.Line.Length))) }
Get-Process Player -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
Copy-Item $bak $cfgPath -Force
Write-Output 'DONE'
