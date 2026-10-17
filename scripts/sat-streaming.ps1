# MusicLoom streaming SAT launcher. No account credentials are stored.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MusicLoomExe,
    [ValidateSet('Spotify', 'ApplePreview', 'AppleFullHls', 'Migu')]
    [string]$Provider = 'Spotify',
    [string]$SpotifyLocalUrl = 'http://127.0.0.1:3678',
    [switch]$PreflightOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolved = (Resolve-Path -LiteralPath $MusicLoomExe -ErrorAction Stop).Path
if (-not [IO.Path]::GetFileName($resolved).Equals('MusicLoom.exe',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use the published MusicLoom.exe from the Windows app CI artifact.'
}

$originalUrl = [Environment]::GetEnvironmentVariable('MUSICLOOM_GO_LIBRESPOT_URL', 'Process')
$originalHls = [Environment]::GetEnvironmentVariable('MUSICLOOM_APPLE_PREFER_FULL_HLS', 'Process')
try {
    if ($Provider -eq 'Spotify') {
        $parsed = [Uri]$SpotifyLocalUrl
        if ($parsed.Scheme -ne 'http' -or -not $parsed.IsLoopback -or
            $parsed.AbsolutePath -ne '/' -or $parsed.UserInfo -ne '' -or
            $parsed.Query -ne '') {
            throw 'Spotify local service must be plain HTTP on loopback.'
        }
        try {
            $ready = Invoke-RestMethod -Method Get -Uri "$($parsed.AbsoluteUri)" -TimeoutSec 3
            if (-not $ready.playback_ready) {
                throw 'The local Spotify decoder is running but not authenticated/playback_ready.'
            }
            Write-Host 'Spotify local decoder is ready for the MusicLoom SAT.'
        }
        catch {
            throw ('Start and authorize go-librespot (server enabled, Windows WASAPI)' +
                ' before the Spotify SAT. Details: ' + $_.Exception.Message)
        }
        [Environment]::SetEnvironmentVariable('MUSICLOOM_GO_LIBRESPOT_URL',
            $parsed.AbsoluteUri.TrimEnd('/'), 'Process')
    }
    elseif ($Provider -eq 'AppleFullHls') {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable(
                    'MUSICLOOM_APPLE_WEB_BEARER', 'Process'))) {
            throw 'Provide your own MUSICLOOM_APPLE_WEB_BEARER in this shell.'
        }
        [Environment]::SetEnvironmentVariable('MUSICLOOM_APPLE_PREFER_FULL_HLS',
            '1', 'Process')
        Write-Host 'Apple full HLS experimental mode ON. Playlist discovery is not guaranteed playback.'
    }
    elseif ($Provider -eq 'ApplePreview') {
        $web = [Environment]::GetEnvironmentVariable(
            'MUSICLOOM_APPLE_WEB_BEARER', 'Process')
        $dev = [Environment]::GetEnvironmentVariable(
            'MUSICLOOM_APPLE_DEVELOPER_TOKEN', 'Process')
        if ([string]::IsNullOrWhiteSpace($web) -and
            [string]::IsNullOrWhiteSpace($dev)) {
            throw 'Provide your own Apple web Bearer or official catalog developer token.'
        }
        [Environment]::SetEnvironmentVariable('MUSICLOOM_APPLE_PREFER_FULL_HLS',
            '0', 'Process')
    }
    else {
        Write-Host 'Migu experimental SAT: no extra login token is supplied.'
    }

    if ($PreflightOnly) {
        Write-Host 'Preflight passed. No player was launched.'
        return
    }
    Write-Host "Starting $resolved for $Provider SAT (no tokens logged)."
    & $resolved
}
finally {
    [Environment]::SetEnvironmentVariable(
        'MUSICLOOM_GO_LIBRESPOT_URL', $originalUrl, 'Process')
    [Environment]::SetEnvironmentVariable(
        'MUSICLOOM_APPLE_PREFER_FULL_HLS', $originalHls, 'Process')
}
