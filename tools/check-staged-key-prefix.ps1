# P6: reject the reserved ChKSz API-key prefix from the complete Git index.
# Keep the prefix assembled at runtime so this checker does not need its own
# whitelist entry.
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 otherwise decodes native Git output with the active
# code page. The checker only relies on ASCII markers, but forcing UTF-8 keeps
# diagnostics and path parsing deterministic on older Windows installations.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

$needle = 'chksz' + '_'
$matches = @(& git grep --cached -n -i -F -- $needle -- . 2>$null)
$grepExit = $LASTEXITCODE

if ($grepExit -eq 1) {
    exit 0
}

if ($grepExit -ne 0) {
    Write-Error "pre-commit: failed to inspect the Git index (git grep exit $grepExit)."
    exit 1
}

$violations = [System.Collections.Generic.List[string]]::new()

foreach ($match in $matches) {
    $parts = $match -split ':', 3
    if ($parts.Count -lt 3) {
        # A binary match has no line number/content. Binary secrets are never
        # valid policy prose or a text-only harness fixture.
        $violations.Add($match)
        continue
    }

    $path = $parts[0].Replace('\', '/')
    $lineNumber = $parts[1]
    $lineText = $parts[2]
    $allowed = $false

    if ($path -eq 'PLAN.md' -or $path -eq 'tools/Player.Harness/Program.cs') {
        # The two documented locations may mention the prefix as prose. A
        # real key starts immediately after the prefix, whereas every allowed
        # occurrence is followed by a space or a Markdown backtick. Keeping
        # this lexical guard prevents either file from becoming a path-wide
        # secret exemption and avoids locale-dependent Chinese string checks.
        $allowed = $true
        $searchFrom = 0
        while ($true) {
            $prefixIndex = $lineText.IndexOf(
                $needle,
                $searchFrom,
                [System.StringComparison]::OrdinalIgnoreCase)
            if ($prefixIndex -lt 0) {
                break
            }

            $suffixIndex = $prefixIndex + $needle.Length
            $occurrenceAllowed = $suffixIndex -ge $lineText.Length -or
                $lineText[$suffixIndex] -eq ' ' -or
                $lineText[$suffixIndex] -eq '`'
            if (-not $occurrenceAllowed) {
                $allowed = $false
                break
            }
            $searchFrom = $suffixIndex
        }
    }

    if (-not $allowed) {
        $violations.Add("${path}:${lineNumber}")
    }
}

if ($violations.Count -eq 0) {
    exit 0
}

[Console]::Error.WriteLine('pre-commit: blocked reserved API-key prefix in staged content:')
foreach ($violation in $violations) {
    [Console]::Error.WriteLine("  $violation")
}
[Console]::Error.WriteLine('Only the documented PLAN policy lines and the harness redaction fixture are allowed.')
exit 1
