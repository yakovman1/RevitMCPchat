[CmdletBinding()]
param(
    [string]$PackageRoot = $PSScriptRoot,
    [string]$RvtMcpPath,
    [int]$RevitPid
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PackageRoot)
$requiredPaths = @(
    'agent\RevitMcpChat.Agent.exe',
    'install.ps1',
    'uninstall.ps1',
    'verify-package.ps1',
    'README.md',
    'docs\install.md'
)
foreach ($year in @(2022, 2023, 2024)) {
    $requiredPaths += "addins\$year\RevitMCPchat.dll"
    $requiredPaths += "addins\$year\RevitMcpChat.Contracts.dll"
    $requiredPaths += "manifests\RevitMcpChat-$year.addin.template"
}
foreach ($relativePath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relativePath) -PathType Leaf)) {
        throw "Package file is missing: $relativePath"
    }
}

$forbidden = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    $_.Name -eq 'GeneratedAdminKeySource.cs' -or $_.Name -match '^admin-keys(\.json)?$'
}
if ($forbidden) {
    throw 'The package contains temporary admin-key source or configuration. Rebuild the package.'
}

if (-not [string]::IsNullOrWhiteSpace($RvtMcpPath)) {
    if (-not (Test-Path -LiteralPath $RvtMcpPath -PathType Leaf)) {
        throw "rvt-mcp was not found at '$RvtMcpPath'. Install it from https://github.com/bimwright/rvt-mcp."
    }
    if ($RevitPid -gt 0) {
        $agent = Join-Path $root 'agent\RevitMcpChat.Agent.exe'
        & $agent verify-mcp --revit-pid $RevitPid --server-path $RvtMcpPath
        if ($LASTEXITCODE -ne 0) {
            throw "rvt-mcp MCP initialization or required Revit tool capabilities failed. Update rvt-mcp from https://github.com/bimwright/rvt-mcp."
        }
    }
    else {
        Write-Host 'rvt-mcp executable exists. Supply -RevitPid while Revit is open to run MCP initialization and capability checks.'
    }
}

Write-Host 'Package layout is valid for Revit 2022, 2023 and 2024.'
