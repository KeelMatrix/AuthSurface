[CmdletBinding()]
param([ValidateSet('All','Cleanup')][string] $Section = 'All')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $sourceRoot 'build/Invoke-NestedPwsh.ps1')
. (Join-Path $sourceRoot 'build/Invoke-PublicPackageSession.ps1')
$scratch = if ($env:AUTHSURFACE_TEST_SCRATCH) { $env:AUTHSURFACE_TEST_SCRATCH } else { [IO.Path]::GetTempPath() }
$scratch = Get-PublicPackagePath $scratch
$testRoot = Join-Path $scratch ('authsurface-input-tests-' + [guid]::NewGuid().ToString('N'))
$old = @{}
$results = [Collections.Generic.List[object]]::new()
foreach ($name in @('NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','NUGET_FALLBACK_PACKAGES','KEELMATRIX_NO_TELEMETRY','DOTNET_CLI_TELEMETRY_OPTOUT','DO_NOT_TRACK','AUTHSURFACE_TEST_SCRATCH','AUTHSURFACE_INPUT_NATIVE','AUTHSURFACE_INPUT_PHASE','AUTHSURFACE_INPUT_FAILURE','AUTHSURFACE_INPUT_ROOT')) { $old[$name] = [Environment]::GetEnvironmentVariable($name) }
function Invoke-Checked([scriptblock] $Action) {
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Fixture command failed: $LASTEXITCODE" }
}
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $env:AUTHSURFACE_TEST_SCRATCH = $testRoot
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $testRoot 'http-inherited'
    $env:NUGET_SCRATCH = Join-Path $testRoot 'scratch-inherited'
    $env:AUTHSURFACE_INPUT_NATIVE = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    $fixture = Join-Path $testRoot 'fixture with spaces'
    $library = Join-Path $fixture 'src/KeelMatrix.AuthSurface'
    $probe = Join-Path $fixture 'tests/Probe'
    New-Item -ItemType Directory -Path $library, $probe, (Join-Path $fixture 'build'), (Join-Path $fixture 'scripts') -Force | Out-Null
    foreach ($file in @('Directory.Build.props','Directory.Packages.props','NuGet.config','global.json')) { Copy-Item -LiteralPath (Join-Path $sourceRoot $file) -Destination $fixture }
    foreach ($file in @('Invoke-PublicPackageSession.ps1','Record-CompilerInputs.targets')) { Copy-Item -LiteralPath (Join-Path $sourceRoot "build/$file") -Destination (Join-Path $fixture 'build') }
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.AuthSurface</PackageId><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat><GenerateDocumentationFile>false</GenerateDocumentationFile><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><PackageReference Include="KeelMatrix.Telemetry" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $library 'KeelMatrix.AuthSurface.csproj')
    @'
