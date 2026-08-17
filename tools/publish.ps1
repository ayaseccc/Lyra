# P6 发布脚本：生成并审计 self-contained win-x64 便携 zip。
# 用法：pwsh tools/publish.ps1 [-Version 1.0.0]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-ChildPath {
    param(
        [Parameter(Mandatory)] [string]$Parent,
        [Parameter(Mandatory)] [string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent)
    $childFull = [IO.Path]::GetFullPath((Join-Path $parentFull $Child))
    $prefix = $parentFull.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $childFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "发布路径越出仓库：$childFull"
    }
    return $childFull
}

function Test-StreamContainsText {
    param(
        [Parameter(Mandatory)] [IO.Stream]$Stream,
        [Parameter(Mandatory)] [string]$Needle
    )

    $reader = [IO.StreamReader]::new(
        $Stream,
        [Text.Encoding]::ASCII,
        $false,
        16KB,
        $true)
    try {
        $buffer = [char[]]::new(16KB)
        $tail = ''
        while (($read = $reader.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $chunk = $tail + [string]::new($buffer, 0, $read)
            if ($chunk.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                return $true
            }

            $keep = [Math]::Min($Needle.Length - 1, $chunk.Length)
            $tail = if ($keep -gt 0) { $chunk.Substring($chunk.Length - $keep) } else { '' }
        }
        return $false
    }
    finally {
        $reader.Dispose()
    }
}

function Test-ForbiddenPackagePath {
    param([Parameter(Mandatory)] [string]$RelativePath)

    $path = $RelativePath.Replace('\', '/')
    $leaf = [IO.Path]::GetFileName($path)
    return (
        $path -match '(?i)(^|/)(data|format-test)(/|$)' -or
        $leaf -match '(?i)^config.*\.json$' -or
        $leaf -match '(?i)-bak\.json$' -or
        $leaf -match '(?i)\.(db|log|bak)$')
}

function Assert-StagingContent {
    param(
        [Parameter(Mandatory)] [string]$Directory,
        [Parameter(Mandatory)] [string[]]$RequiredEntries,
        [Parameter(Mandatory)] [string[]]$AllowedBassDlls
    )

    foreach ($entry in $RequiredEntries) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $entry) -PathType Leaf)) {
            throw "发布暂存缺少必需文件：$entry"
        }
    }

    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
        if (Test-ForbiddenPackagePath $relative) {
            throw "发布暂存包含敏感或运行期文件：$relative"
        }
        if ($file.Name -like 'bass*.dll' -and
            ($relative -match '/' -or $AllowedBassDlls -notcontains $file.Name)) {
            throw "发布暂存包含未审计的 BASS DLL：$relative"
        }

        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            if (Test-StreamContainsText $stream ('chksz' + '_')) {
                throw "发布暂存包含保留的 API Key 前缀：$relative"
            }
        }
        finally {
            $stream.Dispose()
        }
    }
}

