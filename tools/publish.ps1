# P6 发布脚本：一键产出 self-contained 便携 zip
# 用法：pwsh tools/publish.ps1 [-Version 1.0.0]
param(
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'Player.App\Player.App.csproj'
$staging = Join-Path $root 'publish\staging'
$zipName = "Player-v$Version-win-x64.zip"
$zipPath = Join-Path $root $zipName

# 清理暂存
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

# 发布（self-contained + 单文件框架内嵌；原生 bass dll 随 publish 输出）
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $staging -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

# 用户向 README 放进 zip
$readme = Join-Path $root 'README.md'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $staging 'README.md') -Force }

# 打 zip（顶层是 Player.exe + 依赖）
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Output ('产出：' + $zipPath)
Write-Output ('大小：' + [math]::Round((Get-Item $zipPath).Length / 1MB, 2) + ' MB')
