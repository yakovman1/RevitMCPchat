[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'RevitMcpChat'),
    [string]$AddinsRoot = (Join-Path $env:APPDATA 'Autodesk\Revit\Addins'),
    [int[]]$RevitYears = @(2022, 2023, 2024),
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$installPath = [IO.Path]::GetFullPath($InstallRoot)
$addinsPath = [IO.Path]::GetFullPath($AddinsRoot)
foreach ($year in $RevitYears) {
    if ($year -notin @(2022, 2023, 2024)) {
        throw "Revit $year is not supported by this uninstaller."
    }

    $manifest = Join-Path (Join-Path $addinsPath "$year") 'RevitMcpChat.addin'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        Remove-Item -LiteralPath $manifest -Force
    }

    $yearAddin = Join-Path (Join-Path $installPath 'addin') "$year"
    if (Test-Path -LiteralPath $yearAddin -PathType Container) {
        Remove-Item -LiteralPath $yearAddin -Recurse -Force
    }
}

$agentDirectory = Join-Path $installPath 'agent'
if (Test-Path -LiteralPath $agentDirectory -PathType Container) {
    Remove-Item -LiteralPath $agentDirectory -Recurse -Force
}

if ($Purge -and (Test-Path -LiteralPath $installPath -PathType Container)) {
    Remove-Item -LiteralPath $installPath -Recurse -Force
    Write-Host 'Revit MCP Chat program files and user data were removed.'
}
else {
    Write-Host 'Revit MCP Chat add-in and agent were removed. User history, settings and DPAPI keys were preserved.'
    Write-Host 'Use -Purge only when you also want to remove that user data.'
}
