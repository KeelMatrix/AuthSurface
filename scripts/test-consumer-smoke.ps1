[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$staleRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-stale-cache-' + [guid]::NewGuid().ToString('N'))
$stalePackageDirectory = Join-Path $staleRoot 'keelmatrix.authsurface\0.1.0'
$oldCache = $env:NUGET_PACKAGES

try {
    New-Item -ItemType Directory -Path $stalePackageDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $stalePackageDirectory 'keelmatrix.authsurface.0.1.0.nupkg') -Value 'stale same-version package bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $stalePackageDirectory 'keelmatrix.authsurface.0.1.0.nupkg.sha512') -Value 'c3RhbGU=' -Encoding ascii
    $env:NUGET_PACKAGES = $staleRoot

    & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $PackagePath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Stale-cache consumer smoke failed with exit code $exitCode."
    }
    Write-Output 'Stale same-version cache regression passed: the supplied artifact was selected from fresh isolated caches.'
}
finally {
    if ($null -eq $oldCache) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $oldCache
    }
    Remove-Item -LiteralPath $staleRoot -Recurse -Force -ErrorAction SilentlyContinue
}