namespace Fixture;
public static class DependencyProbe {
    public static string Hash() {
        new KeelMatrix.Telemetry.Client("fixture", typeof(DependencyProbe)).TrackActivation();
        return System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(typeof(KeelMatrix.Telemetry.Client).Assembly.Location)));
    }
}
'@ | Set-Content -LiteralPath (Join-Path $library 'Probe.cs')
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType><IsPackable>false</IsPackable><GenerateDocumentationFile>false</GenerateDocumentationFile><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj" /></ItemGroup>
  <Target Name="RunDependencyAssertion" BeforeTargets="VSTest"><Exec Command="dotnet &quot;$(TargetPath)&quot;" /></Target>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $probe 'Probe.csproj')
    Set-Content -LiteralPath (Join-Path $probe 'Program.cs') -Value 'System.Console.WriteLine(Fixture.DependencyProbe.Hash());'
    Push-Location $fixture
    try {
        Invoke-Checked { dotnet new sln -n KeelMatrix.AuthSurface }
        Invoke-Checked { dotnet sln ./KeelMatrix.AuthSurface.sln add ./src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj ./tests/Probe/Probe.csproj }
        $baseline = Join-Path $testRoot 'verified-public'
        $env:NUGET_PACKAGES = $baseline
        Invoke-Checked { dotnet restore ./KeelMatrix.AuthSurface.sln --configfile ./NuGet.config --packages $baseline --force-evaluate --force -p:RestoreNoHttpCache=true -p:RestoreFallbackFolders= -p:RestoreAdditionalProjectFallbackFolders= }
        $publicDll = Join-Path $baseline 'keelmatrix.telemetry/0.1.1/lib/net8.0/KeelMatrix.Telemetry.dll'
        $publicHash = (Get-FileHash -LiteralPath $publicDll -Algorithm SHA256).Hash
        $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($publicDll).Version.ToString()
        "if (Fixture.DependencyProbe.Hash() != `"$publicHash`") throw new System.InvalidOperationException(`"wrong compiled dependency bytes`" ); System.Console.WriteLine(`"Real dependency assertion passed`" );" | Set-Content -LiteralPath (Join-Path $probe 'Program.cs')
    }
    finally { Pop-Location }

    # A real, loadable same-ID/version/assembly-version package in owned storage.
    $poison = Join-Path $testRoot 'variant-source'
    New-Item -ItemType Directory -Path $poison -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'global.json'), (Join-Path $sourceRoot 'NuGet.config') -Destination $poison
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.Telemetry</PackageId><Version>0.1.1</Version><AssemblyName>KeelMatrix.Telemetry</AssemblyName><AssemblyVersion>$assemblyVersion</AssemblyVersion></PropertyGroup></Project>" | Set-Content -LiteralPath (Join-Path $poison 'Variant.csproj')
    'namespace KeelMatrix.Telemetry { public sealed class Client { public Client(string product, System.Type type) {} public void TrackActivation() { System.GC.KeepAlive("distinct executable fixture bytes"); } public void TrackHeartbeat() {} } }' | Set-Content -LiteralPath (Join-Path $poison 'Client.cs')
    $env:NUGET_PACKAGES = Join-Path $testRoot 'variant-build-cache'
    Invoke-Checked { dotnet pack (Join-Path $poison 'Variant.csproj') -c Release -o (Join-Path $poison 'feed') }
    $poisonCache = Join-Path $testRoot 'inherited-poison'
    $control = Join-Path $testRoot 'unisolated-control'
    New-Item -ItemType Directory -Path $control -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'global.json') -Destination $control
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="KeelMatrix.Telemetry" Version="[0.1.1]" /></ItemGroup></Project>' | Set-Content -LiteralPath (Join-Path $control 'Control.csproj')
    'new KeelMatrix.Telemetry.Client("fixture", typeof(object)).TrackActivation(); System.Console.WriteLine(System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(typeof(KeelMatrix.Telemetry.Client).Assembly.Location))));' | Set-Content -LiteralPath (Join-Path $control 'Program.cs')
    $env:NUGET_PACKAGES = $poisonCache
    Invoke-Checked { dotnet restore (Join-Path $control 'Control.csproj') --source (Join-Path $poison 'feed') --packages $poisonCache }
    $variantDll = Join-Path $poisonCache 'keelmatrix.telemetry/0.1.1/lib/net8.0/KeelMatrix.Telemetry.dll'
    $variantHash = (Get-FileHash -LiteralPath $variantDll -Algorithm SHA256).Hash
    if ($variantHash -eq $publicHash -or [Reflection.AssemblyName]::GetAssemblyName($variantDll).Version.ToString() -ne $assemblyVersion) { throw 'Variant stimulus is not distinct with the same assembly version.' }
    $controlOutput = & dotnet run --project (Join-Path $control 'Control.csproj') --no-restore 2>&1
    if ($LASTEXITCODE -ne 0 -or ($controlOutput -join "`n") -notlike "*$variantHash*") { throw "Unisolated control did not consume variant: $controlOutput" }
    Write-Host "Unisolated real compiler selected wrong same-version DLL: $variantHash (public $publicHash)."
    $poisonBefore = @(Get-ChildItem -LiteralPath $poisonCache -Recurse -File | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName).Hash)" }) -join "`n"
    $env:NUGET_FALLBACK_PACKAGES = $poisonCache
    $env:AUTHSURFACE_INPUT_ROOT = $fixture
    # The shipping commands remain intact; only unrelated gates and the shipping-specific inspector change in these copies.
    foreach ($name in @('validate.ps1','rehearse-release.ps1')) {
        $text = Get-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Raw
        if ($name -eq 'validate.ps1') {
            $text = [regex]::Replace($text, "(?m)^    Invoke-Gate 'format [^\r\n]+", '')
            $start = $text.IndexOf('    $vulnerabilityReportPath =')
            $end = $text.IndexOf("    Invoke-Gate 'clean package consumer smoke'", $start)
            $text = $text.Remove($start, $end - $start)
        }
        Set-Content -LiteralPath (Join-Path $fixture "scripts/$name") -Value $text
    }
    $gateNames = @('check-website-metadata','validate-release-workflow','test-release-workflow','test-action-pin-requests','test-release-contract','check-release-contract','test-website-metadata','test-source-provenance','consumer-smoke','test-consumer-smoke','test-build-inputs','test-vulnerability-gate','test-repository-hygiene','check-repository-hygiene')
    foreach ($name in $gateNames) { '$global:LASTEXITCODE = 0' | Set-Content -LiteralPath (Join-Path $fixture "scripts/$name.ps1") }
    '$global:LASTEXITCODE = 0' | Set-Content -LiteralPath (Join-Path $fixture 'build/Test-NestedPwshLaunch.ps1')
    @'
param($PackagePath, $SymbolPackagePath, $ExpectedVersion, $RepositoryRoot, $ExpectedCommit, $VerifierPath)
$ErrorActionPreference = 'Stop'
if ($env:AUTHSURFACE_INPUT_PHASE -eq 'inspect') {
    Write-Host "INJECTED_BOUNDARY_inspect_$env:AUTHSURFACE_INPUT_FAILURE"
    if ($env:AUTHSURFACE_INPUT_FAILURE -eq 'throw') { throw 'injected-inspect-throw' }
    & $env:AUTHSURFACE_INPUT_NATIVE --invalid-input-fixture
    return
}
$zip = [IO.Compression.ZipFile]::OpenRead($PackagePath)
try {
    $stream = $zip.GetEntry('lib/net8.0/KeelMatrix.AuthSurface.dll').Open()
    try { $packed = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
    $built = (Get-FileHash -LiteralPath (Join-Path $RepositoryRoot 'src/KeelMatrix.AuthSurface/bin/Release/net8.0/KeelMatrix.AuthSurface.dll')).Hash
    if ($packed -ne $built) { throw 'Packed DLL does not match the real compilation.' }
}
finally { $zip.Dispose() }
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath (Join-Path $fixture 'scripts/inspect-package.ps1')
    $workflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.github/workflows/release.yml') -Raw
    $release = [regex]::Match($workflow, '(?ms)^      - name: Prepare public locked package pair\r?\n.*?^        run: \|\r?\n(?<code>.*?)(?=^      - name:)').Groups['code'].Value
    if (-not $release) { throw 'Release preparation block missing.' }
    $release = [regex]::Replace($release, '(?m)^          ', '').Replace("'`${{ github.sha }}'", "'$('1' * 40)'")
    $preamble = @'
