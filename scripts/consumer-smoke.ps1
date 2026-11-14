[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [string] $EvidenceDirectory = (Join-Path $PSScriptRoot '../artifacts/consumer-evidence')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $root "candidate feed & 'quoted'"
$project = Join-Path $root 'consumer'
$packageVersion = ([xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$packageId = 'KeelMatrix.AuthSurface'

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Package does not exist: $PackagePath"
}

$suppliedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$suppliedHash = (Get-FileHash -LiteralPath $suppliedPackage -Algorithm SHA512).Hash.ToLowerInvariant()
. (Join-Path $repositoryRoot 'build/Get-PackageContentHash.ps1')
. (Join-Path $repositoryRoot 'build/Read-CandidateInstall.ps1')
$suppliedContentHash = Get-PackageContentHash $suppliedPackage
$zip = [IO.Compression.ZipFile]::OpenRead($suppliedPackage)
try {
    $nuspecs = @($zip.Entries | Where-Object FullName -Like '*.nuspec')
    if ($nuspecs.Count -ne 1) { throw 'Expected one package manifest.' }
    $reader = [IO.StreamReader]::new($nuspecs[0].Open())
    try { $metadata = ([xml]$reader.ReadToEnd()).package.metadata } finally { $reader.Dispose() }
    $groups = @($metadata.dependencies.group)
    if ($metadata.id -cne $packageId -or $metadata.version -cne $packageVersion -or $groups.Count -ne 1 -or
        $groups[0].targetFramework -cne 'net8.0' -or @($groups[0].dependency).Count -ne 1 -or
        $groups[0].dependency.id -cne 'KeelMatrix.Telemetry' -or $groups[0].dependency.version -cne '[0.1.1]') { throw 'Supplied package ID/version/framework/dependency contract mismatch.' }
    $reader = [IO.StreamReader]::new($zip.GetEntry('README.md').Open())
    try { $packedReadme = $reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $zip.Dispose() }
$instructions = @(
    [pscustomobject]@{ Name = 'root'; Install = Read-CandidateInstall (Get-Content -LiteralPath (Join-Path $repositoryRoot 'README.md') -Raw) $packageVersion; Absolute = $false },
    [pscustomobject]@{ Name = 'packed'; Install = Read-CandidateInstall $packedReadme $packageVersion; Absolute = $true }
)
$originalVariables = @{}
foreach ($name in @('NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','NUGET_FALLBACK_PACKAGES','KEELMATRIX_NO_TELEMETRY','DOTNET_CLI_TELEMETRY_OPTOUT','DO_NOT_TRACK')) {
    $originalVariables[$name] = [Environment]::GetEnvironmentVariable($name)
}
$originalLocation = Get-Location
$receipts = [Collections.Generic.List[object]]::new()
New-Item -ItemType Directory -Path $feed, $project -Force | Out-Null
Copy-Item -LiteralPath $suppliedPackage -Destination (Join-Path $feed "$packageId.$packageVersion.nupkg")

function Invoke-IsolatedDotnet([string] $Name, [string] $CachePath, [scriptblock] $Action) {
    $previousCache = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $CachePath
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $root 'http'
    $env:NUGET_SCRATCH = Join-Path $root 'scratch'
    $env:NUGET_FALLBACK_PACKAGES = $null
    $env:KEELMATRIX_NO_TELEMETRY = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DO_NOT_TRACK = '1'
    try {
        Write-Output "=== $Name (NUGET_PACKAGES=$CachePath) ==="
        & $Action
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($null -eq $previousCache) {
            Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
        }
        else {
            $env:NUGET_PACKAGES = $previousCache
        }
    }

    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }
}

function Assert-SuppliedPackageConsumed([string] $CachePath, [string] $ProjectPath, [string] $ExpectedHash) {
    $packageFolder = Join-Path $CachePath ($packageId.ToLowerInvariant() + "\" + $packageVersion)
    $cachedPackage = Join-Path $packageFolder "$($packageId.ToLowerInvariant()).$packageVersion.nupkg"
    $cachedHashFile = "$cachedPackage.sha512"
    if (-not (Test-Path -LiteralPath $cachedPackage) -or -not (Test-Path -LiteralPath $cachedHashFile)) {
        throw "The isolated package cache does not contain the expected $packageId $packageVersion artifact."
    }

    $cacheHash = (Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($cacheHash -cne $ExpectedHash -or (Get-PackageContentHash $cachedPackage) -cne $suppliedContentHash -or
        (Get-Content -LiteralPath $cachedHashFile -Raw).Trim() -cne $suppliedContentHash) {
        throw "The consumer restored a different $packageId $packageVersion artifact. supplied=$ExpectedHash consumed=$cacheHash"
    }

    $assetsPath = Join-Path $ProjectPath 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        throw "The consumer restore did not produce project.assets.json: $assetsPath"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $library = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -eq "$packageId/$packageVersion" }) | Select-Object -First 1
    $suppliedHashBase64 = $suppliedContentHash
    if ($null -eq $library -or [string]$library.Value.sha512 -ne $suppliedHashBase64) {
        $reportedHash = if ($null -eq $library) { '<missing>' } else { [string]$library.Value.sha512 }
        throw "project.assets.json did not select the supplied $packageId $packageVersion artifact. reported=$reportedHash"
    }

    $receipts.Add([pscustomobject]@{ Cache = $CachePath; RawSHA512 = $cacheHash; NuGetContentHash = $suppliedContentHash; AssetsSHA256 = (Get-FileHash -LiteralPath $assetsPath -Algorithm SHA256).Hash })
    Write-Output "Verified supplied package artifact: $packageId $packageVersion SHA512=$ExpectedHash"
}

try {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $root 'global.json')

    @'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $project 'Consumer.csproj') -Encoding utf8

    @'
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

static WebApplication BuildApplication(
    bool fallbackPolicy,
    bool includeNewEndpoint,
    bool includeCorrectedCases = false,
    bool includeEmptyRequirementData = false)
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.Services.AddAuthorization(options =>
    {
        if (fallbackPolicy)
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        }
    });
    WebApplication application = builder.Build();
    application.Urls.Add("http://127.0.0.1:0");
    application.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
    application.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
    application.MapGet("/unprotected", () => Results.Ok());
    if (includeNewEndpoint)
    {
        application.MapGet("/new", () => Results.Ok());
    }
    if (includeCorrectedCases)
    {
        application.MapGet("/literal/{{id}}", () => Results.Ok()).AllowAnonymous();
        application.MapGet("/literal/{id}", () => Results.Ok()).AllowAnonymous();
        application.MapGet("/derived-role", () => Results.Ok()).WithMetadata(
            new AuthorizationPolicyBuilder()
                .AddRequirements(new DerivedRolesRequirement("consumer-state", ["Admin"]))
                .Build());
    }
    if (includeEmptyRequirementData)
    {
        application.MapGet("/empty-requirements", () => Results.Ok())
            .WithMetadata(new EmptyRequirementData())
            .AllowAnonymous();
    }

    return application;
}

