#Requires -Version 7
<#
.SYNOPSIS
    Re-syncs the vendored Osprey.ML sources from a ProteoWizard/pwiz checkout.

.DESCRIPTION
    MARS vendors Osprey.ML's gradient boosted trees rather than referencing an assembly,
    because pwiz has no package feed to consume yet. Osprey.ML remains the owner: bugs get
    fixed upstream, then pulled down with this script.

    Without -Apply the script only reports. With -Apply it copies the upstream file,
    re-extracts the XorShift64 fragment, and rewrites the hashes in UPSTREAM.json.

.PARAMETER PwizPath
    Path to a pwiz checkout, e.g. D:\Dev\pwiz.

.PARAMETER Apply
    Write the changes instead of only reporting them.

.EXAMPLE
    pwsh -File ./scripts/sync-osprey-ml.ps1 -PwizPath D:\Dev\pwiz
    pwsh -File ./scripts/sync-osprey-ml.ps1 -PwizPath D:\Dev\pwiz -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PwizPath,

    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

$vendorDir = Join-Path $PSScriptRoot '..\third_party\Osprey.ML' | Resolve-Path
$manifestPath = Join-Path $vendorDir 'UPSTREAM.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$ospreyMl = Join-Path $PwizPath 'pwiz_tools\Osprey\Osprey.ML'
if (-not (Test-Path $ospreyMl)) {
    throw "Not a pwiz checkout: $ospreyMl does not exist"
}

function Get-TextHash([string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

# Pull the XorShift64 class out of LinearSvmClassifier.cs by brace matching, so the
# fragment is extracted the same way every time rather than by hand.
function Get-XorShiftFragment([string]$sourcePath) {
    $lines = [System.IO.File]::ReadAllLines($sourcePath)
    $start = -1
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match '^\s*public class XorShift64\b') {
            $start = $i
            break
        }
    }
    if ($start -lt 0) {
        throw "XorShift64 not found in $sourcePath. The fragment guard needs updating."
    }

    # Walk back over the preceding doc comment block.
    $docStart = $start
    while ($docStart -gt 0 -and $lines[$docStart - 1] -match '^\s*///') {
        $docStart--
    }

    $depth = 0
    $end = -1
    for ($i = $start; $i -lt $lines.Length; $i++) {
        $depth += ([regex]::Matches($lines[$i], '\{')).Count
        $depth -= ([regex]::Matches($lines[$i], '\}')).Count
        if ($depth -eq 0 -and $i -gt $start) {
            $end = $i
            break
        }
    }
    if ($end -lt 0) {
        throw "Unbalanced braces walking XorShift64 in $sourcePath"
    }

    return ($lines[$docStart..$end] -join "`r`n")
}

$changed = $false

foreach ($file in $manifest.files) {
    $vendoredPath = Join-Path $vendorDir $file.vendored
    $upstreamPath = Join-Path $PwizPath ($file.upstream -replace '/', '\')

    if (-not (Test-Path $upstreamPath)) {
        Write-Host "MISSING upstream: $($file.upstream)" -ForegroundColor Red
        $changed = $true
        continue
    }

    $currentHash = (Get-FileHash $vendoredPath -Algorithm SHA256).Hash
    if ($currentHash -ne $file.sha256) {
        Write-Host "LOCALLY EDITED: $($file.vendored) no longer matches its recorded hash" -ForegroundColor Red
        Write-Host "  recorded $($file.sha256)"
        Write-Host "  actual   $currentHash"
        $changed = $true
    }

    if ($file.verbatim) {
        $upstreamHash = (Get-FileHash $upstreamPath -Algorithm SHA256).Hash
        if ($upstreamHash -eq $currentHash) {
            Write-Host "up to date: $($file.vendored)" -ForegroundColor Green
            continue
        }

        Write-Host "UPSTREAM MOVED: $($file.vendored)" -ForegroundColor Yellow
        $changed = $true
        if ($Apply) {
            Copy-Item $upstreamPath $vendoredPath -Force
            $file.sha256 = (Get-FileHash $vendoredPath -Algorithm SHA256).Hash
            Write-Host "  updated to $($file.sha256)"
        }
    }
    else {
        # Fragment: compare the extracted class body, ignoring the MARS-authored header.
        $fragment = Get-XorShiftFragment $upstreamPath
        $vendored = [System.IO.File]::ReadAllText($vendoredPath)
        $normalizedFragment = ($fragment -replace "`r`n", "`n")
        $normalizedVendored = ($vendored -replace "`r`n", "`n")
        if ($normalizedVendored.Contains($normalizedFragment)) {
            Write-Host "up to date: $($file.vendored) (fragment matches upstream)" -ForegroundColor Green
        }
        else {
            Write-Host "UPSTREAM MOVED: $($file.vendored) fragment differs from $($file.upstream)" -ForegroundColor Yellow
            Write-Host "  re-extract by hand; the surrounding header is MARS-authored"
            $changed = $true
        }
    }
}

if ($Apply) {
    $manifest.commit = (git -C $PwizPath rev-parse HEAD).Trim()
    $manifest.commitSubject = (git -C $PwizPath log -1 --format=%s).Trim()
    $manifest.branch = (git -C $PwizPath rev-parse --abbrev-ref HEAD).Trim()
    $manifest.syncedOn = (Get-Date -Format 'yyyy-MM-dd')
    $manifest | ConvertTo-Json -Depth 6 | Set-Content $manifestPath -Encoding utf8NoBOM
    Write-Host "`nRewrote $manifestPath" -ForegroundColor Cyan
}

if ($changed -and -not $Apply) {
    Write-Host "`nRe-run with -Apply to pull the upstream changes down." -ForegroundColor Cyan
    exit 1
}

exit 0
