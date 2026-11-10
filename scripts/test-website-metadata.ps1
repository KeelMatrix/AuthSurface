[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot 'check-website-metadata.ps1'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$scratch = [IO.Path]::GetTempPath()
$testRoot = Join-Path $scratch ('authsurface-metadata-' + [guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $testRoot 'keelmatrix.website.json'
$propsPath = Join-Path $testRoot 'Directory.Build.props'
$projectPath = Join-Path $testRoot 'source package/Library.csproj'
$extraProjectPath = Join-Path $testRoot 'Sibling.csproj'
$packagePath = Join-Path $testRoot 'metadata.nupkg'
$symbolsPath = Join-Path $testRoot 'metadata.snupkg'
$validManifest = '{"schemaVersion":1,"packages":{"KeelMatrix.AuthSurface":{"visibility":"public-product","role":"primary"}}}'
$url = 'https://github.com/KeelMatrix/AuthSurface'
$validProps = @'
<Project><PropertyGroup Condition="'$(Configuration)' == 'Release'">
  <PackageId>KeelMatrix.AuthSurface</PackageId>
  <PackageTags>aspnetcore;keelmatrix-public-product;keelmatrix-primary</PackageTags>
  <PackageProjectUrl>__URL__</PackageProjectUrl><RepositoryUrl>__URL__</RepositoryUrl><RepositoryType>git</RepositoryType>
</PropertyGroup></Project>
'@.Replace('__URL__', $url)
$validProject = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>true</IsPackable></PropertyGroup></Project>'
$validNuspec = @"
<package><metadata><id>KeelMatrix.AuthSurface</id><tags>aspnetcore keelmatrix-public-product keelmatrix-primary</tags>
<projectUrl>$url</projectUrl><repository type="git" url="$url" /></metadata></package>
"@
$caseCount = 0
$originalPath = $env:PATH
$originalNative = $env:AUTHSURFACE_METADATA_NATIVE

function Reset-Fixture {
    Set-Content -LiteralPath $manifestPath -Value $validManifest -Encoding utf8
    Set-Content -LiteralPath $propsPath -Value $validProps -Encoding utf8
    Set-Content -LiteralPath $projectPath -Value $validProject -Encoding utf8
    Set-Content -LiteralPath $extraProjectPath -Value '<Project><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>' -Encoding utf8
}

function Assert-Rejected([string] $Name, [scriptblock] $Mutate, [string] $Reason) {
    Reset-Fixture
    & $Mutate
    $rejected = $false
    try { & $checker -Root $testRoot | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*Website metadata:*$Reason*") { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "Website metadata negative control accepted: $Name" }
    $script:caseCount++
}

function Write-Archive([string] $Path, [string] $Nuspec) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('KeelMatrix.AuthSurface.nuspec')
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.Write($Nuspec) } finally { $writer.Dispose() }
    }
    finally { $zip.Dispose() }
}

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $projectPath) -Force | Out-Null
    & git -C $testRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Metadata fixture git initialization failed.' }
    # Force multiple application matches, as on hosts with several dotnet paths.
    $env:AUTHSURFACE_METADATA_NATIVE = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $shimDirectory = Join-Path $testRoot 'dotnet-path'
    New-Item -ItemType Directory -Path $shimDirectory -Force | Out-Null
    if ($IsWindows) {
        Set-Content -LiteralPath (Join-Path $shimDirectory 'dotnet.cmd') -Value '@"%AUTHSURFACE_METADATA_NATIVE%" %*' -Encoding ascii
    }
    else {
        $shimPath = Join-Path $shimDirectory 'dotnet'
        [IO.File]::WriteAllText($shimPath, "#!/bin/sh`n" + 'exec "$AUTHSURFACE_METADATA_NATIVE" "$@"' + "`n")
        & chmod +x $shimPath
        if ($LASTEXITCODE -ne 0) { throw 'Metadata fixture executable setup failed.' }
    }
    $env:PATH = $shimDirectory + [IO.Path]::PathSeparator + $originalPath
    if (@(Get-Command dotnet -CommandType Application).Count -lt 2) { throw 'Metadata fixture did not create multiple dotnet application matches.' }
    Reset-Fixture
    & $checker -Root $testRoot

    Assert-Rejected 'missing manifest' { Remove-Item -LiteralPath $manifestPath } 'missing'
    foreach ($json in @('', '{', 'null', '[]', '[{"schemaVersion":1}]', '{"schemaVersion":1,}',
            '{"schemaVersion":1,"schemaVersion":1,"packages":{}}',
            '{"schemaVersion":1,"packages":{},"extra":true}', '{"packages":{}}')) {
        Assert-Rejected 'malformed manifest' { Set-Content -LiteralPath $manifestPath -Value $json } ''
    }
    foreach ($version in @('0', '2', '-1', '1.0', '"1"', 'null', 'true', '[]', '{}')) {
        Assert-Rejected 'unsupported schema' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('"schemaVersion":1', '"schemaVersion":' + $version)) } 'schemaVersion'
    }
    foreach ($packages in @('null', '[]', '{}', '{"KeelMatrix.AuthSurface":null}',
            '{"KeelMatrix.AuthSurface":{"visibility":"public-product"}}',
            '{"KeelMatrix.AuthSurface":{"visibility":"public-product","role":"primary","role":"primary"}}',
            '{"KeelMatrix.AuthSurface":{"visibility":"public-product","role":"primary","extra":true}}')) {
        Assert-Rejected 'invalid packages' { Set-Content -LiteralPath $manifestPath -Value ('{"schemaVersion":1,"packages":' + $packages + '}') } ''
    }
    foreach ($field in @('visibility', 'role')) {
        $original = if ($field -eq 'visibility') { 'public-product' } else { 'primary' }
        foreach ($value in @('null', 'true', '1', '[]', '{}', '""', '"UNKNOWN"', ('"' + $original.ToUpperInvariant() + '"'))) {
            Assert-Rejected 'invalid classification value' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('"' + $original + '"', $value)) } 'classification'
        }
    }
    Assert-Rejected 'missing exact identity' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('KeelMatrix.AuthSurface', 'KeelMatrix.Foreign')) } 'identity'
    Assert-Rejected 'identity casing' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('KeelMatrix.AuthSurface', 'keelmatrix.authsurface')) } 'identity'
    Assert-Rejected 'stale entry' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('}}}', '},"KeelMatrix.Stale":{"visibility":"public-product","role":"component"}}}')) } 'stale'
    Assert-Rejected 'duplicate identity' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('}}}', '},"KeelMatrix.AuthSurface":{"visibility":"public-product","role":"component"}}}')) } 'duplicate'
    Assert-Rejected 'zero primary' { Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('primary', 'component')) } 'exactly one primary'
    Assert-Rejected 'multiple primary' {
        Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('}}}', '},"KeelMatrix.AuthSurface.Sibling":{"visibility":"public-product","role":"primary"}}}'))
        Set-Content -LiteralPath $extraProjectPath -Value ($validProject.Replace('</Project>', '<PropertyGroup><PackageId>KeelMatrix.AuthSurface.Sibling</PackageId></PropertyGroup></Project>'))
    } 'exactly one primary'
    Assert-Rejected 'mixed visibility duplicate primary' {
        Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('}}}', '},"KeelMatrix.AuthSurface.Sibling":{"visibility":"internal-package","role":"primary"}}}'))
    } 'exactly one primary'

    Reset-Fixture
    Set-Content -LiteralPath $manifestPath -Value ($validManifest.Replace('}}}', '},"KeelMatrix.AuthSurface.Sibling":{"visibility":"public-product","role":"component"}}}'))
    Set-Content -LiteralPath $extraProjectPath -Value ($validProject.Replace('</Project>', '<PropertyGroup><PackageId>KeelMatrix.AuthSurface.Sibling</PackageId><PackageTags>keelmatrix-public-product;keelmatrix-component</PackageTags></PropertyGroup></Project>'))
    & $checker -Root $testRoot

    $badTags = @('', 'aspnetcore', 'keelmatrix-primary', 'keelmatrix-public-product',
        'keelmatrix-public-product;keelmatrix-component', 'keelmatrix-internal-package;keelmatrix-primary',
        'keelmatrix-public-product;keelmatrix-internal-package;keelmatrix-primary',
        'keelmatrix-public-product;keelmatrix-primary;keelmatrix-component',
        'keelmatrix-public-product;keelmatrix-public-product;keelmatrix-primary',
        'keelmatrix-public-product;keelmatrix-primary;keelmatrix-primary',
        'KeelMatrix-public-product;keelmatrix-primary', 'keelmatrix-public-product;KeelMatrix-primary')
    foreach ($tags in $badTags) {
        Assert-Rejected 'source sentinel drift' { Set-Content -LiteralPath $propsPath -Value ($validProps.Replace('aspnetcore;keelmatrix-public-product;keelmatrix-primary', $tags)) } 'sentinel'
    }
    foreach ($field in @('PackageProjectUrl', 'RepositoryUrl')) {
        foreach ($value in @('', 'https://github.com/KeelMatrix/Foreign', 'https://example.invalid/AuthSurface')) {
            Assert-Rejected 'source URL drift' { Set-Content -LiteralPath $propsPath -Value ($validProps.Replace("<$field>$url</$field>", "<$field>$value</$field>")) } 'repository'
        }
    }
    Assert-Rejected 'source identity drift' { Set-Content -LiteralPath $propsPath -Value ($validProps.Replace('KeelMatrix.AuthSurface</PackageId>', 'KeelMatrix.Foreign</PackageId>')) } 'identity'
    Assert-Rejected 'source repository type' { Set-Content -LiteralPath $propsPath -Value ($validProps.Replace('<RepositoryType>git</RepositoryType>', '<RepositoryType>svn</RepositoryType>')) } 'repository'
    Assert-Rejected 'nonpackable entry' { Set-Content -LiteralPath $projectPath -Value ($validProject.Replace('true', 'false')) } 'stale'
    Assert-Rejected 'duplicate source identity' { Set-Content -LiteralPath $extraProjectPath -Value $validProject } 'duplicate source'
    Assert-Rejected 'source evaluation failure' { Set-Content -LiteralPath $projectPath -Value '<Project>' } 'evaluation failed'

    Reset-Fixture
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    Write-Archive $packagePath $validNuspec
    Write-Archive $symbolsPath $validNuspec
    & $checker -Root $testRoot -PackagePath @($packagePath, $symbolsPath)
    foreach ($path in @($packagePath, $symbolsPath)) {
        foreach ($tags in $badTags) {
            Write-Archive $path ($validNuspec.Replace('aspnetcore keelmatrix-public-product keelmatrix-primary', $tags))
            $rejected = $false
            try { & $checker -Root $testRoot -PackagePath @($packagePath, $symbolsPath) | Out-Null }
            catch { if ($_.Exception.Message -notlike '*Website metadata:*sentinel*') { throw }; $rejected = $true }
            if (-not $rejected) { throw 'Archive sentinel drift was accepted.' }
            $caseCount++
        }
        foreach ($pair in @(@('<id>KeelMatrix.AuthSurface</id>', '<id>KeelMatrix.Foreign</id>'),
                @("<projectUrl>$url</projectUrl>", '<projectUrl>https://github.com/KeelMatrix/Foreign</projectUrl>'),
                @(('url="' + $url + '"'), 'url="https://example.invalid/AuthSurface"'),
                @("<projectUrl>$url</projectUrl>", ''),
                @(('url="' + $url + '"'), 'url=""'),
                @('type="git"', 'type="svn"'))) {
            Write-Archive $path ($validNuspec.Replace($pair[0], $pair[1]))
            $rejected = $false
            try { & $checker -Root $testRoot -PackagePath @($packagePath, $symbolsPath) | Out-Null }
            catch { if ($_.Exception.Message -notlike '*Website metadata:*') { throw }; $rejected = $true }
            if (-not $rejected) { throw 'Archive identity/URL drift was accepted.' }
            $caseCount++
        }
        Write-Archive $path $validNuspec
    }

    # Run real callers in child processes. A reached restore or a returning failure is an error.
    $scripts = Join-Path $testRoot 'scripts'
    $build = Join-Path $testRoot 'build'
    New-Item -ItemType Directory -Path $scripts, $build -Force | Out-Null
    foreach ($name in @('check-website-metadata.ps1', 'inspect-package.ps1', 'validate.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scripts
    }
    Set-Content -LiteralPath (Join-Path $build 'Test-NestedPwshLaunch.ps1') -Value '$global:LASTEXITCODE = 0'
    $childPath = Join-Path $testRoot 'child.ps1'
    foreach ($caller in @('check-website-metadata.ps1', 'inspect-package.ps1', 'validate.ps1')) {
        Reset-Fixture
        if ($caller -eq 'inspect-package.ps1') {
            Write-Archive $symbolsPath ($validNuspec.Replace('keelmatrix-primary', 'keelmatrix-component'))
            $invoke = '& (Join-Path $PSScriptRoot ''scripts/inspect-package.ps1'') -PackagePath (Join-Path $PSScriptRoot ''metadata.nupkg'') -SymbolPackagePath (Join-Path $PSScriptRoot ''metadata.snupkg'')'
        }
        else {
            Set-Content -LiteralPath $propsPath -Value ($validProps.Replace('keelmatrix-primary', 'keelmatrix-component'))
            $invoke = '& (Join-Path $PSScriptRoot ''scripts/' + $caller + ''') -Root $PSScriptRoot'
            if ($caller -eq 'validate.ps1') { $invoke = $invoke.Replace(' -Root $PSScriptRoot', '') }
        }
        $child = '$ErrorActionPreference = ''Stop''' + "`n" + 'function dotnet { if ($args[0] -eq ''restore'') { exit 86 }; & $env:AUTHSURFACE_TEST_DOTNET @args; $global:LASTEXITCODE = $LASTEXITCODE }' + "`n" + $invoke + "`nexit 0"
        Set-Content -LiteralPath $childPath -Value $child
        $oldDotnet = $env:AUTHSURFACE_TEST_DOTNET
        try {
            $env:AUTHSURFACE_TEST_DOTNET = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
            $output = Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $childPath) 2>&1
            $exitCode = $LASTEXITCODE
        }
        finally { $env:AUTHSURFACE_TEST_DOTNET = $oldDotnet }
        if ($exitCode -ne 1 -or ($output -join "`n") -notlike '*Website metadata:*sentinel*') {
            throw "Website metadata failure did not propagate through $caller (exit $exitCode): $output"
        }
        $caseCount++
    }
    Write-Output "Website metadata regression tests passed: $caseCount negative cases; evaluated inherited Release properties, nonpackable exclusion, valid source and both archive types, and fatal caller propagation."
}
finally {
    $env:PATH = $originalPath
    $env:AUTHSURFACE_METADATA_NATIVE = $originalNative
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith([IO.Path]::GetFullPath($scratch).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Metadata fixture cleanup is outside the temporary directory.'
    }
    Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
