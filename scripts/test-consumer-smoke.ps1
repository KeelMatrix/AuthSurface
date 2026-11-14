[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$staleRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-stale-cache-' + [guid]::NewGuid().ToString('N'))
$packageVersion = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$stalePackageDirectory = Join-Path $staleRoot "keelmatrix.authsurface/$packageVersion"
$oldCache = $env:NUGET_PACKAGES
$oldHttpCache = $env:NUGET_HTTP_CACHE_PATH
$oldFallback = $env:NUGET_FALLBACK_PACKAGES
$oldLocation = Get-Location
$repositoryRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repositoryRoot 'build/Read-CandidateInstall.ps1')

try {
    New-Item -ItemType Directory -Path $stalePackageDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $stalePackageDirectory "keelmatrix.authsurface.$packageVersion.nupkg") -Value 'stale same-version package bytes' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $stalePackageDirectory "keelmatrix.authsurface.$packageVersion.nupkg.sha512") -Value 'c3RhbGU=' -Encoding ascii
    $env:NUGET_PACKAGES = $staleRoot

    & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $PackagePath
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Stale-cache consumer smoke failed with exit code $exitCode."
    }
    Write-Output 'Stale same-version cache regression passed: the supplied artifact was selected from fresh isolated caches.'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $staleRoot 'http'
    $env:NUGET_FALLBACK_PACKAGES = $null
    # Exercise each real documentation sequence with cold negative controls.
    $zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('README.md').Open())
        try { $packedText = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
    $texts = @((Get-Content -LiteralPath (Join-Path $repositoryRoot 'README.md') -Raw), $packedText)
    $count = 0
    foreach ($text in $texts) {
        foreach ($mode in @('local-only','absent','lower-version','higher-version')) {
            $count++
            $directory = Join-Path $staleRoot "negative-$count"
            $feed = Join-Path $directory "empty feed & 'quoted'"
            New-Item -ItemType Directory -Path $directory, $feed -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination $directory
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>' | Set-Content -LiteralPath (Join-Path $directory 'Consumer.csproj')
            if ($mode -ne 'absent') {
                $otherVersion = if ($mode -eq 'higher-version') { '0.1.2' } else { '0.1.0' }
                $testPackage = Join-Path $feed $(if ($mode -in @('lower-version','higher-version')) { "KeelMatrix.AuthSurface.$otherVersion.nupkg" } else { "KeelMatrix.AuthSurface.$packageVersion.nupkg" })
                Copy-Item -LiteralPath $PackagePath -Destination $testPackage
                if ($mode -in @('lower-version','higher-version')) {
                    $archive = [IO.Compression.ZipFile]::Open($testPackage, [IO.Compression.ZipArchiveMode]::Update)
                    try {
                        $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')[0]
                        $name = $entry.FullName
                        $reader = [IO.StreamReader]::new($entry.Open())
                        try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
                        $manifest.package.metadata.version = $otherVersion
                        $entry.Delete()
                        $writer = [IO.StreamWriter]::new($archive.CreateEntry($name).Open())
                        try { $writer.Write($manifest.OuterXml) } finally { $writer.Dispose() }
                    }
                    finally { $archive.Dispose() }
                }
            }
            $install = Read-CandidateInstall $text $packageVersion
            $install.Config.configuration.packageSources.add[0].SetAttribute('value', $feed)
            if ($mode -eq 'local-only') {
                $node = $install.Config.SelectSingleNode('/configuration/packageSources/add[@key="nuget.org"]')
                [void]$node.ParentNode.RemoveChild($node)
            }
            $install.Config.Save((Join-Path $directory 'NuGet.config'))
            $env:NUGET_PACKAGES = Join-Path $directory 'cold-packages'
            Push-Location $directory
            try {
                & ([scriptblock]::Create($install.Commands[0])) | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Literal add failed before restore control.' }
                $output = & ([scriptblock]::Create($install.Commands[1])) 2>&1
                $expected = if ($mode -eq 'local-only') { 'KeelMatrix.Telemetry' } else { 'KeelMatrix.AuthSurface' }
                if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notlike "*$expected*") { throw "Cold $mode did not reject the expected missing package: $output" }
                Write-Host "Literal README negative $count/$mode rejected $expected."
            }
            finally { Pop-Location }
        }
    }
    # Missing supplied archive must reject even after successful warm executions.
    $caught = $false
    try { & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath (Join-Path $staleRoot 'missing.nupkg') }
    catch { $caught = $_ -like '*Package does not exist*' }
    if (-not $caught) { throw 'Missing supplied archive was accepted after warm success.' }
    foreach ($field in @('id','lower-version','higher-version','framework','dependency')) {
        $mutant = Join-Path $staleRoot "wrong-$field.nupkg"
        Copy-Item -LiteralPath $PackagePath -Destination $mutant
        $archive = [IO.Compression.ZipFile]::Open($mutant, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = @($archive.Entries | Where-Object FullName -Like '*.nuspec')[0]
            $entryName = $entry.FullName
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
            switch ($field) {
                'id' { $manifest.package.metadata.id = 'Another.Package' }
                'lower-version' { $manifest.package.metadata.version = '0.1.0' }
                'higher-version' { $manifest.package.metadata.version = '0.1.2' }
                'framework' { $manifest.package.metadata.dependencies.group.targetFramework = 'net9.0' }
                'dependency' { $manifest.package.metadata.dependencies.group.dependency.version = '[0.1.0]' }
            }
            $entry.Delete()
            $writer = [IO.StreamWriter]::new($archive.CreateEntry($entryName).Open())
            try { $writer.Write($manifest.OuterXml) } finally { $writer.Dispose() }
        }
        finally { $archive.Dispose() }
        $caught = $false
        try { & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $mutant }
        catch { $caught = $_ -like '*ID/version/framework/dependency contract mismatch*' }
        if (-not $caught) { throw "Wrong supplied $field was accepted or failed at the wrong boundary." }
        Write-Host "Wrong supplied $field rejected before feed creation."
    }
}
finally {
    if ($null -eq $oldCache) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $oldCache
    }
    if ($null -eq $oldHttpCache) { Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue } else { $env:NUGET_HTTP_CACHE_PATH = $oldHttpCache }
    if ($null -eq $oldFallback) { Remove-Item Env:NUGET_FALLBACK_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_FALLBACK_PACKAGES = $oldFallback }
    Set-Location -LiteralPath $oldLocation.Path
    $resolved = [IO.Path]::GetFullPath($staleRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('/','\') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Consumer fixture cleanup escaped scratch.' }
    Remove-Item -LiteralPath $staleRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
