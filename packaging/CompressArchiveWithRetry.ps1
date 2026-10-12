function Compress-ArchiveWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,
        [ValidateSet('Optimal', 'Fastest', 'NoCompression')]
        [string]$CompressionLevel = 'Optimal',
        [ValidateRange(1, 10)]
        [int]$MaxAttempts = 6
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Compress-Archive -Path $Path -DestinationPath $DestinationPath -CompressionLevel $CompressionLevel -Force -ErrorAction Stop
            return
        }
        catch {
            $isSharingViolation = $false
            for ($exception = $_.Exception; $null -ne $exception; $exception = $exception.InnerException) {
                if ($exception -is [IO.IOException] -and (($exception.HResult -band 0xffff) -in @(32, 33))) {
                    $isSharingViolation = $true
                    break
                }
            }

            if (-not $isSharingViolation -or $attempt -eq $MaxAttempts) {
                throw
            }

            $delayMilliseconds = [int][Math]::Min(200 * [Math]::Pow(2, $attempt - 1), 2000)
            Start-Sleep -Milliseconds $delayMilliseconds
        }
    }
}
