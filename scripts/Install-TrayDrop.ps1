[CmdletBinding()]
param(
    [string]$SourceDirectory,
    [switch]$StartWithWindows
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'TrayDrop can only be installed on Windows.'
}

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'TrayDrop.exe') -PathType Leaf) {
        $PSScriptRoot
    } else {
        Join-Path $PSScriptRoot '..\artifacts\TrayDrop'
    }
}

$source = (Resolve-Path -LiteralPath $SourceDirectory).ProviderPath.TrimEnd([IO.Path]::DirectorySeparatorChar)
$sourceExecutable = Join-Path $source 'TrayDrop.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "TrayDrop.exe was not found in '$source'. Publish the self-contained Windows build first."
}

$installDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\TrayDrop'
if ([string]::Equals($source, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The published build must be in a different folder from the installation.'
}

$assetDirectory = $source
$guide = Join-Path $source 'HOW_TO_USE.md'
if (-not (Test-Path -LiteralPath $guide -PathType Leaf)) {
    $assetDirectory = Split-Path -Parent $PSScriptRoot
    $guide = Join-Path $assetDirectory 'docs\HOW_TO_USE.md'
}
$assetFiles = @(
    $guide
    (Join-Path $assetDirectory 'LICENSE')
    (Join-Path $assetDirectory 'THIRD_PARTY_NOTICES.md')
)
foreach ($file in $assetFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required guide or license file is missing: '$file'. Use the complete extracted release or source checkout."
    }
}
$licenses = Join-Path $assetDirectory 'licenses'
if (-not (Test-Path -LiteralPath $licenses -PathType Container)) {
    throw "The dependency licenses folder is missing: '$licenses'. Use the complete extracted release or source checkout."
}

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$running = @([Diagnostics.Process]::GetProcessesByName('TrayDrop') |
    Where-Object { $_.SessionId -eq $sessionId })
if ($running.Count -ne 0) {
    throw 'TrayDrop is running. Finish any transfers, choose Exit in its tray menu, then run this script again.'
}

$null = New-Item -ItemType Directory -Path $installDirectory -Force
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $installDirectory -Recurse -Force

if (-not [string]::Equals($assetDirectory, $source, [StringComparison]::OrdinalIgnoreCase)) {
    foreach ($file in $assetFiles) {
        Copy-Item -LiteralPath $file -Destination $installDirectory -Force
    }
    Copy-Item -LiteralPath $licenses -Destination $installDirectory -Recurse -Force
}

$executable = Join-Path $installDirectory 'TrayDrop.exe'
$shell = New-Object -ComObject WScript.Shell

function Set-TrayDropShortcut {
    param([string]$Path, [string]$Arguments)

    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $executable
    $shortcut.Arguments = $Arguments
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.Description = 'TrayDrop local file transfer'
    $shortcut.IconLocation = "$executable,0"
    $shortcut.Save()
}

$startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'TrayDrop.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'TrayDrop.lnk'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'TrayDrop.lnk'
Set-TrayDropShortcut -Path $startMenuShortcut -Arguments '--show'
Set-TrayDropShortcut -Path $desktopShortcut -Arguments '--show'

if ($StartWithWindows) {
    Set-TrayDropShortcut -Path $startupShortcut -Arguments ''
} elseif (Test-Path -LiteralPath $startupShortcut) {
    Remove-Item -LiteralPath $startupShortcut
}

Write-Output "Installed TrayDrop to: $installDirectory"
Write-Output 'Open TrayDrop from Start or the desktop shortcut to show its panel.'
Write-Output "Start with Windows: $($StartWithWindows.IsPresent)"
Write-Output 'Existing settings and received files were not moved or deleted.'
