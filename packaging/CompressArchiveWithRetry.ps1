function Compress-ArchiveWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [ValidateSet('Optimal', 'Fastest', 'NoCompression')]
        [string]$CompressionLevel = 'Optimal',
        [ValidateRange(1, 10)]
        [int]$MaxAttempts = 6
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory).TrimEnd([char[]]@('\', '/')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Directory]::Exists($sourceRoot)) {
        throw "Archive source directory does not exist: $SourceDirectory"
    }
    $compression = [Enum]::Parse([IO.Compression.CompressionLevel], $CompressionLevel, $true)

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $archiveStream = $null
        $archive = $null
        try {
            $archiveStream = [IO.File]::Open(
                $DestinationPath,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            $archive = [IO.Compression.ZipArchive]::new(
                $archiveStream,
                [IO.Compression.ZipArchiveMode]::Create)
            foreach ($file in [IO.Directory]::EnumerateFiles($sourceRoot, '*', [IO.SearchOption]::AllDirectories)) {
                $entryName = $file.Substring($sourceRoot.Length).Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $file,
                    $entryName,
                    $compression)
            }
            $archive.Dispose()
            $archive = $null
            $archiveStream.Dispose()
            $archiveStream = $null
            return
        }
        catch {
            $operationError = $_
            if ($null -ne $archive) {
                try {
                    $archive.Dispose()
                }
                catch {
                    # Preserve the operation failure; still close the underlying stream.
                }
            }
            if ($null -ne $archiveStream) {
                try {
                    $archiveStream.Dispose()
                }
                catch {
                    # Preserve the operation failure so retry classification uses its HRESULT.
                }
            }

            $isSharingViolation = $false
            for ($exception = $operationError.Exception; $null -ne $exception; $exception = $exception.InnerException) {
                if ($exception -is [IO.IOException] -and (($exception.HResult -band 0xffff) -in @(32, 33))) {
                    $isSharingViolation = $true
                    break
                }
            }

            if (-not $isSharingViolation -or $attempt -eq $MaxAttempts) {
                throw $operationError
            }

            if (Test-Path -LiteralPath $DestinationPath) {
                Remove-Item -LiteralPath $DestinationPath -Force
            }
            $delayMilliseconds = [int][Math]::Min(200 * [Math]::Pow(2, $attempt - 1), 2000)
            Start-Sleep -Milliseconds $delayMilliseconds
        }
    }
}