function Assert-ZipContent {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string[]]$RequiredEntries,
        [Parameter(Mandatory)] [string[]]$AllowedBassDlls
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entryNames = [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            [void]$entryNames.Add($name)
            if (Test-ForbiddenPackagePath $name) {
                throw "ZIP 包含敏感或运行期文件：$name"
            }

            $leaf = [IO.Path]::GetFileName($name)
            if ($leaf -like 'bass*.dll' -and
                ($name -match '/' -or $AllowedBassDlls -notcontains $leaf)) {
                throw "ZIP 包含未审计的 BASS DLL：$name"
            }

            if ($entry.Length -gt 0) {
                $stream = $entry.Open()
                try {
                    if (Test-StreamContainsText $stream ('chksz' + '_')) {
                        throw "ZIP 包含保留的 API Key 前缀：$name"
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
        }

        foreach ($required in $RequiredEntries) {
            if (-not $entryNames.Contains($required.Replace('\', '/'))) {
                throw "ZIP 缺少必需文件：$required"
            }
        }

        return $archive.Entries.Count
    }
    finally {
        $archive.Dispose()
    }
}

$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Resolve-ChildPath $root 'Player.App\Player.App.csproj'
$guide = Resolve-ChildPath $root 'docs\用户指南.md'
$notices = Resolve-ChildPath $root 'THIRD-PARTY-NOTICES.md'
$licenseSource = Resolve-ChildPath $root 'licenses'
$stagingRoot = Resolve-ChildPath $root 'publish-staging'
$runId = [Guid]::NewGuid().ToString('N')
$staging = Resolve-ChildPath $stagingRoot $runId
$zipName = "Player-v$Version-win-x64.zip"
$zipPath = Resolve-ChildPath $root $zipName
$temporaryZip = Resolve-ChildPath $stagingRoot "$runId.tmp.zip"
$backupZip = Resolve-ChildPath $stagingRoot "$runId.previous"

foreach ($requiredSource in @($project, $guide, $notices)) {
    if (-not (Test-Path -LiteralPath $requiredSource -PathType Leaf)) {
        throw "发布源文件不存在：$requiredSource"
    }
}
if (-not (Test-Path -LiteralPath $licenseSource -PathType Container)) {
    throw "许可目录不存在：$licenseSource"
}

$dotnetCommand = Get-Command dotnet -ErrorAction Stop
$dotnetRoot = Split-Path -Parent $dotnetCommand.Source
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
foreach ($dotnetLegalFile in @($dotnetLicense, $dotnetNotices)) {
    if (-not (Test-Path -LiteralPath $dotnetLegalFile -PathType Leaf)) {
        throw "找不到构建机官方 .NET 法律文件：$dotnetLegalFile"
    }
}

$nativeDlls = @(
    'bass.dll',
    'bassalac.dll',
    'bassape.dll',
    'bassasio.dll',
    'bassflac.dll',
    'bassmix.dll',
    'bassopus.dll',
    'basswasapi.dll',
    'basswv.dll'
)
$requiredEntries = @(
    'Player.exe',
    'Player.dll',
    'Player.deps.json',
    'Player.runtimeconfig.json',
    'Player.Core.dll',
    'README.md',
    'THIRD-PARTY-NOTICES.md',
    'licenses/MIT.txt',
    'licenses/Apache-2.0.txt',
    'licenses/LGPL-2.1.txt',
    'licenses/BASS.txt',
    'licenses/BASSASIO.txt',
    'licenses/BASS-ADDONS.txt',
    'licenses/DOTNET-RUNTIME.md',
    'licenses/SQLite-Public-Domain.txt',
    'licenses/WPF-UI-ThirdPartyNotices.txt',
    'licenses/CommunityToolkit-Mvvm-ThirdPartyNotices.txt',
    'licenses/dotnet/LICENSE.txt',
    'licenses/dotnet/ThirdPartyNotices.txt'
) + $nativeDlls

try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $publishArguments = @(
        'publish',
        $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $staging,
        "-p:Version=$Version"
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

    Copy-Item -LiteralPath $guide -Destination (Join-Path $staging 'README.md') -Force
    Copy-Item -LiteralPath $notices -Destination $staging -Force
    Copy-Item -LiteralPath $licenseSource -Destination $staging -Recurse -Force

    $dotnetLegalDestination = Join-Path $staging 'licenses\dotnet'
    New-Item -ItemType Directory -Path $dotnetLegalDestination -Force | Out-Null
    Copy-Item -LiteralPath $dotnetLicense -Destination $dotnetLegalDestination -Force
    Copy-Item -LiteralPath $dotnetNotices -Destination $dotnetLegalDestination -Force

    Assert-StagingContent $staging $requiredEntries $nativeDlls

    [IO.Compression.ZipFile]::CreateFromDirectory(
        $staging,
        $temporaryZip,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $entryCount = Assert-ZipContent $temporaryZip $requiredEntries $nativeDlls

    if (Test-Path -LiteralPath $zipPath) {
        [IO.File]::Replace($temporaryZip, $zipPath, $backupZip, $true)
        Remove-Item -LiteralPath $backupZip -Force
    }
    else {
        [IO.File]::Move($temporaryZip, $zipPath)
    }

    $zipInfo = Get-Item -LiteralPath $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    Write-Output ('产出：' + $zipPath)
    Write-Output ('条目：' + $entryCount)
    Write-Output ('大小：' + [Math]::Round($zipInfo.Length / 1MB, 2) + ' MB')
    Write-Output ('SHA-256：' + $hash)
}
finally {
    if (Test-Path -LiteralPath $temporaryZip) {
        Remove-Item -LiteralPath $temporaryZip -Force
    }
    if (Test-Path -LiteralPath $backupZip) {
        Remove-Item -LiteralPath $backupZip -Force
    }
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        $remaining = Get-ChildItem -LiteralPath $stagingRoot -Force | Select-Object -First 1
        if ($null -eq $remaining) {
            Remove-Item -LiteralPath $stagingRoot -Force
        }
    }
}
