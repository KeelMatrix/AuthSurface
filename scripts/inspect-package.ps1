[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package does not exist: $PackagePath"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    Write-Output 'Package entries:'
    $entries | ForEach-Object { Write-Output "  $_" }

    if ($entries -contains 'README.md' -eq $false -or $entries -contains 'LICENSE' -eq $false -or
        $entries -contains 'SECURITY.md' -eq $false -or $entries -contains 'PRIVACY.md' -eq $false) {
        throw 'Package is missing required documentation entries.'
    }

    if ($entries | Where-Object { $_ -match '(^|/)(tests|fixtures|sample|samples|obj|bin)(/|$)|\.env' }) {
        throw 'Package contains test, fixture, sample, local, or environment files.'
    }

    $nuspecEntry = $archive.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
    if ($null -eq $nuspecEntry) {
        throw 'Package has no nuspec.'
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $nuspec.package.metadata
    Write-Output "Nuspec id=$($metadata.id) version=$($metadata.version) tfm=net8.0"
    Write-Output "Nuspec description=$($metadata.description)"
    Write-Output "Nuspec license=$($metadata.license.expression) repository=$($metadata.repository.url)"

    if ($metadata.id -ne 'KeelMatrix.AuthSurface' -or $metadata.version -ne '0.1.0') {
        throw 'Package ID or version is incorrect.'
    }

    $dependencyNames = @($metadata.dependencies.group.dependency | ForEach-Object name | Sort-Object -Unique)
    Write-Output "Dependencies=$($dependencyNames -join ',')"
    if ($dependencyNames.Count -ne 1 -or $dependencyNames[0] -ne 'KeelMatrix.Telemetry') {
        throw 'Package dependency set is not exactly KeelMatrix.Telemetry.'
    }

    $frameworkReferences = @($metadata.frameworkReferences.group.frameworkReference | ForEach-Object name)
    Write-Output "FrameworkReferences=$($frameworkReferences -join ',')"
    if ($frameworkReferences -notcontains 'Microsoft.AspNetCore.App') {
        throw 'Microsoft.AspNetCore.App framework reference is missing from nuspec metadata.'
    }

    if ($entries -notcontains 'lib/net8.0/KeelMatrix.AuthSurface.dll' -or
        $entries -notcontains 'lib/net8.0/KeelMatrix.AuthSurface.xml') {
        throw 'net8.0 library or XML documentation entry is missing.'
    }

    if ($entries -contains 'icon.png') {
        Write-Output 'Icon=packed icon.png'
    }
    else {
        Write-Output 'Icon=not present; founder placement remains required before a public release.'
    }
}
finally {
    $archive.Dispose()
}
