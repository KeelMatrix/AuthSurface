[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string[]] $PackagePath = @()
)

$ErrorActionPreference = 'Stop'
$repositoryUrl = 'https://github.com/KeelMatrix/AuthSurface'

function Assert-ObjectFields($Element, [string[]] $Names, [string] $Location) {
    if ($Element.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
        throw "Website metadata: $Location must be an object."
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $seen.Add($property.Name) -or $property.Name -cnotin $Names) {
            throw "Website metadata: duplicate or unknown field in $Location."
        }
    }
    if ($seen.Count -ne $Names.Count) { throw "Website metadata: missing field in $Location." }
}

function Assert-PackageMetadata([string] $Id, [string] $Tags, [string] $ProjectUrl, [string] $Repository, [string] $RepositoryType) {
    if (-not $classifications.ContainsKey($Id)) {
        throw 'Website metadata: package identity has no exact manifest entry.'
    }
    $classification = $classifications[$Id]
    $sentinels = @($Tags -split '[;\s]+' | Where-Object { $_ -match '^keelmatrix-(public-product|internal-package|primary|component)$' })
    if ($sentinels.Count -ne 2 -or
        @($sentinels | Where-Object { $_ -ceq "keelmatrix-$($classification.Visibility)" }).Count -ne 1 -or
        @($sentinels | Where-Object { $_ -ceq "keelmatrix-$($classification.Role)" }).Count -ne 1) {
        throw 'Website metadata: exactly one matching lowercase visibility sentinel and role sentinel are required.'
    }
    if ($ProjectUrl -cne $repositoryUrl -or $Repository -cne $repositoryUrl -or $RepositoryType -cne 'git') {
        throw 'Website metadata: project/repository URL or repository type disagrees with the source repository.'
    }
}

$manifestPath = Join-Path $Root 'keelmatrix.website.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Website metadata: root manifest is missing.' }
try {
    $document = [System.Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($manifestPath))
}
catch { throw 'Website metadata: manifest is not strict JSON.' }
$classifications = [System.Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
try {
    $manifest = $document.RootElement
    Assert-ObjectFields $manifest @('schemaVersion', 'packages') 'manifest'
    $version = $manifest.GetProperty('schemaVersion')
    $schemaVersion = 0
    if ($version.ValueKind -ne [System.Text.Json.JsonValueKind]::Number -or
        -not $version.TryGetInt32([ref]$schemaVersion) -or $schemaVersion -ne 1) {
        throw 'Website metadata: unsupported schemaVersion; expected integer 1.'
    }
    $packages = $manifest.GetProperty('packages')
    if ($packages.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Website metadata: packages must be an object.' }
    foreach ($package in $packages.EnumerateObject()) {
        Assert-ObjectFields $package.Value @('visibility', 'role') 'package classification'
        $visibility = $package.Value.GetProperty('visibility')
        $role = $package.Value.GetProperty('role')
        if ($visibility.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $role.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
            $visibility.GetString() -cnotin @('public-product', 'internal-package') -or
            $role.GetString() -cnotin @('primary', 'component') -or $classifications.ContainsKey($package.Name)) {
            throw 'Website metadata: invalid or duplicate package classification.'
        }
        $classifications.Add($package.Name, @{ Visibility = $visibility.GetString(); Role = $role.GetString() })
    }
}
finally { $document.Dispose() }
if ($classifications.Count -eq 0) { throw 'Website metadata: package manifest is empty.' }
$publicPackages = @($classifications.Values | Where-Object Visibility -CEQ 'public-product')
if (@($classifications.Values | Where-Object Role -CEQ 'primary').Count -gt 1 -or
    ($publicPackages.Count -gt 0 -and @($publicPackages | Where-Object Role -CEQ 'primary').Count -ne 1)) {
    throw 'Website metadata: the public product family must have exactly one primary package.'
}

$projectPaths = @(& git -C $Root ls-files --cached --others --exclude-standard -- '*.csproj')
if ($LASTEXITCODE -ne 0 -or $projectPaths.Count -eq 0) { throw 'Website metadata: cannot discover repository projects.' }
$sourceIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($projectPath in $projectPaths) {
    $output = & dotnet msbuild (Join-Path $Root $projectPath) -nologo '-property:Configuration=Release' '-getProperty:IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType'
    if ($LASTEXITCODE -ne 0) { throw 'Website metadata: project metadata evaluation failed.' }
    $properties = ($output -join "`n" | ConvertFrom-Json -AsHashtable).Properties
    if ($properties.IsPackable -notin @('true', 'false')) { throw 'Website metadata: project packability is unknown.' }
    if ($properties.IsPackable -eq 'false') { continue }
    Assert-PackageMetadata $properties.PackageId $properties.PackageTags $properties.PackageProjectUrl $properties.RepositoryUrl $properties.RepositoryType
    if (-not $sourceIds.Add($properties.PackageId)) { throw 'Website metadata: duplicate source package identity.' }
}
if ($sourceIds.Count -ne $classifications.Count) { throw 'Website metadata: manifest contains a stale or foreign package identity.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($path in $PackagePath) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $path))
    try {
        $nuspecEntries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecEntries.Count -ne 1) { throw 'Website metadata: archive must contain exactly one nuspec.' }
        $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $nuspec.package.metadata
        Assert-PackageMetadata $metadata.id $metadata.tags $metadata.projectUrl $metadata.repository.url $metadata.repository.type
        if (-not $sourceIds.Contains([string]$metadata.id)) { throw 'Website metadata: archive identity is not a source package.' }
    }
    finally { $archive.Dispose() }
}
$global:LASTEXITCODE = 0
Write-Output "Website metadata passed: $($sourceIds.Count) source package(s), $($PackagePath.Count) archive(s)."
