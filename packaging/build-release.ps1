[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AdminKeysFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$buildId = [Guid]::NewGuid().ToString('N')
$stagePath = Join-Path $outputPath ".stage-$buildId"
$workPath = Join-Path $outputPath ".work-$buildId"
$adminKeySource = $null
$buildFailed = $false

function ConvertTo-CSharpLiteral([string]$Value) {
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    foreach ($character in $Value.ToCharArray()) {
        switch ([int]$character) {
            34 { [void]$builder.Append('\"') }
            92 { [void]$builder.Append('\\') }
            8 { [void]$builder.Append('\b') }
            9 { [void]$builder.Append('\t') }
            10 { [void]$builder.Append('\n') }
            12 { [void]$builder.Append('\f') }
            13 { [void]$builder.Append('\r') }
            default {
                if ([char]::IsControl($character)) {
                    [void]$builder.Append('\u{0:x4}' -f [int]$character)
                }
                else {
                    [void]$builder.Append($character)
                }
            }
        }
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-DotNet([string[]]$Arguments, [bool]$Quiet) {
    if ($Quiet) {
        $output = & dotnet @Arguments 2>&1
    }
    else {
        & dotnet @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        if ($Quiet) {
            throw 'Private agent build failed. Check the SDK and package restore, then retry.'
        }
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

. (Join-Path $PSScriptRoot 'CompressArchiveWithRetry.ps1')

function Read-AdminKeys([string]$Path) {
    try {
        $json = [IO.File]::ReadAllText([IO.Path]::GetFullPath($Path))
        $document = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    }
    catch {
        throw 'The admin keys file could not be parsed. Expected a JSON object mapping provider IDs to API keys.'
    }

    if ($null -eq $document -or $document -isnot [PSCustomObject]) {
        throw 'The admin keys file must be a JSON object mapping provider IDs to API keys.'
    }

    $keys = [Collections.Generic.List[object]]::new()
    foreach ($property in $document.PSObject.Properties) {
        $providerId = $property.Name.Trim()
        $key = [string]$property.Value
        if ($providerId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$' -or [string]::IsNullOrWhiteSpace($key)) {
            throw 'The admin keys file contains an invalid provider ID or empty key.'
        }

        $keys.Add([pscustomobject]@{ ProviderId = $providerId; Key = $key })
    }

    if ($keys.Count -eq 0) {
        throw 'The admin keys file does not contain any API keys.'
    }

    return $keys
}

try {
    if ($AdminKeysFile -and -not (Test-Path -LiteralPath $AdminKeysFile -PathType Leaf)) {
        throw 'The specified admin keys file does not exist.'
    }

    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    New-Item -ItemType Directory -Path $workPath -Force | Out-Null

    if ($AdminKeysFile) {
        $keys = Read-AdminKeys $AdminKeysFile
        $sourceDirectory = Join-Path $workPath 'admin-key-source'
        New-Item -ItemType Directory -Path $sourceDirectory -Force | Out-Null
        $adminKeySource = Join-Path $sourceDirectory 'GeneratedAdminKeySource.cs'
        $entries = foreach ($item in $keys) {
            '            [{0}] = {1}' -f (ConvertTo-CSharpLiteral $item.ProviderId), (ConvertTo-CSharpLiteral $item.Key)
        }
        $source = @"
using System;
using System.Collections.Generic;
using RevitMcpChat.Agent.Settings;

internal sealed class GeneratedAdminKeySource : IAdminKeySource
{
    private static readonly Dictionary<string, string> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
$($entries -join ",`r`n")
    };

    public string? GetKey(string providerId) =>
        providerId != null && Keys.TryGetValue(providerId, out var key) ? key : null;
}
"@
        [IO.File]::WriteAllText($adminKeySource, $source, [Text.UTF8Encoding]::new($false))
    }

    Write-Host "Building Revit MCP Chat $Version ($Configuration)"
    foreach ($year in @(2022, 2023, 2024)) {
        $addinDirectory = Join-Path $stagePath "addins\$year"
        New-Item -ItemType Directory -Path $addinDirectory -Force | Out-Null
        $addinProject = Join-Path $repoRoot 'RevitMCPchat\RevitMCPchat.csproj'
        Invoke-DotNet -Arguments @('build', $addinProject, '-c', $Configuration, "-p:RevitYear=$year", '-o', $addinDirectory, '--nologo', '-v:minimal') -Quiet $false
        Get-ChildItem -LiteralPath $addinDirectory -Filter '*.pdb' -File | Remove-Item -Force

        $manifestDirectory = Join-Path $stagePath 'manifests'
        New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'RevitMcpChat.addin.template') (Join-Path $manifestDirectory "RevitMcpChat-$year.addin.template")
    }

    $agentDirectory = Join-Path $stagePath 'agent'
    New-Item -ItemType Directory -Path $agentDirectory -Force | Out-Null
    $agentProject = Join-Path $repoRoot 'src\RevitMcpChat.Agent\RevitMcpChat.Agent.csproj'
    $publishArguments = @(
        'publish', $agentProject, '-c', $Configuration, '-r', 'win-x64',
        '--self-contained', 'true', '-o', $agentDirectory, '--nologo', '-v:minimal',
        '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
        '-p:DebugType=None', "-p:Version=$Version"
    )
    if ($adminKeySource) {
        $publishArguments += "-p:AdminKeySourcePath=$adminKeySource"
    }
    Invoke-DotNet -Arguments $publishArguments -Quiet ([bool]$adminKeySource)

    foreach ($file in @('install.ps1', 'uninstall.ps1', 'verify-package.ps1')) {
        Copy-Item (Join-Path $PSScriptRoot $file) (Join-Path $stagePath $file)
    }
    Copy-Item (Join-Path $repoRoot 'README.md') (Join-Path $stagePath 'README.md')
    $docsDirectory = Join-Path $stagePath 'docs'
    New-Item -ItemType Directory -Path $docsDirectory -Force | Out-Null
    Copy-Item (Join-Path $repoRoot 'docs\install.md') (Join-Path $docsDirectory 'install.md')
    Copy-Item (Join-Path $repoRoot 'docs\test-matrix.md') (Join-Path $docsDirectory 'test-matrix.md')

    $verifyScript = Join-Path $stagePath 'verify-package.ps1'
    & $verifyScript -PackageRoot $stagePath

    $variant = if ($AdminKeysFile) { '-private' } else { '' }
    $packagePath = Join-Path $outputPath "RevitMcpChat-$Version$variant-win-x64.zip"
    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Force
    }
    Compress-ArchiveWithRetry -Path (Join-Path $stagePath '*') -DestinationPath $packagePath -CompressionLevel Optimal
    Write-Host "Package created: $packagePath"
}
catch {
    $buildFailed = $true
    throw
}
finally {
    if ($adminKeySource -and (Test-Path -LiteralPath $adminKeySource)) {
        try {
            $length = (Get-Item -LiteralPath $adminKeySource).Length
            if ($length -gt 0) {
                [IO.File]::WriteAllBytes($adminKeySource, [byte[]]::new([int]$length))
            }
        }
        catch {
            Write-Warning 'Could not overwrite the temporary generated admin-key source before deleting it.'
        }
    }

    foreach ($temporaryPath in @($stagePath, $workPath)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Recurse -Force
        }
    }
    if ($buildFailed -and $adminKeySource) {
        Write-Warning 'Private build failed; its temporary key source was removed on a best-effort basis.'
    }
}
