# Verifies the staged-secret checker without touching the developer's real index.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$checker = Join-Path $PSScriptRoot 'check-staged-key-prefix.ps1'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempIndex = Join-Path $tempRoot ('player-hook-' + [guid]::NewGuid().ToString('N') + '.index')
$previousIndex = $env:GIT_INDEX_FILE

Push-Location $root
try {
    $env:GIT_INDEX_FILE = $tempIndex
    & git read-tree HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法初始化临时 Git 索引' }

    & pwsh -NoLogo -NoProfile -NonInteractive -File $checker
    if ($LASTEXITCODE -ne 0) { throw '合法暂存内容被错误拦截' }

    # PLAN is an allowed policy location in the real tree. Mapping the same blob
    # to another path proves that the exemption is path-specific and fails closed.
    $planBlob = & git rev-parse 'HEAD:PLAN.md'
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($planBlob)) {
        throw '无法读取 PLAN.md blob'
    }

    & git update-index --add --cacheinfo "100644,$planBlob,hook-probe.txt"
    if ($LASTEXITCODE -ne 0) { throw '无法构造拦截测试索引' }

    & pwsh -NoLogo -NoProfile -NonInteractive -File $checker
    if ($LASTEXITCODE -eq 0) { throw '非白名单路径未被拦截' }

    Write-Output 'pre-commit self-test passed: allow=0, block=1'
}
finally {
    if ($null -eq $previousIndex) {
        Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    }
    else {
        $env:GIT_INDEX_FILE = $previousIndex
    }

    $resolvedIndex = [System.IO.Path]::GetFullPath($tempIndex)
    if ($resolvedIndex.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedIndex)) {
        Remove-Item -LiteralPath $resolvedIndex -Force
    }
    Pop-Location
}
