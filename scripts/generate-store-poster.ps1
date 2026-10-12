[CmdletBinding()]
param(
    [string]$OutputPath = 'artifacts\store-listing-1.0.0.56\YASK-CIEM-Store-Poster-720x1080.png',
    [string]$LogoPath = 'artifacts\store-listing-1.0.0.56\YASK-CIEM-Store-Logo-1080.png'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputFile = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repositoryRoot $OutputPath }
$logoFile = if ([IO.Path]::IsPathRooted($LogoPath)) { $LogoPath } else { Join-Path $repositoryRoot $LogoPath }
if (-not (Test-Path -LiteralPath $logoFile -PathType Leaf)) {
    throw "Logo file not found: $logoFile"
}

$outputDirectory = Split-Path -Parent $outputFile
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param(
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height,
        [int]$Radius
    )

    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$width = 720
$height = 1080
$bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$logo = $null
$resources = [System.Collections.Generic.List[IDisposable]]::new()
try {
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, $width, $height),
        [System.Drawing.Color]::FromArgb(255, 10, 25, 37),
        [System.Drawing.Color]::FromArgb(255, 22, 54, 72),
        35
    )
    $resources.Add($background)
    $graphics.FillRectangle($background, 0, 0, $width, $height)

    $gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(24, 148, 195, 204), 1)
    $resources.Add($gridPen)
    for ($x = 32; $x -lt $width; $x += 54) {
        $graphics.DrawLine($gridPen, $x, 485, $x, 900)
    }
    for ($y = 485; $y -lt 900; $y += 54) {
        $graphics.DrawLine($gridPen, 32, $y, 688, $y)
    }

    $logoPlate = New-RoundedRectanglePath -X 45 -Y 43 -Width 112 -Height 112 -Radius 20
    $resources.Add($logoPlate)
    $logoPlateBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 246, 250, 251))
    $resources.Add($logoPlateBrush)
    $graphics.FillPath($logoPlateBrush, $logoPlate)
    $logo = [System.Drawing.Image]::FromFile($logoFile)
    $graphics.DrawImage($logo, [System.Drawing.Rectangle]::new(51, 49, 100, 100))

    $brandFont = [System.Drawing.Font]::new('Segoe UI', 31, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($brandFont)
    $brandBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 246, 250, 251))
    $resources.Add($brandBrush)
    $graphics.DrawString('YASK-CIEM', $brandFont, $brandBrush, 177, 61)

    $smallFont = [System.Drawing.Font]::new('Segoe UI', 13, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($smallFont)
    $mutedBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 177, 203, 211))
    $resources.Add($mutedBrush)
    $accentBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 183, 222, 151))
    $resources.Add($accentBrush)
    $graphics.DrawString('CLOUD IDENTITY EXPOSURE MANAGEMENT', $smallFont, $mutedBrush, 179, 106)
    $graphics.DrawString('CLOUD ACCESS, MADE VISIBLE', $smallFont, $accentBrush, 54, 190)

    $headlineFont = [System.Drawing.Font]::new('Segoe UI', 49, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($headlineFont)
    $graphics.DrawString("See access.`nUnderstand risk.", $headlineFont, $brandBrush, 50, 224)

    $bodyFont = [System.Drawing.Font]::new('Segoe UI', 19, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($bodyFont)
    $graphics.DrawString("Review identities and permissions across Azure,`nAWS, and Google Cloud.", $bodyFont, $mutedBrush, 55, 354)

    $panelPath = New-RoundedRectanglePath -X 42 -Y 482 -Width 636 -Height 383 -Radius 18
    $resources.Add($panelPath)
    $panelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(232, 19, 43, 57))
    $resources.Add($panelBrush)
    $panelPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(110, 101, 177, 184), 1.5)
    $resources.Add($panelPen)
    $graphics.FillPath($panelBrush, $panelPath)
    $graphics.DrawPath($panelPen, $panelPath)

    $sectionFont = [System.Drawing.Font]::new('Segoe UI', 15, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($sectionFont)
    $graphics.DrawString('ONE ACCESS PICTURE', $sectionFont, $brandBrush, 70, 510)
    $subtlePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(70, 177, 203, 211), 1)
    $resources.Add($subtlePen)
    $graphics.DrawLine($subtlePen, 70, 541, 650, 541)

    $providerFont = [System.Drawing.Font]::new('Segoe UI', 17, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($providerFont)
    $providerFormat = [System.Drawing.StringFormat]::new()
    $resources.Add($providerFormat)
    $providerFormat.Alignment = [System.Drawing.StringAlignment]::Center
    $providerFormat.LineAlignment = [System.Drawing.StringAlignment]::Center
    $providers = @(
        @{ Name = 'Azure'; X = 72; Color = [System.Drawing.Color]::FromArgb(255, 87, 178, 232) },
        @{ Name = 'AWS'; X = 277; Color = [System.Drawing.Color]::FromArgb(255, 242, 190, 104) },
        @{ Name = 'Google Cloud'; X = 482; Color = [System.Drawing.Color]::FromArgb(255, 143, 211, 174) }
    )
    foreach ($provider in $providers) {
        $providerPath = New-RoundedRectanglePath -X $provider.X -Y 563 -Width 166 -Height 56 -Radius 10
        $resources.Add($providerPath)
        $providerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 29, 58, 72))
        $resources.Add($providerBrush)
        $providerPen = [System.Drawing.Pen]::new($provider.Color, 2)
        $resources.Add($providerPen)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 245, 249, 248))
        $resources.Add($textBrush)
        $graphics.FillPath($providerBrush, $providerPath)
        $graphics.DrawPath($providerPen, $providerPath)
        $graphics.DrawString($provider.Name, $providerFont, $textBrush, [System.Drawing.RectangleF]::new($provider.X, 563, 166, 56), $providerFormat)
    }

    $connectorPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 108, 188, 174), 2.5)
    $resources.Add($connectorPen)
    $connectorPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $connectorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    foreach ($centerX in @(155, 360, 565)) {
        $graphics.DrawLine($connectorPen, $centerX, 619, $centerX, 657)
    }
    $graphics.DrawLine($connectorPen, 155, 657, 565, 657)
    $graphics.DrawLine($connectorPen, 360, 657, 360, 687)
    $nodeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 183, 222, 151))
    $resources.Add($nodeBrush)
    foreach ($centerX in @(155, 360, 565)) {
        $graphics.FillEllipse($nodeBrush, $centerX - 5, 652, 10, 10)
    }

    $graphics.DrawLine($subtlePen, 70, 704, 650, 704)
    $graphics.DrawLine($subtlePen, 263, 727, 263, 823)
    $graphics.DrawLine($subtlePen, 457, 727, 457, 823)
    $columnFont = [System.Drawing.Font]::new('Segoe UI', 14, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($columnFont)
    $columnFormat = [System.Drawing.StringFormat]::new()
    $resources.Add($columnFormat)
    $columnFormat.Alignment = [System.Drawing.StringAlignment]::Center
    $columnFormat.LineAlignment = [System.Drawing.StringAlignment]::Center
    foreach ($column in @(
        @{ Text = 'IDENTITIES'; X = 70; Color = [System.Drawing.Color]::FromArgb(255, 87, 178, 232) },
        @{ Text = 'ENTITLEMENTS'; X = 263; Color = [System.Drawing.Color]::FromArgb(255, 242, 190, 104) },
        @{ Text = 'RISK FINDINGS'; X = 457; Color = [System.Drawing.Color]::FromArgb(255, 143, 211, 174) }
    )) {
        $markerBrush = [System.Drawing.SolidBrush]::new($column.Color)
        $resources.Add($markerBrush)
        $graphics.FillRectangle($markerBrush, $column.X + 12, 752, 4, 30)
        $graphics.DrawString($column.Text, $columnFont, $brandBrush, [System.Drawing.RectangleF]::new($column.X + 20, 729, 166, 76), $columnFormat)
    }

    $statementFont = [System.Drawing.Font]::new('Segoe UI', 22, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($statementFont)
    $graphics.DrawString('Map access. Prioritize exposure.', $statementFont, $brandBrush, 51, 891)

    $footerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 183, 222, 151), 3)
    $resources.Add($footerPen)
    $graphics.DrawLine($footerPen, 54, 962, 102, 962)
    $footerFont = [System.Drawing.Font]::new('Segoe UI', 16, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $resources.Add($footerFont)
    $graphics.DrawString('READ-ONLY CLOUD SCANNING', $footerFont, $brandBrush, 54, 981)
    $graphics.DrawString('Scans leave cloud resources unchanged.', $smallFont, $mutedBrush, 54, 1011)

    $bitmap.Save($outputFile, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Created 720x1080 Store poster at $outputFile"
}
finally {
    if ($logo) { $logo.Dispose() }
    foreach ($resource in $resources) { $resource.Dispose() }
    $graphics.Dispose()
    $bitmap.Dispose()
}
