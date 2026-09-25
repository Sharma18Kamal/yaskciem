[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$SourcePath = '.\artifacts\user-install\publish'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $repositoryRoot 'scripts\publish-windows.ps1'
$source = Join-Path $repositoryRoot $SourcePath
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\YASK-CIEM'
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcutPath = Join-Path $startMenuDirectory 'YASK-CIEM.lnk'
$executablePath = Join-Path $installDirectory 'CloudEntitle.Desktop.exe'

& $publishScript -Configuration $Configuration -Runtime $Runtime -OutputPath $SourcePath
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

if (Test-Path $installDirectory) {
    Remove-Item $installDirectory -Recurse -Force
}
New-Item $installDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $source '*') $installDirectory -Recurse -Force

New-Item $startMenuDirectory -ItemType Directory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $executablePath
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = 'YASK-CIEM cloud identity exposure management'
$shortcut.IconLocation = "$executablePath,0"
$shortcut.Save()

Write-Host "Installed YASK-CIEM to $installDirectory"
Write-Host "Created Start Menu shortcut at $shortcutPath"
