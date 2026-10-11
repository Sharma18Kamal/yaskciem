[CmdletBinding()]
param(
    [string]$OutputPath = '.\installer\Assets'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repositoryRoot $OutputPath }
New-Item $outputDirectory -ItemType Directory -Force | Out-Null
Add-Type -AssemblyName System.Drawing

$assets = @(
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'Square44x44Logo.png'; Size = 44 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'YASK-CIEM-Store-Logo-300.png'; Size = 300 },
    @{ Name = 'YASK-CIEM-Logo-512.png'; Size = 512 }
)

foreach ($asset in $assets) {
    $size = $asset.Size
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $tilePath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shieldPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $brush = $null
    $shieldFill = $null
    $shieldPen = $null
    $markPen = $null
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $margin = [Math]::Max(1, [int][Math]::Round($size * 0.06))
        $bounds = [System.Drawing.Rectangle]::new($margin, $margin, $size - (2 * $margin), $size - (2 * $margin))
        $radius = [Math]::Max(2, [int][Math]::Round($size * 0.16))
        $diameter = $radius * 2
        $tilePath.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
        $tilePath.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
        $tilePath.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
        $tilePath.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
        $tilePath.CloseFigure()

        $startColor = [System.Drawing.Color]::FromArgb(255, 47, 121, 191)
        $endColor = [System.Drawing.Color]::FromArgb(255, 25, 59, 93)
        $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($bounds, $startColor, $endColor, 45)
        $graphics.FillPath($brush, $tilePath)

        $point = { param([double]$x, [double]$y) [System.Drawing.PointF]::new([single]($x * $size), [single]($y * $size)) }
        $shieldPoints = [System.Drawing.PointF[]]@(
            (& $point 0.50 0.13),
            (& $point 0.77 0.24),
            (& $point 0.75 0.52),
            (& $point 0.68 0.69),
            (& $point 0.50 0.84),
            (& $point 0.32 0.69),
            (& $point 0.25 0.52),
            (& $point 0.23 0.24)
        )
        $shieldPath.AddPolygon($shieldPoints)
        $shieldPath.CloseFigure()
        $shieldFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(34, 255, 255, 255))
        $shieldPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(238, 255, 255, 255), [single]($size * 0.025))
        $shieldPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.FillPath($shieldFill, $shieldPath)
        $graphics.DrawPath($shieldPen, $shieldPath)

        $markPen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, [single]($size * 0.052))
        $markPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $markPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $markPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawLine($markPen, (& $point 0.37 0.35), (& $point 0.50 0.48))
        $graphics.DrawLine($markPen, (& $point 0.63 0.35), (& $point 0.50 0.48))
        $graphics.DrawLine($markPen, (& $point 0.50 0.48), (& $point 0.50 0.66))
        $bitmap.Save((Join-Path $outputDirectory $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($markPen) { $markPen.Dispose() }
        if ($shieldPen) { $shieldPen.Dispose() }
        if ($shieldFill) { $shieldFill.Dispose() }
        if ($brush) { $brush.Dispose() }
        $shieldPath.Dispose()
        $tilePath.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

Write-Host "Created branded MSIX assets in $outputDirectory"
