param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\lyra-icon.svg'),
    [string]$IcoOutput = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Player.App\Assets\lyra.ico'),
    [string]$PreviewOutput = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets\lyra-icon-preview.png')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

[xml]$document = Get-Content -LiteralPath $Source -Raw
$gradient = $document.SelectSingleNode("//*[local-name()='linearGradient' and @id='lyraStroke']")
$path = $document.SelectSingleNode("//*[local-name()='path']")
$circle = $document.SelectSingleNode("//*[local-name()='circle']")
if ($null -eq $gradient -or $null -eq $path -or $null -eq $circle) {
    throw 'The Lyra SVG does not contain the expected gradient, path, and circle.'
}

$pathNumbers = @([Text.RegularExpressions.Regex]::Matches(
        $path.GetAttribute('d'),
        '-?\d+(?:\.\d+)?') | ForEach-Object { [single]::Parse($_.Value, [Globalization.CultureInfo]::InvariantCulture) })
if ($pathNumbers.Count -ne 6) {
    throw 'The Lyra path must contain exactly three points.'
}

$stopNodes = @($gradient.SelectNodes("*[local-name()='stop']"))
if ($stopNodes.Count -lt 2) {
    throw 'The Lyra gradient must contain at least two stops.'
}

$gradientColors = [Drawing.Color[]]@($stopNodes | ForEach-Object {
        [Drawing.ColorTranslator]::FromHtml($_.GetAttribute('stop-color'))
    })
$gradientPositions = [single[]]@($stopNodes | ForEach-Object {
        [single]::Parse($_.GetAttribute('offset'), [Globalization.CultureInfo]::InvariantCulture)
    })
$circleColor = [Drawing.ColorTranslator]::FromHtml($circle.GetAttribute('fill'))
$strokeWidth = [single]::Parse($path.GetAttribute('stroke-width'), [Globalization.CultureInfo]::InvariantCulture)
$gradientStart = [Drawing.PointF]::new(
    [single]::Parse($gradient.GetAttribute('x1'), [Globalization.CultureInfo]::InvariantCulture),
    [single]::Parse($gradient.GetAttribute('y1'), [Globalization.CultureInfo]::InvariantCulture))
$gradientEnd = [Drawing.PointF]::new(
    [single]::Parse($gradient.GetAttribute('x2'), [Globalization.CultureInfo]::InvariantCulture),
    [single]::Parse($gradient.GetAttribute('y2'), [Globalization.CultureInfo]::InvariantCulture))
$circleCenter = [Drawing.PointF]::new(
    [single]::Parse($circle.GetAttribute('cx'), [Globalization.CultureInfo]::InvariantCulture),
    [single]::Parse($circle.GetAttribute('cy'), [Globalization.CultureInfo]::InvariantCulture))
$circleRadius = [single]::Parse($circle.GetAttribute('r'), [Globalization.CultureInfo]::InvariantCulture)

function New-LyraBitmap {
    param([Parameter(Mandatory)] [int]$Size)

    $scale = [single]($Size / 256.0)
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality

        if ($Size -eq 16) {
            $points = [Drawing.PointF[]]@(
                [Drawing.PointF]::new(5.5, 4.0),
                [Drawing.PointF]::new(5.5, 11.0),
                [Drawing.PointF]::new(10.5, 11.0))
            $scaledStroke = [single]2.0
            $scaledCircleCenter = [Drawing.PointF]::new(10.5, 5.75)
            $scaledCircleRadius = [single]1.2
        }
        else {
            $points = [Drawing.PointF[]]@(
                [Drawing.PointF]::new($pathNumbers[0] * $scale, $pathNumbers[1] * $scale),
                [Drawing.PointF]::new($pathNumbers[2] * $scale, $pathNumbers[3] * $scale),
                [Drawing.PointF]::new($pathNumbers[4] * $scale, $pathNumbers[5] * $scale))
            $scaledStroke = $strokeWidth * $scale
            $scaledCircleCenter = [Drawing.PointF]::new($circleCenter.X * $scale, $circleCenter.Y * $scale)
            $scaledCircleRadius = $circleRadius * $scale
        }

        $brush = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.PointF]::new($gradientStart.X * $scale, $gradientStart.Y * $scale),
            [Drawing.PointF]::new($gradientEnd.X * $scale, $gradientEnd.Y * $scale),
            $gradientColors[0],
            $gradientColors[-1])
        try {
            $blend = [Drawing.Drawing2D.ColorBlend]::new()
            $blend.Colors = $gradientColors
            $blend.Positions = $gradientPositions
            $brush.InterpolationColors = $blend

            $pen = [Drawing.Pen]::new($brush, $scaledStroke)
            try {
                $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                $graphics.DrawLines($pen, $points)
            }
            finally {
                $pen.Dispose()
            }
        }
        finally {
            $brush.Dispose()
        }

        $starBrush = [Drawing.SolidBrush]::new($circleColor)
        try {
            $graphics.FillEllipse(
                $starBrush,
                $scaledCircleCenter.X - $scaledCircleRadius,
                $scaledCircleCenter.Y - $scaledCircleRadius,
                $scaledCircleRadius * 2,
                $scaledCircleRadius * 2)
        }
        finally {
            $starBrush.Dispose()
        }

        return $bitmap
    }
    finally {
        $graphics.Dispose()
    }
}

function ConvertTo-PngBytes {
    param([Parameter(Mandatory)] [Drawing.Bitmap]$Bitmap)

    $stream = [IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

$sizes = @(256, 64, 48, 32, 24, 16)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-LyraBitmap -Size $size
    try {
        $frames.Add((ConvertTo-PngBytes -Bitmap $bitmap))
    }
    finally {
        $bitmap.Dispose()
    }
}

$icoStream = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($icoStream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $frame = $frames[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Length
    }
    foreach ($frame in $frames) {
        $writer.Write($frame)
    }
    $writer.Flush()
    [IO.File]::WriteAllBytes($IcoOutput, $icoStream.ToArray())
}
finally {
    $writer.Dispose()
    $icoStream.Dispose()
}

$preview = New-LyraBitmap -Size 512
try {
    $preview.Save($PreviewOutput, [Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $preview.Dispose()
}

Write-Output "ICO: $IcoOutput"
Write-Output "Preview: $PreviewOutput"
