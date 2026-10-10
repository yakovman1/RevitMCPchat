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
$localAppDataPath = [IO.Path]::GetFullPath($env:LOCALAPPDATA)
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

if ($Purge) {
    $userDataPath = Join-Path $localAppDataPath 'RevitMcpChat'
    foreach ($fileName in @('settings.json', 'history.sqlite3', 'history.sqlite3-shm', 'history.sqlite3-wal')) {
        $dataFile = Join-Path $userDataPath $fileName
        if (Test-Path -LiteralPath $dataFile -PathType Leaf) {
            Remove-Item -LiteralPath $dataFile -Force
        }
    }

    $secretsDirectory = Join-Path $userDataPath 'secrets'
    if (Test-Path -LiteralPath $secretsDirectory -PathType Container) {
        Remove-Item -LiteralPath $secretsDirectory -Recurse -Force
    }

    if ((Test-Path -LiteralPath $userDataPath -PathType Container) -and
        -not (Get-ChildItem -LiteralPath $userDataPath -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $userDataPath -Force
    }

    Write-Host 'Revit MCP Chat files and known user settings, history and keys were removed.'
}
else {
    Write-Host 'Revit MCP Chat add-in and agent were removed. User history, settings and DPAPI keys were preserved.'
    Write-Host 'Use -Purge only when you also want to remove that user data.'
}
