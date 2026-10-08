$ErrorActionPreference = 'Stop'

$outputPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'YASK-CIEM-Solution-Overview.pptx'
$powerPoint = $null
$presentation = $null

function Get-OfficeColor([string]$hex) {
    $value = $hex.TrimStart('#')
    $red = [Convert]::ToInt32($value.Substring(0, 2), 16)
    $green = [Convert]::ToInt32($value.Substring(2, 2), 16)
    $blue = [Convert]::ToInt32($value.Substring(4, 2), 16)
    return [int]($red + ($green -shl 8) + ($blue -shl 16))
}

function Add-Text($slide, [double]$x, [double]$y, [double]$width, [double]$height, [string]$text, [double]$size, [string]$color = '#18211F', [string]$font = 'Aptos', [bool]$bold = $false) {
    $shape = $slide.Shapes.AddTextbox(1, $x * 72, $y * 72, $width * 72, $height * 72)
    $shape.TextFrame.MarginLeft = 0
    $shape.TextFrame.MarginRight = 0
    $shape.TextFrame.MarginTop = 0
    $shape.TextFrame.MarginBottom = 0
    $shape.TextFrame.WordWrap = -1
    $shape.TextFrame.TextRange.Text = $text
    $shape.TextFrame.TextRange.Font.Name = $font
    $shape.TextFrame.TextRange.Font.Size = $size
    $shape.TextFrame.TextRange.Font.Bold = [int]$bold
    $shape.TextFrame.TextRange.Font.Color.RGB = Get-OfficeColor $color
    return $shape
}

function Add-Rect($slide, [double]$x, [double]$y, [double]$width, [double]$height, [string]$fill, [bool]$rounded = $false, [string]$line = $fill) {
    $shapeType = if ($rounded) { 5 } else { 1 }
    $shape = $slide.Shapes.AddShape($shapeType, $x * 72, $y * 72, $width * 72, $height * 72)
    $shape.Fill.Solid()
    $shape.Fill.ForeColor.RGB = Get-OfficeColor $fill
    $shape.Line.ForeColor.RGB = Get-OfficeColor $line
    $shape.Line.Weight = 0.7
    return $shape
}

function Add-Line($slide, [double]$x1, [double]$y1, [double]$x2, [double]$y2, [string]$color, [double]$weight = 1.2) {
    $shape = $slide.Shapes.AddShape(9, $x1 * 72, $y1 * 72, ($x2 - $x1) * 72, ($y2 - $y1) * 72)
    $shape.Line.ForeColor.RGB = Get-OfficeColor $color
    $shape.Line.Weight = $weight
    return $shape
}

function Add-BaseSlide($presentation, [string]$background, [string]$title, [string]$eyebrow, [int]$number, [string]$source) {
    $slide = $presentation.Slides.Add($presentation.Slides.Count + 1, 12)
    $slide.Background.Fill.Solid()
    $slide.Background.Fill.ForeColor.RGB = Get-OfficeColor $background
    Add-Text $slide 0.62 0.38 11.8 0.24 $eyebrow.ToUpperInvariant() 10 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 0.62 0.72 12.0 0.56 $title 27 '#18211F' 'Georgia' $true | Out-Null
    Add-Line $slide 0.62 6.98 12.72 6.98 '#D6DDD5' 0.8 | Out-Null
    Add-Text $slide 0.62 7.07 10.9 0.18 $source 8 '#64716B' | Out-Null
    Add-Text $slide 12.0 7.04 0.7 0.2 ('0' + $number) 9 '#64716B' 'Aptos' $true | Out-Null
    return $slide
}

function Add-Card($slide, [double]$x, [double]$y, [double]$width, [double]$height, [string]$heading, [string]$body, [string]$accent = '#D8EF72', [string]$fill = '#FFFFFF') {
    Add-Rect $slide $x $y $width $height $fill $true '#E2E6DF' | Out-Null
    Add-Rect $slide ($x + 0.18) ($y + 0.22) 0.06 ($height - 0.44) $accent | Out-Null
    Add-Text $slide ($x + 0.4) ($y + 0.23) ($width - 0.58) 0.36 $heading 15 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide ($x + 0.4) ($y + 0.72) ($width - 0.58) ($height - 0.84) $body 12 '#45534D' | Out-Null
}

