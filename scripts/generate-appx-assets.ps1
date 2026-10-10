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
    @{ Name = 'Square150x150Logo.png'; Size = 150 }
)

foreach ($asset in $assets) {
    $size = $asset.Size
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $brush = $null
    $font = $null
    $whiteBrush = $null
    $format = $null
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $margin = [Math]::Max(1, [int][Math]::Round($size * 0.06))
        $bounds = [System.Drawing.Rectangle]::new($margin, $margin, $size - (2 * $margin), $size - (2 * $margin))
        $radius = [Math]::Max(2, [int][Math]::Round($size * 0.16))
        $diameter = $radius * 2
        $path.AddArc($bounds.Left, $bounds.Top, $diameter, $diameter, 180, 90)
        $path.AddArc($bounds.Right - $diameter, $bounds.Top, $diameter, $diameter, 270, 90)
        $path.AddArc($bounds.Right - $diameter, $bounds.Bottom - $diameter, $diameter, $diameter, 0, 90)
        $path.AddArc($bounds.Left, $bounds.Bottom - $diameter, $diameter, $diameter, 90, 90)
        $path.CloseFigure()

        $startColor = [System.Drawing.Color]::FromArgb(255, 47, 121, 191)
        $endColor = [System.Drawing.Color]::FromArgb(255, 25, 59, 93)
        $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($bounds, $startColor, $endColor, 45)
        $graphics.FillPath($brush, $path)

        $font = [System.Drawing.Font]::new('Segoe UI', [single]($size * 0.57), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
        $format = [System.Drawing.StringFormat]::new()
        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $textBounds = [System.Drawing.RectangleF]::new(0, 0, $size, $size)
        $graphics.DrawString('Y', $font, $whiteBrush, $textBounds, $format)
        $bitmap.Save((Join-Path $outputDirectory $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        if ($format) { $format.Dispose() }
        if ($whiteBrush) { $whiteBrush.Dispose() }
        if ($font) { $font.Dispose() }
        if ($brush) { $brush.Dispose() }
        $path.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

Write-Host "Created branded MSIX assets in $outputDirectory"
