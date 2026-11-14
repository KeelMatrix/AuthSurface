# Resolve existing directory aliases once, including macOS /var -> /private/var.
function Get-PublicPackagePath([string] $Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $current = [IO.Path]::GetPathRoot($absolute)
    $suffix = $absolute.Substring($current.Length)
    foreach ($part in $suffix.Split([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        $directory = [IO.DirectoryInfo]::new($current)
        if ($directory.Exists -and $null -ne $directory.LinkTarget) { $current = $directory.ResolveLinkTarget($true).FullName }
    }
    return [IO.Path]::GetFullPath($current)
}

# Call immediately after a successful exact-pair inspection, before later gates.
function Set-PublicPackageInspection($Session) {
    $Session.InspectedArchives = @($Session.ExpectedArchives | ForEach-Object {
        [pscustomobject]@{ Name = $_; SHA256 = (Get-FileHash -LiteralPath (Join-Path $Session.Output $_) -Algorithm SHA256).Hash }
    })
}

# Non-shipping preparation scope. The caller owns its build/test/pack/inspection gates.
function Invoke-PublicPackageSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Solution,
        [Parameter(Mandatory)][string] $OutputDirectory,
        [Parameter(Mandatory)][string] $ExpectedCommit,
        [Parameter(Mandatory)][string] $Version,
        [ValidateSet('Debug','Release')][string] $Configuration = 'Release',
        [Parameter(Mandatory)][scriptblock] $Action
    )
    $ErrorActionPreference = 'Stop'
    $Root = Get-PublicPackagePath $Root
    $Solution = Get-PublicPackagePath $Solution
    $OutputDirectory = Get-PublicPackagePath $OutputDirectory
    $temporaryBase = if ($env:AUTHSURFACE_TEST_SCRATCH) { $env:AUTHSURFACE_TEST_SCRATCH } else { [IO.Path]::GetTempPath() }
    $temporaryBase = Get-PublicPackagePath $temporaryBase
    $sessionRoot = Join-Path $temporaryBase ('authsurface-public-' + [guid]::NewGuid().ToString('N'))
    $sessionRoot = [IO.Path]::GetFullPath($sessionRoot)
    $session = [pscustomobject]@{
        Root = $sessionRoot
        RepositoryRoot = $Root
        Solution = $Solution
        Packages = Join-Path $sessionRoot 'packages'
        Output = Join-Path $sessionRoot 'output'
        References = Join-Path $sessionRoot 'references'
        Properties = @()
        ExpectedArchives = @("KeelMatrix.AuthSurface.$Version.nupkg", "KeelMatrix.AuthSurface.$Version.snupkg")
        InspectedArchives = @()
    }
    $session.Properties = @(
        "-p:RestorePackagesPath=$($session.Packages)",
        "-p:Version=$Version",
        "-p:PackageVersion=$Version",
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $Root 'build/Record-CompilerInputs.targets')",
        "-p:PublicPackageSessionReferences=$($session.References)"
    )
    $previous = @{}
    foreach ($name in @('NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_SCRATCH', 'NUGET_FALLBACK_PACKAGES', 'KEELMATRIX_NO_TELEMETRY', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DO_NOT_TRACK')) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    $location = Get-Location
    $failure = $null
    try {
        New-Item -ItemType Directory -Path $session.Packages, $session.Output, $session.References -Force | Out-Null
        $env:NUGET_PACKAGES = $session.Packages
        $env:NUGET_HTTP_CACHE_PATH = Join-Path $sessionRoot 'http'
        $env:NUGET_SCRATCH = Join-Path $sessionRoot 'scratch'
        $env:NUGET_FALLBACK_PACKAGES = $null
        $env:KEELMATRIX_NO_TELEMETRY = '1'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DO_NOT_TRACK = '1'
        Set-Location -LiteralPath $Root
        $sdk = (& dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine session SDK.' }
        $nuget = (& dotnet nuget --version) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Cannot determine session NuGet version.' }
        # Use the selected SDK's NuGet implementation for signed-package content hashes.
        $sdkLine = @(& dotnet --list-sdks | Where-Object { $_ -match "^$([regex]::Escape($sdk)) \[" })
        if ($LASTEXITCODE -ne 0 -or $sdkLine.Count -ne 1) { throw 'Cannot resolve selected SDK directory.' }
        $sdkDirectory = Join-Path ([regex]::Match($sdkLine[0], '\[(.+)\]').Groups[1].Value) $sdk
        foreach ($assembly in @('NuGet.Common', 'NuGet.Versioning', 'NuGet.Frameworks', 'NuGet.Packaging')) {
            [void][Reflection.Assembly]::LoadFrom((Join-Path $sdkDirectory "$assembly.dll"))
        }
        $config = Join-Path $Root 'NuGet.config'
        $projects = @([regex]::Matches((Get-Content -LiteralPath $Solution -Raw), '"([^"\r\n]+\.csproj)"') | ForEach-Object {
            [IO.Path]::GetFullPath((Join-Path $Root ($_.Groups[1].Value.Replace('\', '/'))))
        } | Sort-Object -Unique)
        if ($projects.Count -eq 0) { throw 'No solution projects found.' }
        $locks = @($projects | ForEach-Object { Join-Path (Split-Path $_) 'packages.lock.json' })
        foreach ($lock in $locks) {
            if (-not (Test-Path -LiteralPath $lock -PathType Leaf)) { throw "Missing committed dependency lock: $lock" }
        }
        function Get-InputHashes {
            @(@($config, (Join-Path $Root 'global.json'), (Join-Path $Root 'Directory.Build.props'), (Join-Path $Root 'Directory.Packages.props')) + $locks | ForEach-Object {
                [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($Root, $_).Replace('\', '/'); SHA256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
            })
        }
        $inputHashes = Get-InputHashes
        Write-Host '=== private public locked restore ==='
        $restoreArguments = @('restore', $Solution, '--configfile', $config, '--packages', $session.Packages, '--locked-mode', '--force',
            '-p:RestoreNoHttpCache=true', '-p:RestoreFallbackFolders=', '-p:RestoreAdditionalProjectFallbackFolders=',
            '-p:RestoreSources=https://api.nuget.org/v3/index.json', '-p:RestoreAdditionalProjectSources=', '-p:RestoreIgnoreFailedSources=false')
        dotnet @restoreArguments
        if ($LASTEXITCODE -ne 0) { throw "Public locked restore failed with exit code $LASTEXITCODE." }

        function Get-RestoredInputs {
            $result = [Collections.Generic.List[object]]::new()
            foreach ($project in $projects) {
                $assetsPath = Join-Path (Split-Path $project) 'obj/project.assets.json'
                $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
                $folders = @($assets.packageFolders.Keys)
                if ($folders.Count -ne 1 -or [IO.Path]::GetFullPath($folders[0]).TrimEnd('/','\') -ne $session.Packages.TrimEnd('/','\')) { throw 'Assets escaped private package directory.' }
                $sources = @($assets.project.restore.sources.Keys)
                if ($sources.Count -ne 1 -or $sources[0] -cne 'https://api.nuget.org/v3/index.json') { throw 'Assets contain a non-public source.' }
                $lock = Get-Content -LiteralPath (Join-Path (Split-Path $project) 'packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
                $result.Add([pscustomobject]@{ Project = [IO.Path]::GetRelativePath($Root, $project).Replace('\','/'); AssetsSHA256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash })
                foreach ($key in @($assets.libraries.Keys | Sort-Object)) {
                    $library = $assets.libraries[$key]
                    if ($library.type -ne 'package') { continue }
                    $parts = $key.Split('/')
                    $folder = [IO.Path]::GetFullPath((Join-Path $session.Packages $library.path))
                    if (-not $folder.StartsWith($session.Packages + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Package path escaped private session.' }
                    $archive = Join-Path $folder ($key.ToLowerInvariant().Replace('/','.') + '.nupkg')
                    $rawHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
                    $rawBase64 = [Convert]::ToBase64String([Convert]::FromHexString($rawHash))
                    $reader = [NuGet.Packaging.PackageArchiveReader]::new($archive)
                    try { $contentHash = $reader.GetContentHash([Threading.CancellationToken]::None, [Func[string]]{ $rawBase64 }) }
                    finally { $reader.Dispose() }
                    $entries = @($lock.dependencies.Values | ForEach-Object { $_.GetEnumerator() } | Where-Object { $_.Key -ieq $parts[0] -and $_.Value.resolved -eq $parts[1] })
                    if ($entries.Count -eq 0 -or @($entries | Where-Object { $_.Value.contentHash -cne $contentHash }).Count -gt 0 -or $library.sha512 -cne $contentHash) { throw "Lock/content mismatch: $key" }
                    $metadata = Get-Content -LiteralPath (Join-Path $folder '.nupkg.metadata') -Raw | ConvertFrom-Json
                    if ($metadata.source -cne 'https://api.nuget.org/v3/index.json') { throw "Unexpected package source: $key" }
                    $files = [Collections.Generic.List[object]]::new()
                    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
                    try {
                        foreach ($file in @($library.files | Where-Object { $_ -match '\.(dll|exe|props|targets)$' } | Sort-Object)) {
                            $entry = $zip.GetEntry($file)
                            if ($null -eq $entry) { throw "Missing restored archive entry: $key/$file" }
                            $stream = $entry.Open()
                            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                            finally { $stream.Dispose() }
                            $actual = (Get-FileHash -LiteralPath (Join-Path $folder $file) -Algorithm SHA256).Hash
                            if ($hash -cne $actual) { throw "Extracted dependency differs from archive: $key/$file" }
                            $files.Add([pscustomobject]@{ Path = $file; SHA256 = $hash })
                        }
                    }
                    finally { $zip.Dispose() }
                    $result.Add([pscustomobject]@{ Package = $key; ContentHash = $contentHash; ArchiveSHA512 = $rawHash; Files = @($files.ToArray()) })
                }
            }
            return $result.ToArray()
        }
        $before = @(Get-RestoredInputs)
        & $Action $session
        if ($LASTEXITCODE -ne 0) { throw "Preparation action failed with exit code $LASTEXITCODE." }
        $after = @(Get-RestoredInputs)
        if (($before | ConvertTo-Json -Depth 15 -Compress) -cne ($after | ConvertTo-Json -Depth 15 -Compress) -or
            ($inputHashes | ConvertTo-Json -Compress) -cne ((Get-InputHashes) | ConvertTo-Json -Compress)) { throw 'Build inputs changed during preparation.' }
        $compiler = @($projects | ForEach-Object {
            $references = Join-Path $session.References (([IO.Path]::GetFileNameWithoutExtension($_)) + '.txt')
            if (-not (Test-Path -LiteralPath $references)) { throw "Compilation receipt missing: $_" }
            foreach ($reference in Get-Content -LiteralPath $references) {
                if ($reference.StartsWith($session.Packages + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) {
                    [pscustomobject]@{ Project = [IO.Path]::GetRelativePath($Root, $_); Path = [IO.Path]::GetRelativePath($session.Packages, $reference).Replace('\','/'); SHA256 = (Get-FileHash -LiteralPath $reference -Algorithm SHA256).Hash }
                }
            }
        })
        $expected = @("KeelMatrix.AuthSurface.$Version.nupkg", "KeelMatrix.AuthSurface.$Version.snupkg")
        $actual = @(Get-ChildItem -LiteralPath $session.Output -File | ForEach-Object Name | Sort-Object)
        if (@(Compare-Object $expected $actual).Count) { throw 'Session did not produce the exact archive pair.' }
        $pair = @($expected | ForEach-Object { [pscustomobject]@{ Name = $_; SHA256 = (Get-FileHash -LiteralPath (Join-Path $session.Output $_) -Algorithm SHA256).Hash } })
        if (($pair | ConvertTo-Json -Compress) -cne ($session.InspectedArchives | ConvertTo-Json -Compress)) { throw 'Archive pair changed or inspection receipt is missing.' }
        $receipt = [ordered]@{ SchemaVersion = 1; Commit = $ExpectedCommit; Version = $Version; Configuration = $Configuration; SDK = $sdk; NuGet = $nuget; CompletedUtc = [DateTime]::UtcNow.ToString('o'); PackagesPath = $session.Packages; HttpCachePath = $env:NUGET_HTTP_CACHE_PATH; ScratchPath = $env:NUGET_SCRATCH; Inputs = $inputHashes; Before = $before; After = $after; CompilerReferences = $compiler; Archives = $pair }
        $evidenceDirectory = Join-Path $Root 'artifacts/build-evidence'
        New-Item -ItemType Directory -Path $OutputDirectory, $evidenceDirectory -Force | Out-Null
        $receiptName = (Split-Path $OutputDirectory -Leaf) + '-' + $ExpectedCommit + '-' + [guid]::NewGuid().ToString('N')
        $dependencyEvidence = Join-Path $evidenceDirectory ($receiptName + '/dependencies')
        New-Item -ItemType Directory -Path $dependencyEvidence -Force | Out-Null
        # Preserve the shipping compiler dependency bytes, beyond the lifetime of the private cache.
        $retained = @()
        foreach ($reference in @($compiler | Where-Object { $_.Project.Replace('\','/') -eq 'src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj' })) {
            $parts = $reference.Path.Split('/')
            $archive = Join-Path $session.Packages ("$($parts[0])/$($parts[1])/$($parts[0]).$($parts[1]).nupkg")
            foreach ($file in @($archive, (Join-Path $session.Packages $reference.Path))) {
                $relative = [IO.Path]::GetRelativePath($session.Packages, $file)
                $destination = Join-Path $dependencyEvidence $relative
                New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
                Copy-Item -LiteralPath $file -Destination $destination -Force
                $retained += [pscustomobject]@{ Path = [IO.Path]::GetRelativePath($evidenceDirectory, $destination).Replace('\','/'); SHA256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
            }
        }
        $receipt.RetainedDependencyFiles = $retained
        # Replace only the current candidate pair. Historical retained evidence is never removed.
        foreach ($name in $expected) { Copy-Item -LiteralPath (Join-Path $session.Output $name) -Destination (Join-Path $OutputDirectory $name) -Force }
        $receiptPath = Join-Path $evidenceDirectory ($receiptName + '.json')
    }
    catch { $failure = $_; throw }
    finally {
        foreach ($name in $previous.Keys) {
            if ($null -eq $previous[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
            else { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
        }
        Set-Location -LiteralPath $location.Path
        try {
            $resolved = [IO.Path]::GetFullPath($sessionRoot)
            $prefix = [IO.Path]::GetFullPath($temporaryBase).TrimEnd('/','\') + [IO.Path]::DirectorySeparatorChar
            if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'authsurface-public-*') { throw 'Session cleanup escaped its temporary root.' }
            if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
        }
        catch { if ($null -eq $failure) { throw }; Write-Warning "Session cleanup also failed: $($_.Exception.Message)" }
    }
    # Cleanup is part of success. A cleanup failure must not leave a success receipt.
    $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $receiptPath -Encoding utf8
    Write-Host "Public build-input receipt: $receiptPath"
    $global:LASTEXITCODE = 0
}
