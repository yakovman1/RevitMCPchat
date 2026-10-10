[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'RevitMcpChat'),
    [string]$AddinsRoot = (Join-Path $env:APPDATA 'Autodesk\Revit\Addins'),
    [int[]]$RevitYears,
    [string]$RvtMcpPath = (Join-Path $env:LOCALAPPDATA 'Bimwright\rvt-mcp\server\current\rvt-mcp.exe')
)

$ErrorActionPreference = 'Stop'
$supportedYears = @(2022, 2023, 2024)
$packagePath = [IO.Path]::GetFullPath($PackageRoot)
$installPath = [IO.Path]::GetFullPath($InstallRoot)
$addinsPath = [IO.Path]::GetFullPath($AddinsRoot)

if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
    throw "Package folder was not found: $packagePath"
}
& (Join-Path $packagePath 'verify-package.ps1') -PackageRoot $packagePath

if (-not $RevitYears -or $RevitYears.Count -eq 0) {
    $RevitYears = foreach ($year in $supportedYears) {
        $exe = Join-Path $env:ProgramFiles "Autodesk\Revit $year\Revit.exe"
        $addinDirectory = Join-Path $addinsPath "$year"
        if ((Test-Path -LiteralPath $exe -PathType Leaf) -or (Test-Path -LiteralPath $addinDirectory -PathType Container)) {
            $year
        }
    }
}

$RevitYears = @($RevitYears | Select-Object -Unique)
if ($RevitYears.Count -eq 0) {
    throw 'No Revit 2022–2024 installation was detected. Install Revit or specify -RevitYears 2022,2023,2024.'
}
foreach ($year in $RevitYears) {
    if ($year -notin $supportedYears) {
        throw "Revit $year is not supported by this package. Supported years: 2022, 2023, 2024."
    }
}

if (-not (Test-Path -LiteralPath $RvtMcpPath -PathType Leaf)) {
    Write-Warning "rvt-mcp was not found at '$RvtMcpPath'. Install it separately from https://github.com/bimwright/rvt-mcp; this installer will not download or modify it."
}
else {
    $revitProcesses = @(Get-Process -Name Revit -ErrorAction SilentlyContinue)
    if ($revitProcesses.Count -eq 0) {
        Write-Host 'rvt-mcp executable found. Open Revit and reconnect in the chat to complete the MCP initialization check.'
    }
    else {
        $agent = Join-Path $packagePath 'agent\RevitMcpChat.Agent.exe'
        foreach ($process in $revitProcesses) {
            $probe = & $agent verify-mcp --revit-pid $process.Id --server-path $RvtMcpPath 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "rvt-mcp MCP initialization succeeded for Revit PID $($process.Id)."
                break
            }

            Write-Warning (($probe | Out-String).Trim())
            if ($process -eq $revitProcesses[-1]) {
                Write-Warning 'The installed rvt-mcp could not be verified. Update it or reconnect after restarting Revit.'
            }
        }
    }
}

$agentSource = Join-Path $packagePath 'agent'
$addinSource = Join-Path $packagePath 'addins'
$agentDestination = Join-Path $installPath 'agent'
New-Item -ItemType Directory -Path $agentDestination -Force | Out-Null
Copy-Item (Join-Path $agentSource '*') $agentDestination -Recurse -Force

foreach ($year in $RevitYears) {
    $yearText = $year.ToString([Globalization.CultureInfo]::InvariantCulture)
    $destination = Join-Path (Join-Path $installPath 'addin') $yearText
    $revitAddins = Join-Path $addinsPath $yearText
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    New-Item -ItemType Directory -Path $revitAddins -Force | Out-Null
    Copy-Item (Join-Path (Join-Path $addinSource $yearText) '*') $destination -Recurse -Force

    $manifestTemplate = Join-Path (Join-Path $packagePath 'manifests') "RevitMcpChat-$yearText.addin.template"
    $manifestText = [IO.File]::ReadAllText($manifestTemplate)
    $escapedDirectory = [Security.SecurityElement]::Escape($destination)
    $manifestText = $manifestText.Replace('__ADDIN_DIRECTORY__', $escapedDirectory)
    [IO.File]::WriteAllText(
        (Join-Path $revitAddins 'RevitMcpChat.addin'),
        $manifestText,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "Revit MCP Chat installed for Revit $($RevitYears -join ', ')."
Write-Host "Agent files: $agentDestination"
Write-Host 'User history, settings and DPAPI keys are stored separately and were not changed.'
