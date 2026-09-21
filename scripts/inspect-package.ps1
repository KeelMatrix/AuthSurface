[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [string] $SymbolPackagePath,

    [string] $ExpectedVersion = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$projectReadmePath = Join-Path $root 'src/KeelMatrix.AuthSurface/README.md'

function Get-PngDimensions([System.IO.Stream] $Stream) {
    $header = New-Object byte[] 24
    if ($Stream.Read($header, 0, $header.Length) -ne $header.Length -or
        $header[0] -ne 137 -or $header[1] -ne 80 -or $header[2] -ne 78 -or $header[3] -ne 71) {
        throw 'Package icon is not a valid PNG.'
    }

    $width = ([int]$header[16] -shl 24) -bor ([int]$header[17] -shl 16) -bor ([int]$header[18] -shl 8) -bor [int]$header[19]
    $height = ([int]$header[20] -shl 24) -bor ([int]$header[21] -shl 16) -bor ([int]$header[22] -shl 8) -bor [int]$header[23]
    return @{ Width = $width; Height = $height }
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Package does not exist: $PackagePath"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    $entries = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    Write-Output 'Package entries:'
    $entries | ForEach-Object { Write-Output "  $_" }

    $expectedEntries = @(
        '_rels/.rels',
        '[Content_Types].xml',
        'icon.png',
        'KeelMatrix.AuthSurface.nuspec',
        'lib/net8.0/KeelMatrix.AuthSurface.dll',
        'lib/net8.0/KeelMatrix.AuthSurface.xml',
        'LICENSE',
        'PRIVACY.md',
        'README.md',
        'SECURITY.md'
    )
    $corePropertyEntries = @($entries | Where-Object { $_ -match '^package/services/metadata/core-properties/[0-9a-f-]+\.psmdcp$' })
    $missingEntries = @($expectedEntries | Where-Object { $entries -notcontains $_ })
    $unexpectedEntries = @($entries | Where-Object { $_ -notin $expectedEntries -and $_ -notin $corePropertyEntries })
    if ($corePropertyEntries.Count -ne 1 -or $missingEntries.Count -gt 0 -or $unexpectedEntries.Count -gt 0) {
        throw "Unexpected package entry set. Missing: $($missingEntries -join ', '); unexpected: $($unexpectedEntries -join ', '); core-properties: $($corePropertyEntries.Count)"
    }

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

    if ($metadata.id -ne 'KeelMatrix.AuthSurface' -or $metadata.version -ne $ExpectedVersion -or
        $metadata.authors -ne 'KeelMatrix' -or $metadata.license.type -ne 'expression' -or
        $metadata.license.'#text' -ne 'MIT' -or $metadata.repository.url -ne 'https://github.com/KeelMatrix/AuthSurface' -or
        $metadata.readme -ne 'README.md') {
        throw 'Package ID or version is incorrect.'
    }

    $dependencyNames = @($metadata.dependencies.group.dependency | ForEach-Object { $_.id } | Sort-Object -Unique)
    Write-Output "Dependencies=$($dependencyNames -join ',')"
    $telemetryDependency = @($metadata.dependencies.group.dependency | Where-Object id -eq 'KeelMatrix.Telemetry' | Select-Object -First 1)
    if ($dependencyNames.Count -ne 1 -or $dependencyNames[0] -ne 'KeelMatrix.Telemetry' -or
        $null -eq $telemetryDependency -or $telemetryDependency.version -ne '[0.1.0]') {
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

    if ($entries -notcontains 'icon.png' -or $metadata.icon -ne 'icon.png') {
        throw 'Package icon.png or icon metadata is missing.'
    }

    $rootIconPath = Join-Path $root 'icon.png'
    $packedIcon = $archive.Entries | Where-Object FullName -eq 'icon.png' | Select-Object -First 1
    if ((Get-Item -LiteralPath $rootIconPath).Length -gt 204800) {
        throw 'Repository icon exceeds the 200 KB package limit.'
    }
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
    $iconStream = $packedIcon.Open()
    try { $dimensions = Get-PngDimensions $iconStream }
    finally { $iconStream.Dispose() }
    Write-Output "IconDimensions=$($dimensions.Width)x$($dimensions.Height)"
    if ($dimensions.Width -ne 512 -or $dimensions.Height -ne 512) {
        throw 'Package icon dimensions must be exactly 512x512.'
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
        $symbolEntries = @($symbols.Entries | ForEach-Object FullName | Sort-Object)
        $expectedSymbolEntries = @('_rels/.rels', '[Content_Types].xml', 'KeelMatrix.AuthSurface.nuspec', 'lib/net8.0/KeelMatrix.AuthSurface.pdb')
        $symbolCorePropertyEntries = @($symbolEntries | Where-Object { $_ -match '^package/services/metadata/core-properties/[0-9a-f-]+\.psmdcp$' })
        $missingSymbolEntries = @($expectedSymbolEntries | Where-Object { $symbolEntries -notcontains $_ })
        $unexpectedSymbolEntries = @($symbolEntries | Where-Object { $_ -notin $expectedSymbolEntries -and $_ -notin $symbolCorePropertyEntries })
        if ($symbolCorePropertyEntries.Count -ne 1 -or $missingSymbolEntries.Count -gt 0 -or $unexpectedSymbolEntries.Count -gt 0) {
            throw "Unexpected symbol package entry set. Missing: $($missingSymbolEntries -join ', '); unexpected: $($unexpectedSymbolEntries -join ', '); core-properties: $($symbolCorePropertyEntries.Count)"
        }
        if (-not ($symbols.Entries.FullName | Where-Object { $_ -like '*.pdb' })) {
            throw 'Symbol package contains no PDB.'
        }
    }
    finally {
        $symbols.Dispose()
    }
}
