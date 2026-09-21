[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $SymbolPackagePath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$projectReadmePath = Join-Path $root 'src/KeelMatrix.AuthSurface/README.md'

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package does not exist: $PackagePath"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    Write-Output 'Package entries:'
    $entries | ForEach-Object { Write-Output "  $_" }

    if (-not ($entries -contains 'README.md') -or -not ($entries -contains 'LICENSE') -or
        -not ($entries -contains 'SECURITY.md') -or -not ($entries -contains 'PRIVACY.md')) {
        throw 'Package is missing required documentation entries.'
    }

    if (-not (Test-Path -LiteralPath $projectReadmePath)) {
        throw "Project README does not exist: $projectReadmePath"
    }

    $readmeEntry = $archive.Entries | Where-Object FullName -eq 'README.md' | Select-Object -First 1
    $readmeHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        $readmeStream = $readmeEntry.Open()
        try { $packedReadmeHash = [System.BitConverter]::ToString($readmeHashAlgorithm.ComputeHash($readmeStream)).Replace('-', '').ToLowerInvariant() }
        finally { $readmeStream.Dispose() }
    }
    finally { $readmeHashAlgorithm.Dispose() }
    $projectReadmeHash = (Get-FileHash -LiteralPath $projectReadmePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "README_SHA256 packed=$packedReadmeHash project=$projectReadmeHash"
    if ($packedReadmeHash -ne $projectReadmeHash) {
        throw 'Packed README is not byte-identical to the project-local README.'
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
    Write-Output "Nuspec license=$($metadata.license.'#text') repository=$($metadata.repository.url)"

    if ($metadata.id -ne 'KeelMatrix.AuthSurface' -or $metadata.version -ne '0.1.0') {
        throw 'Package ID or version is incorrect.'
    }

    $dependencyNames = @($metadata.dependencies.group.dependency | ForEach-Object { $_.id } | Sort-Object -Unique)
    Write-Output "Dependencies=$($dependencyNames -join ',')"
    if ($dependencyNames.Count -ne 1 -or $dependencyNames[0] -ne 'KeelMatrix.Telemetry') {
        throw 'Package dependency set is not exactly KeelMatrix.Telemetry.'
    }

    $frameworkReferences = @($metadata.frameworkReferences.group.frameworkReference | ForEach-Object { $_.name })
    Write-Output "FrameworkReferences=$($frameworkReferences -join ',')"
    if ($frameworkReferences -notcontains 'Microsoft.AspNetCore.App') {
        throw 'Microsoft.AspNetCore.App framework reference is missing from nuspec metadata.'
    }

    if ($entries -notcontains 'lib/net8.0/KeelMatrix.AuthSurface.dll' -or
        $entries -notcontains 'lib/net8.0/KeelMatrix.AuthSurface.xml') {
        throw 'net8.0 library or XML documentation entry is missing.'
    }

    if ($entries -contains 'icon.png') {
        if ($metadata.icon -ne 'icon.png') {
            throw 'Package icon metadata is not icon.png.'
        }

        $rootIconPath = Join-Path $root 'icon.png'
        $packedIcon = $archive.Entries | Where-Object FullName -eq 'icon.png' | Select-Object -First 1
        $rootIconHash = (Get-FileHash -LiteralPath $rootIconPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $iconHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            $iconStream = $packedIcon.Open()
            try { $packedIconHash = [System.BitConverter]::ToString($iconHashAlgorithm.ComputeHash($iconStream)).Replace('-', '').ToLowerInvariant() }
            finally { $iconStream.Dispose() }
        }
        finally { $iconHashAlgorithm.Dispose() }
        Write-Output "Icon=packed icon.png SHA256=$packedIconHash root=$rootIconHash"
        if ($packedIconHash -ne $rootIconHash) {
            throw 'Packed icon is not byte-identical to the repository-root icon.'
        }
    }
    else {
        Write-Output 'Icon=not present; founder placement remains required before a public release.'
    }
}
finally {
    $archive.Dispose()
}

if ($SymbolPackagePath) {
    if (-not (Test-Path -LiteralPath $SymbolPackagePath)) {
        throw "Symbol package does not exist: $SymbolPackagePath"
    }

    $symbols = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $SymbolPackagePath))
    try {
        Write-Output 'Symbol package entries:'
        @($symbols.Entries | ForEach-Object FullName | Sort-Object) | ForEach-Object { Write-Output "  $_" }
        if (-not ($symbols.Entries.FullName | Where-Object { $_ -like '*.pdb' })) {
            throw 'Symbol package contains no PDB.'
        }
    }
    finally {
        $symbols.Dispose()
    }
}