try {
    $powerPoint = New-Object -ComObject PowerPoint.Application
    $powerPoint.Visible = 1
    $presentation = $powerPoint.Presentations.Add()
    $presentation.PageSetup.SlideSize = 16

    # 1. Cover
    $slide = $presentation.Slides.Add(1, 12)
    $slide.Background.Fill.Solid()
    $slide.Background.Fill.ForeColor.RGB = Get-OfficeColor '#18211F'
    Add-Text $slide 0.8 0.62 5.2 0.3 'YASK-CIEM  /  SOLUTION OVERVIEW' 11 '#D8EF72' 'Aptos' $true | Out-Null
    Add-Text $slide 0.8 1.65 7.0 1.55 'Make cloud access\nexplainable.' 36 '#F6F4EC' 'Georgia' $true | Out-Null
    Add-Text $slide 0.82 3.65 5.3 0.9 'A Windows-first, read-only view of identity, entitlement, and risk across cloud providers.' 17 '#C5D0C9' | Out-Null
    Add-Text $slide 0.82 6.63 5.0 0.3 'Engineering overview  •  October 2026' 10 '#AAB7AF' | Out-Null
    Add-Rect $slide 8.45 1.35 4.0 4.95 '#26332F' $true '#3A4A43' | Out-Null
    Add-Text $slide 8.85 1.75 3.2 0.4 'ONE QUESTION' 10 '#D8EF72' 'Aptos' $true | Out-Null
    Add-Text $slide 8.85 2.28 3.15 1.2 'Who can do\nwhat, where?' 25 '#F6F4EC' 'Georgia' $true | Out-Null
    Add-Line $slide 8.86 3.78 11.95 3.78 '#52625A' 1 | Out-Null
    Add-Text $slide 8.85 4.04 3.1 0.34 'AZURE' 11 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 10.0 4.04 2.1 0.34 'AWS' 11 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 8.85 4.55 3.1 0.34 'GOOGLE CLOUD' 11 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 8.85 5.35 3.1 0.58 'Read-only discovery  /  local evidence' 11 '#AAB7AF' | Out-Null

    # 2. The problem
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'Access is fragmented by cloud and identity layer' '01 / The problem' 2 'Sources: README.md; docs/architecture/domain-model.md'
    Add-Text $slide 0.72 1.48 11.6 0.46 'Teams need one defensible answer, not three provider consoles and a pile of role names.' 16 '#45534D' | Out-Null
    Add-Card $slide 0.72 2.35 3.75 1.54 'Different policy languages' 'Azure RBAC, AWS IAM, and GCP IAM express grants differently.' '#72B7A1'
    Add-Card $slide 4.8 2.35 3.75 1.54 'Access is indirect' 'Groups, inheritance, scopes, conditions, and role chains obscure the real path.' '#D8EF72'
    Add-Card $slide 8.88 2.35 3.75 1.54 'Risk needs evidence' 'A powerful role alone is not proof of excessive access or business impact.' '#F07D5D'
    Add-Rect $slide 0.72 4.55 11.91 1.27 '#E7ECE3' $true '#E7ECE3' | Out-Null
    Add-Text $slide 1.05 4.84 11.2 0.62 'YASK-CIEM design goal: preserve provider meaning while making access and findings comparable.' 18 '#18211F' 'Georgia' $true | Out-Null

    # 3. Data path
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'From provider data to a finding you can inspect' '02 / The analysis path' 3 'Sources: docs/cloud/provider-execution.md; docs/architecture/entitlement-normalization.md; docs/architecture/risk-engine.md'
    Add-Rect $slide 0.62 2.0 2.28 2.75 '#FFFFFF' $true '#E2E6DF' | Out-Null
    Add-Text $slide 0.82 2.22 1.7 0.28 '01' 11 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 0.82 2.68 1.85 0.36 'Collect' 17 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 0.82 3.25 1.85 1.1 'Read-only CLI discovery from Azure, AWS, and GCP.' 11 '#45534D' | Out-Null
    Add-Line $slide 2.91 3.33 3.10 3.33 '#F07D5D' 2 | Out-Null
    Add-Rect $slide 3.14 2.0 2.28 2.75 '#FFFFFF' $true '#E2E6DF' | Out-Null
    Add-Text $slide 3.34 2.22 1.7 0.28 '02' 11 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 3.34 2.68 1.85 0.36 'Normalize' 17 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 3.34 3.25 1.85 1.1 'Map identities, roles, permissions, scopes, and source evidence.' 11 '#45534D' | Out-Null
    Add-Line $slide 5.43 3.33 5.62 3.33 '#F07D5D' 2 | Out-Null
    Add-Rect $slide 5.66 2.0 2.28 2.75 '#FFFFFF' $true '#E2E6DF' | Out-Null
    Add-Text $slide 5.86 2.22 1.7 0.28 '03' 11 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 5.86 2.68 1.85 0.36 'Resolve' 17 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 5.86 3.25 1.85 1.1 'Evaluate effective access with uncertainty and provider semantics.' 11 '#45534D' | Out-Null
    Add-Line $slide 7.95 3.33 8.14 3.33 '#F07D5D' 2 | Out-Null
    Add-Rect $slide 8.18 2.0 2.28 2.75 '#FFFFFF' $true '#E2E6DF' | Out-Null
    Add-Text $slide 8.38 2.22 1.7 0.28 '04' 11 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 8.38 2.68 1.85 0.36 'Assess' 17 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 8.38 3.25 1.85 1.1 'Apply risk rules to access, paths, and activity evidence.' 11 '#45534D' | Out-Null
    Add-Line $slide 10.47 3.33 10.66 3.33 '#F07D5D' 2 | Out-Null
    Add-Rect $slide 10.70 2.0 2.0 2.75 '#FFFFFF' $true '#E2E6DF' | Out-Null
    Add-Text $slide 10.9 2.22 1.7 0.28 '05' 11 '#668277' 'Aptos' $true | Out-Null
    Add-Text $slide 10.9 2.68 1.6 0.36 'Explain' 17 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 10.9 3.25 1.55 1.1 'Persist, filter, review, and export findings.' 11 '#45534D' | Out-Null
    Add-Text $slide 0.72 5.3 11.7 0.55 'Boundary: provider adapters collect; normalization preserves meaning; analysis engines do not call cloud APIs.' 15 '#45534D' 'Aptos' $true | Out-Null

    # 4. Architecture
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'Modular architecture keeps cloud semantics out of the core' '03 / Architecture' 4 'Sources: docs/architecture/architecture.md; CloudEntitle.sln; src/CloudEntitle.Desktop/CloudEntitle.Desktop.csproj'
    $columns = @(
        @('PROVIDER EDGE', '#72B7A1', 'Cloud.Azure\nCloud.Aws\nCloud.Gcp\nCLI-backed discovery'),
        @('SHARED ANALYSIS', '#D8EF72', 'Domain + Application\nNormalization\nEffective Access\nAccess Path + Risk'),
        @('LOCAL PRODUCT', '#F07D5D', 'WPF / MVVM desktop\nSQLite + EF Core\nReporting + Licensing\nWindows-first workflow')
    )
    $x = 0.75
    foreach ($column in $columns) {
        Add-Rect $slide $x 1.75 3.42 3.45 '#FFFFFF' $true '#E2E6DF' | Out-Null
        Add-Rect $slide $x 1.75 3.42 0.13 $column[1] | Out-Null
        Add-Text $slide ($x + 0.27) 2.13 2.85 0.3 $column[0] 10 '#668277' 'Aptos' $true | Out-Null
        Add-Text $slide ($x + 0.27) 2.72 2.86 1.7 $column[2] 16 '#18211F' 'Aptos' $true | Out-Null
        $x += 4.0
    }
    Add-Line $slide 4.2 3.46 4.65 3.46 '#668277' 2 | Out-Null
    Add-Line $slide 8.2 3.46 8.65 3.46 '#668277' 2 | Out-Null
    Add-Text $slide 0.78 5.68 11.6 0.55 '24 source projects span analysis, governance, compliance, monitoring, reporting, and integration; the desktop app composes the user-facing workflow.' 13 '#45534D' | Out-Null

    # 5. Decision quality
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'Trust comes from showing uncertainty, not hiding it' '04 / Decision quality' 5 'Sources: docs/architecture/effective-access-engine.md; docs/architecture/risk-engine.md'
    Add-Text $slide 0.72 1.52 5.2 0.34 'ACCESS DECISIONS' 10 '#668277' 'Aptos' $true | Out-Null
    $states = @(@('ALLOW', '#DDEEDB'), @('DENY', '#F7D9D0'), @('CONDITIONAL', '#F5E8BC'), @('UNKNOWN', '#E4E8E2'), @('UNSUPPORTED', '#E7DFEA'))
    $x = 0.72
    foreach ($state in $states) {
        Add-Rect $slide $x 2.02 1.63 0.6 $state[1] $true $state[1] | Out-Null
        Add-Text $slide ($x + 0.1) 2.19 1.44 0.2 $state[0] 9 '#18211F' 'Aptos' $true | Out-Null
        $x += 1.77
    }
    Add-Card $slide 0.72 3.15 3.75 1.7 'No false Allow' 'Missing runtime context for a condition remains Conditional, never Allow.' '#72B7A1'
    Add-Card $slide 4.8 3.15 3.75 1.7 'No automatic blame' 'Powerful access, service identities, and missing activity are not automatically called risky.' '#D8EF72'
    Add-Card $slide 8.88 3.15 3.75 1.7 'Evidence stays attached' 'Findings reference provider evidence, affected entities, rule, and lifecycle state.' '#F07D5D'
    Add-Text $slide 0.75 5.55 11.6 0.36 'The engine can say “not enough information yet.” That is a product feature.' 17 '#18211F' 'Georgia' $true | Out-Null

    # 6. Current implementation
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'What the repository says works today' '05 / Current documented implementation' 6 'Sources: README.md; docs/cloud/provider-execution.md; docs/licensing/licensing.md'
    Add-Card $slide 0.72 1.72 5.75 3.72 'In the documented desktop build' 'WPF desktop shell; local SQLite inventory, rules, and findings; provider-filtered views; read-only CLI discovery for Azure, AWS, and GCP; Azure Entra identities and PIM schedules; JSON report export; fourteen-day local trial.' '#72B7A1'
    Add-Card $slide 6.82 1.72 5.75 3.72 'Architecture already has dedicated modules' 'Normalization, Effective Access, Access Path, Risk Engine, Recommendations, Remediation Planning, Identity Governance, Compliance, Analytics, Monitoring, and more. Module presence is not the same as production validation.' '#D8EF72'
    Add-Rect $slide 0.72 5.8 11.9 0.58 '#F5E8BC' $true '#F5E8BC' | Out-Null
    Add-Text $slide 0.95 5.95 11.3 0.27 'Readiness caveat: the provider guide claims broader scan behavior than the README summary. Verify against a live tenant before making release claims.' 11 '#493E27' 'Aptos' $true | Out-Null

    # 7. Boundaries and readiness
    $slide = Add-BaseSlide $presentation '#F6F4EC' 'Clear safety boundaries; a few release gaps remain' '06 / Boundaries and readiness' 7 'Sources: README.md; docs/licensing/licensing.md; docs/architecture/architecture.md; CloudEntitle.Desktop.csproj'
    Add-Card $slide 0.72 1.72 5.75 2.0 'Safety posture' 'Current provider scans are read-only. Credentials are not persisted in the domain model. Missing or unauthenticated CLIs should be reported rather than silently treated as empty inventory.' '#72B7A1'
    Add-Card $slide 6.82 1.72 5.75 2.0 'License enforcement' 'The fourteen-day trial is local and explicitly not tamper-proof. Remote validation/activation support exists in parts, but the desktop flow is not integrated.' '#F07D5D'
    Add-Card $slide 0.72 4.02 5.75 1.72 'Framework target' 'Desktop project targets net9.0-windows; the project brief states .NET 10. Align target framework and CI before production release.' '#D8EF72'
    Add-Card $slide 6.82 4.02 5.75 1.72 'Evidence of readiness' 'Validate end-to-end scans against real cloud tenants, provider permissions, incomplete-data behavior, and exported findings.' '#72B7A1'

    # 8. Close / priorities
    $slide = Add-BaseSlide $presentation '#18211F' 'The product promise is strong; prove the full loop' '07 / Recommended next steps' 8 'Analysis based on README.md, solution project files, and docs/architecture + docs/cloud + docs/licensing'
    Add-Text $slide 0.72 1.52 4.7 0.35 'A practical sequence' 11 '#D8EF72' 'Aptos' $true | Out-Null
    Add-Rect $slide 0.78 2.12 0.52 0.52 '#D8EF72' $true '#D8EF72' | Out-Null
    Add-Text $slide 0.93 2.25 0.25 0.2 '1' 11 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 1.58 2.11 2.3 0.34 'Reconcile scope' 16 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 4.0 2.11 7.7 0.66 'Create a code-backed capability matrix that resolves differences between README, architecture docs, and provider guide.' 12 '#C5D0C9' | Out-Null
    Add-Rect $slide 0.78 3.37 0.52 0.52 '#D8EF72' $true '#D8EF72' | Out-Null
    Add-Text $slide 0.93 3.5 0.25 0.2 '2' 11 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 1.58 3.36 2.3 0.34 'Prove live scans' 16 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 4.0 3.36 7.7 0.66 'Run Azure, AWS, and GCP end to end; verify least-privilege permissions, partial failures, and evidence quality.' 12 '#C5D0C9' | Out-Null
    Add-Rect $slide 0.78 4.62 0.52 0.52 '#D8EF72' $true '#D8EF72' | Out-Null
    Add-Text $slide 0.93 4.75 0.25 0.2 '3' 11 '#18211F' 'Aptos' $true | Out-Null
    Add-Text $slide 1.58 4.61 2.3 0.34 'Close release gaps' 16 '#F6F4EC' 'Aptos' $true | Out-Null
    Add-Text $slide 4.0 4.61 7.7 0.66 'Integrate remote license enforcement and align the shipped framework target with the .NET 10 brief.' 12 '#C5D0C9' | Out-Null
    Add-Text $slide 0.78 6.12 11.7 0.4 'North star: an evidence-backed answer to who can do what, where, and why.' 17 '#D8EF72' 'Georgia' $true | Out-Null

    $presentation.SaveAs($outputPath)
    Write-Output "Created $outputPath"
    Write-Output "Slides: $($presentation.Slides.Count)"
}
finally {
    if ($presentation -ne $null) { $presentation.Close() }
    if ($powerPoint -ne $null) { $powerPoint.Quit() }
}