await using WebApplication app = BuildApplication(fallbackPolicy: true, includeNewEndpoint: false);
await app.StartAsync();
AuthSurfaceScanner scanner = new(
    app.Services.GetServices<EndpointDataSource>(),
    app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
AuthSurfaceReport first = await scanner.ScanAsync();
string baselinePath = Path.Combine(Path.GetTempPath(), "authsurface-consumer-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    AuthSurfaceBaseline.Create(first, baselinePath, overwrite: false);
    AuthSurfaceVerificationResult matching = AuthSurfaceVerifier.Compare(first, baselinePath);
    matching.AssertValid();

    await using WebApplication unprotectedApp = BuildApplication(fallbackPolicy: false, includeNewEndpoint: false);
    await unprotectedApp.StartAsync();
    AuthSurfaceScanner unprotectedScanner = new(
        unprotectedApp.Services.GetServices<EndpointDataSource>(),
        unprotectedApp.Services.GetRequiredService<IAuthorizationPolicyProvider>());
    AuthSurfaceVerificationResult unprotected = AuthSurfaceVerifier.VerifyPolicy(await unprotectedScanner.ScanAsync());
    if (unprotected.IsValid || !unprotected.Violations.Any(violation => violation.Code == "unprotected-endpoint"))
    {
        throw new InvalidOperationException("consumer smoke did not report an unprotected-policy failure");
    }

    await app.StopAsync();
    await using WebApplication changedApp = BuildApplication(fallbackPolicy: true, includeNewEndpoint: true);
    await changedApp.StartAsync();
    AuthSurfaceScanner changedScanner = new(
        changedApp.Services.GetServices<EndpointDataSource>(),
        changedApp.Services.GetRequiredService<IAuthorizationPolicyProvider>());
    AuthSurfaceReport changed = await changedScanner.ScanAsync();
    AuthSurfaceVerificationResult mismatch = AuthSurfaceVerifier.Compare(changed, baselinePath);
    if (mismatch.IsValid || !mismatch.Violations.Any(violation => violation.Code == "endpoint-added"))
    {
        throw new InvalidOperationException("consumer smoke did not report the added endpoint");
    }

    await app.StopAsync();
    await using WebApplication correctedApp = BuildApplication(fallbackPolicy: true, includeNewEndpoint: false, includeCorrectedCases: true);
    await correctedApp.StartAsync();
    AuthSurfaceScanner correctedScanner = new(
        correctedApp.Services.GetServices<EndpointDataSource>(),
        correctedApp.Services.GetRequiredService<IAuthorizationPolicyProvider>());
    AuthSurfaceReport corrected = await correctedScanner.ScanAsync();
    if (corrected.Endpoints.Count(endpoint => endpoint.Route is "/literal/{{id}}" or "/literal/{id}") != 2)
    {
        throw new InvalidOperationException("consumer smoke did not preserve literal-brace and parameter route identities");
    }
    AuthSurfaceEndpoint derivedRole = corrected.Endpoints.Single(endpoint => endpoint.Route == "/derived-role");
    if (derivedRole.Roles.Count != 0 || !derivedRole.Requirements.Any(requirement => requirement.EndsWith("|opaque", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException("consumer smoke did not preserve opaque derived requirement behavior");
    }

    await using WebApplication invalidPolicyApp = BuildApplication(fallbackPolicy: false, includeNewEndpoint: false, includeEmptyRequirementData: true);
    await invalidPolicyApp.StartAsync();
    AuthSurfaceScanner invalidPolicyScanner = new(
        invalidPolicyApp.Services.GetServices<EndpointDataSource>(),
        invalidPolicyApp.Services.GetRequiredService<IAuthorizationPolicyProvider>());
    try
    {
        await invalidPolicyScanner.ScanAsync();
        throw new InvalidOperationException("consumer smoke accepted empty requirement-data policy construction");
    }
    catch (AuthSurfaceAnalysisException exception) when (exception.Code == "policy-resolution-failed")
    {
    }

    Console.WriteLine("consumer smoke passed: PackageReference install, baseline comparison, policy failures, literal-brace identity, and opaque derived-requirement behavior");
}
finally
{
    File.Delete(baselinePath);
    await app.DisposeAsync();
}

sealed class EmptyRequirementData : Attribute, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [];
}

sealed class DerivedRolesRequirement : RolesAuthorizationRequirement
{
    public DerivedRolesRequirement(string state, IEnumerable<string> allowedRoles)
        : base(allowedRoles) => State = state;

    public string State { get; }
}
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs') -Encoding utf8

    $program = Get-Content -LiteralPath (Join-Path $project 'Program.cs') -Raw
    foreach ($instruction in $instructions) {
        $project = Join-Path $root $instruction.Name
        New-Item -ItemType Directory -Path $project -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'consumer/Consumer.csproj') -Destination $project
        Set-Content -LiteralPath (Join-Path $project 'Program.cs') -Value $program -Encoding utf8
        $config = $instruction.Install.Config
        $source = @($config.configuration.packageSources.add | Where-Object key -CEQ 'authsurface-candidate')
        if ($source.Count -ne 1) { throw 'Candidate feed is missing or ambiguous.' }
        $source[0].SetAttribute('value', $(if ($instruction.Absolute) { $feed } else { [IO.Path]::GetRelativePath($project, $feed).Replace('\','/') }))
        $config.Save((Join-Path $project 'NuGet.config'))
        Push-Location $project
        try {
            $installCache = Join-Path $root ($instruction.Name + '-cold')
            foreach ($command in $instruction.Install.Commands) {
                Invoke-IsolatedDotnet "$($instruction.Name): literal $command" $installCache { & ([scriptblock]::Create($command)) }
            }
            Assert-SuppliedPackageConsumed $installCache $project $suppliedHash
            $restore = $instruction.Install.Commands[1]
            Invoke-IsolatedDotnet "$($instruction.Name): verified warm restore" $installCache { & ([scriptblock]::Create($restore)) }
            Assert-SuppliedPackageConsumed $installCache $project $suppliedHash
            $runCache = Join-Path $root ($instruction.Name + '-run')
            Invoke-IsolatedDotnet "$($instruction.Name): fresh runtime restore" $runCache { & ([scriptblock]::Create($restore)) }
            Assert-SuppliedPackageConsumed $runCache $project $suppliedHash
            Invoke-IsolatedDotnet "$($instruction.Name): consumer run" $runCache { dotnet run --project ./Consumer.csproj -c Release --no-restore }
        }
        finally { Pop-Location }
    }
    New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
    [ordered]@{ SuppliedSHA512 = $suppliedHash; ContentHash = $suppliedContentHash; Readmes = @('root','packed'); Consumed = $receipts.ToArray() } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory ($suppliedHash + '.json')) -Encoding utf8
}
finally {
    foreach ($name in $originalVariables.Keys) {
        if ($null -eq $originalVariables[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $originalVariables[$name]) }
    }
    Set-Location -LiteralPath $originalLocation.Path
    $resolved = [IO.Path]::GetFullPath($root)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('/','\') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path $resolved -Leaf) -notlike 'authsurface-consumer-*') { throw 'Consumer cleanup escaped scratch.' }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