$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
function git { $global:LASTEXITCODE = 0; '1111111111111111111111111111111111111111' }
function dotnet {
    if ($args[0] -in @('restore','build','test','pack')) {
        $base = Split-Path $env:NUGET_PACKAGES -Parent
        if ((Split-Path $base -Leaf) -notlike 'authsurface-public-*' -or
            $env:NUGET_HTTP_CACHE_PATH -cne (Join-Path $base 'http') -or
            $env:NUGET_SCRATCH -cne (Join-Path $base 'scratch') -or $env:NUGET_FALLBACK_PACKAGES) { throw 'Native caller escaped the private environment.' }
        if ($args[0] -ne 'restore' -and $args -cnotcontains "-p:RestorePackagesPath=$env:NUGET_PACKAGES") { throw 'Native caller omitted the explicit private package path.' }
    }
    if ($args[0] -eq $env:AUTHSURFACE_INPUT_PHASE) {
        Write-Host "INJECTED_BOUNDARY_$($args[0])_$env:AUTHSURFACE_INPUT_FAILURE"
        if ($env:AUTHSURFACE_INPUT_FAILURE -eq 'throw') { throw "injected-$($args[0])-throw" }
        & $env:AUTHSURFACE_INPUT_NATIVE --invalid-input-fixture
        $global:LASTEXITCODE = $LASTEXITCODE
        return
    }
    & $env:AUTHSURFACE_INPUT_NATIVE @args
    $global:LASTEXITCODE = $LASTEXITCODE
}
Set-Location -LiteralPath $env:AUTHSURFACE_INPUT_ROOT
$env:GITHUB_WORKSPACE = $env:AUTHSURFACE_INPUT_ROOT
$env:RELEASE_VERSION = '0.1.1'
'@
    $child = Join-Path $testRoot 'child.ps1'
    foreach ($caller in $(if ($Section -eq 'All') { @('validate.ps1','rehearse-release.ps1','release-step') } else { @() })) {
        $code = if ($caller -eq 'release-step') { $release } else { "& ./scripts/$caller" }
        Set-Content -LiteralPath $child -Value ($preamble + "`n" + $code + "`nWrite-Output 'CALLER_SUCCESS'`n")
        $env:AUTHSURFACE_INPUT_PHASE = $null
        $env:AUTHSURFACE_INPUT_FAILURE = $null
        $out = Invoke-NestedPwsh -ArgumentList @('-NoProfile','-NonInteractive','-File',$child) 2>&1
        if ($LASTEXITCODE -ne 0 -or ($out -join "`n") -notlike '*CALLER_SUCCESS*' -or ($out -join "`n") -notlike '*Real dependency assertion passed*') { throw "Real $caller failed: $out" }
        $receipt = Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Filter '*.json' | Sort-Object LastWriteTimeUtc | Select-Object -Last 1 | Get-Content -Raw | ConvertFrom-Json
        $refs = @($receipt.CompilerReferences | Where-Object Path -Like 'keelmatrix.telemetry/*/lib/net8.0/KeelMatrix.Telemetry.dll')
        if ($refs.Count -eq 0 -or @($refs | Where-Object SHA256 -NE $publicHash).Count -or @($receipt.Before | Where-Object Package -EQ 'KeelMatrix.Telemetry/0.1.1').Count -eq 0) { throw 'Compiler receipt did not bind public DLL.' }
        Write-Host "Real $caller ignored inherited/fallback variant and correlated compiler/archive bytes."
        $results.Add([pscustomobject]@{ Caller = $caller; Phase = 'positive'; ExitCode = 0; CompilerSHA256 = $refs[0].SHA256; PublicSHA256 = $publicHash; VariantSHA256 = $variantHash })
        # Every native boundary and thrown boundary must stop the real caller. Earlier commands still run natively.
        foreach ($phase in @('restore','build','test','pack','inspect')) {
            foreach ($mode in @('native','throw')) {
                $env:AUTHSURFACE_INPUT_PHASE = $phase
                $env:AUTHSURFACE_INPUT_FAILURE = $mode
                $priorReceipts = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Filter '*.json').Count
                $out = Invoke-NestedPwsh -ArgumentList @('-NoProfile','-NonInteractive','-File',$child) 2>&1
                $childExit = $LASTEXITCODE
                if ($LASTEXITCODE -eq 0 -or ($out -join "`n") -like '*CALLER_SUCCESS*' -or ($out -join "`n") -notlike "*INJECTED_BOUNDARY_${phase}_$mode*") { throw "$caller did not reach/reject $phase/$mode failure: $out" }
                if (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Filter '*.json').Count -ne $priorReceipts) { throw 'Failed preparation emitted success receipt.' }
                Write-Host "$caller rejected $phase/$mode; no success receipt."
                $results.Add([pscustomobject]@{ Caller = $caller; Phase = $phase; Failure = $mode; ExitCode = $childExit; Diagnostic = ($out -join "`n") })
            }
        }
    }
    $env:AUTHSURFACE_INPUT_PHASE = $null
    $env:AUTHSURFACE_INPUT_FAILURE = $null
    # Same-process success/exception/native failure: exact environment, location and cleanup.
    . (Join-Path $fixture 'build/Invoke-PublicPackageSession.ps1')
    $alias = Join-Path $testRoot 'aliased fixture'
    $linkType = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
    New-Item -ItemType $linkType -Path $alias -Target $fixture | Out-Null
    foreach ($mode in @('success','empty-environment','aliased-root','throw','native','mutate-assets','mutate-dll','mutate-lock','mutate-pair','mutate-receipt','lock-mismatch','graph-drift','cleanup-failure')) {
        $env:NUGET_PACKAGES = $null
        $isSuccess = $mode -in @('success','empty-environment','aliased-root')
        if ($mode -eq 'empty-environment') {
            foreach ($name in @('NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','NUGET_FALLBACK_PACKAGES','KEELMATRIX_NO_TELEMETRY','DOTNET_CLI_TELEMETRY_OPTOUT','DO_NOT_TRACK')) { [Environment]::SetEnvironmentVariable($name, '') }
        }
        $environmentBefore = @{}
        foreach ($name in @('NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','NUGET_FALLBACK_PACKAGES','KEELMATRIX_NO_TELEMETRY','DOTNET_CLI_TELEMETRY_OPTOUT','DO_NOT_TRACK')) { $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name) }
        $locationBefore = (Get-Location).Path
        $caught = $false
        $lockPath = Join-Path $library 'packages.lock.json'
        $lockBytes = [IO.File]::ReadAllBytes($lockPath)
        $projectPath = Join-Path $library 'KeelMatrix.AuthSurface.csproj'
        $projectBytes = [IO.File]::ReadAllBytes($projectPath)
        $script:enteredAction = $false
        $receiptsBefore = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Filter '*.json' -ErrorAction SilentlyContinue).Count
        if ($mode -eq 'cleanup-failure') {
            function Remove-Item {
                [CmdletBinding()]
                param([string] $LiteralPath, [switch] $Recurse, [switch] $Force)
                if ((Split-Path $LiteralPath -Leaf) -like 'authsurface-public-*') { throw 'secondary-cleanup-failure' }
                Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
            }
        }
        if ($mode -eq 'lock-mismatch') {
            $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
            $lock.dependencies['net8.0']['KeelMatrix.Telemetry'].contentHash = [Convert]::ToBase64String([byte[]]::new(64))
            $lock | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $lockPath
        }
        if ($mode -eq 'graph-drift') {
            (Get-Content -LiteralPath $projectPath -Raw).Replace('Include="KeelMatrix.Telemetry"', 'Include="KeelMatrix.Telemetry" VersionOverride="[0.1.0]"') | Set-Content -LiteralPath $projectPath
        }
        try {
            $inputRoot = if ($mode -eq 'aliased-root') { $alias } else { $fixture }
            Invoke-PublicPackageSession -Root $inputRoot -Solution (Join-Path $inputRoot 'KeelMatrix.AuthSurface.sln') -OutputDirectory (Join-Path $inputRoot 'artifacts/cleanup') -ExpectedCommit ('1' * 40) -Version '0.1.1' -Action {
                param($session)
                $script:enteredAction = $true
                $script:observedSession = $session.Root
                if ($session.RepositoryRoot -cne $fixture -or $session.Solution -cne (Join-Path $fixture 'KeelMatrix.AuthSurface.sln')) { throw 'Session retained a directory alias.' }
                if ($env:NUGET_PACKAGES -cne $session.Packages -or $env:NUGET_HTTP_CACHE_PATH -cne (Join-Path $session.Root 'http') -or
                    $env:NUGET_SCRATCH -cne (Join-Path $session.Root 'scratch') -or $env:NUGET_FALLBACK_PACKAGES) { throw 'Session environment isolation failed.' }
                if ($mode -eq 'throw') { Set-Location $session.Output; throw 'original-cleanup-failure' }
                if ($mode -eq 'native') { & $env:AUTHSURFACE_INPUT_NATIVE --invalid-input-fixture; return }
                $properties = $session.Properties
                Invoke-Checked { dotnet build (Join-Path $fixture 'KeelMatrix.AuthSurface.sln') -c Release --no-restore --no-incremental @properties }
                Invoke-Checked { dotnet test (Join-Path $fixture 'KeelMatrix.AuthSurface.sln') -c Release --no-build --no-restore @properties }
                Invoke-Checked { dotnet pack (Join-Path $library 'KeelMatrix.AuthSurface.csproj') -c Release --no-build --no-restore @properties -o $session.Output }
                & (Join-Path $fixture 'scripts/inspect-package.ps1') -PackagePath (Join-Path $session.Output 'KeelMatrix.AuthSurface.0.1.1.nupkg') -RepositoryRoot $fixture
                Set-PublicPackageInspection $session
                switch ($mode) {
                    'mutate-assets' { Add-Content -LiteralPath (Join-Path $library 'obj/project.assets.json') -Value ' ' }
                    'mutate-dll' { Add-Content -LiteralPath (Join-Path $session.Packages 'keelmatrix.telemetry/0.1.1/lib/net8.0/KeelMatrix.Telemetry.dll') -Value 'changed' }
                    'mutate-lock' { Add-Content -LiteralPath $lockPath -Value ' ' }
                    'mutate-pair' { Add-Content -LiteralPath (Join-Path $session.Output 'KeelMatrix.AuthSurface.0.1.1.nupkg') -Value 'changed' }
                    'mutate-receipt' { $session.InspectedArchives[0].SHA256 = 'changed' }
                }
            }
        }
        catch {
            $caught = $true
            if ($isSuccess) { throw }
            $diagnostic = switch ($mode) {
                'throw' { 'original-cleanup-failure' }
                'native' { 'Preparation action failed' }
                'mutate-assets' { 'Build inputs changed' }
                'mutate-dll' { 'Extracted dependency differs from archive' }
                'mutate-lock' { 'Build inputs changed' }
                { $_ -in @('mutate-pair','mutate-receipt') } { 'Archive pair changed' }
                { $_ -in @('lock-mismatch','graph-drift') } { 'Public locked restore failed' }
                'cleanup-failure' { 'secondary-cleanup-failure' }
            }
            if ($_ -notlike "*$diagnostic*") { throw }
            Write-Host "$mode rejected at the expected boundary: $diagnostic."
        }
        finally {
            [IO.File]::WriteAllBytes($lockPath, $lockBytes)
            [IO.File]::WriteAllBytes($projectPath, $projectBytes)
            if ($mode -eq 'cleanup-failure') {
                Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:Remove-Item
                $leftover = [IO.Path]::GetFullPath($script:observedSession)
                if (-not $leftover.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup failure test escaped fixture.' }
                Remove-Item -LiteralPath $leftover -Recurse -Force
            }
        }
        $receiptsAfter = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Filter '*.json' -ErrorAction SilentlyContinue).Count
        if ($receiptsAfter -ne ($receiptsBefore + $(if ($isSuccess) { 1 } else { 0 }))) { throw "Unexpected success receipt: $mode" }
        if ($mode -in @('lock-mismatch','graph-drift') -and $script:enteredAction) { throw "Bad lock/graph reached compiler action: $mode" }
        if ((-not $isSuccess) -ne $caught -or (Get-Location).Path -ne $locationBefore -or (Test-Path -LiteralPath $script:observedSession)) { throw "Cleanup/result failed: $mode" }
        foreach ($name in $environmentBefore.Keys) { if ([Environment]::GetEnvironmentVariable($name) -cne $environmentBefore[$name]) { throw "Environment restoration failed: $mode/$name before=[$($environmentBefore[$name])] after=[$([Environment]::GetEnvironmentVariable($name))] beforeNull=$($null -eq $environmentBefore[$name]) afterNull=$($null -eq [Environment]::GetEnvironmentVariable($name))" } }
    }
    Remove-Item -LiteralPath $alias -Force
    # A secondary cleanup exception must not replace the original action failure.
    function Remove-Item {
        [CmdletBinding()]
        param([string] $LiteralPath, [switch] $Recurse, [switch] $Force)
        if ((Split-Path $LiteralPath -Leaf) -like 'authsurface-public-*') { throw 'secondary-cleanup-failure' }
        Microsoft.PowerShell.Management\Remove-Item @PSBoundParameters
    }
    $originalPreserved = $false
    try {
        Invoke-PublicPackageSession -Root $fixture -Solution (Join-Path $fixture 'KeelMatrix.AuthSurface.sln') -OutputDirectory (Join-Path $fixture 'artifacts/cleanup') -ExpectedCommit ('1' * 40) -Version '0.1.1' -Action {
            param($session)
            $script:observedSession = $session.Root
            throw 'original-action-failure'
        }
    }
    catch { $originalPreserved = $_ -like '*original-action-failure*' }
    finally { Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:Remove-Item }
    if (-not $originalPreserved) { throw 'Secondary cleanup failure hid the original action failure.' }
    $leftover = [IO.Path]::GetFullPath($script:observedSession)
    if (-not $leftover.StartsWith([IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Secondary cleanup test escaped fixture.' }
    Remove-Item -LiteralPath $leftover -Recurse -Force
    $poisonAfter = @(Get-ChildItem -LiteralPath $poisonCache -Recurse -File | ForEach-Object { "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName).Hash)" }) -join "`n"
    if ($poisonBefore -cne $poisonAfter) { throw 'Inherited package cache was modified.' }
    $evidence = Join-Path $sourceRoot ('artifacts/build-input-tests/' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixture 'artifacts/build-evidence') -Destination (Join-Path $evidence 'sessions') -Recurse
    [ordered]@{ Section = $Section; AssemblyVersion = $assemblyVersion; PublicSHA256 = $publicHash; VariantSHA256 = $variantHash; Results = $results.ToArray(); CleanupControls = @('success','empty-environment','aliased-root','throw','native','mutate-assets','mutate-dll','mutate-lock','mutate-pair','mutate-receipt','lock-mismatch','graph-drift','cleanup-failure','secondary-cleanup-failure'); InheritedCacheUnchanged = $true } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'controls.json')
    Write-Host "Public compiler-input regressions passed ($Section): $($results.Count) caller controls; default/inherited isolation, mutations and cleanup. Evidence: $evidence"
}
finally {
    foreach ($name in $old.Keys) {
        if ($null -eq $old[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $old[$name]) }
    }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($scratch).TrimEnd('/','\') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Input fixture cleanup escaped scratch.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
