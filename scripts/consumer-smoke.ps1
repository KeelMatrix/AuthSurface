[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
$project = Join-Path $root 'consumer'
$packageVersion = '0.1.0'
$packageId = 'KeelMatrix.AuthSurface'

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Package does not exist: $PackagePath"
}

$suppliedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$suppliedHash = (Get-FileHash -LiteralPath $suppliedPackage -Algorithm SHA512).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Path $feed, $project -Force | Out-Null
Copy-Item -LiteralPath $suppliedPackage -Destination (Join-Path $feed "$packageId.$packageVersion.nupkg")

function Invoke-IsolatedDotnet([string] $Name, [string] $CachePath, [scriptblock] $Action) {
    $previousCache = $env:NUGET_PACKAGES
    $env:NUGET_PACKAGES = $CachePath
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

    $cacheHash = [Convert]::ToHexString(
        [Convert]::FromBase64String((Get-Content -LiteralPath $cachedHashFile -Raw).Trim())).ToLowerInvariant()
    if ($cacheHash -ne $ExpectedHash) {
        throw "The consumer restored a different $packageId $packageVersion artifact. supplied=$ExpectedHash consumed=$cacheHash"
    }

    $assetsPath = Join-Path $ProjectPath 'obj/project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        throw "The consumer restore did not produce project.assets.json: $assetsPath"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $library = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -eq "$packageId/$packageVersion" }) | Select-Object -First 1
    $suppliedHashBase64 = [Convert]::ToBase64String([Convert]::FromHexString($ExpectedHash))
    if ($null -eq $library -or [string]$library.Value.sha512 -ne $suppliedHashBase64) {
        $reportedHash = if ($null -eq $library) { '<missing>' } else { [string]$library.Value.sha512 }
        throw "project.assets.json did not select the supplied $packageId $packageVersion artifact. reported=$reportedHash"
    }

    Write-Output "Verified supplied package artifact: $packageId $packageVersion SHA512=$ExpectedHash"
}

try {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $root 'global.json')

    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="./feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="KeelMatrix.AuthSurface" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $root 'NuGet.config') -Encoding utf8

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

    $configPath = Join-Path $root 'NuGet.config'
    $feedPath = (Resolve-Path -LiteralPath $feed).Path
    $installCache = Join-Path $root 'packages-install'
    $restoreCache = Join-Path $root 'packages-restore'
    $runCache = Join-Path $root 'packages-run'
    New-Item -ItemType Directory -Path $installCache, $restoreCache, $runCache -Force | Out-Null

    Invoke-IsolatedDotnet 'PackageReference install' $installCache {
        dotnet add (Join-Path $project 'Consumer.csproj') package $packageId --version $packageVersion --source $feedPath --no-restore
    }

    Invoke-IsolatedDotnet 'consumer restore' $restoreCache {
        dotnet restore (Join-Path $project 'Consumer.csproj') --configfile $configPath --force-evaluate --no-cache
    }
    Assert-SuppliedPackageConsumed $restoreCache $project $suppliedHash

    Invoke-IsolatedDotnet 'consumer run restore' $runCache {
        dotnet restore (Join-Path $project 'Consumer.csproj') --configfile $configPath --force-evaluate --no-cache
    }
    Assert-SuppliedPackageConsumed $runCache $project $suppliedHash
    Invoke-IsolatedDotnet 'consumer run' $runCache {
        dotnet run --project (Join-Path $project 'Consumer.csproj') -c Release --no-restore
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
