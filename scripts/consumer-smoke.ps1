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
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

static WebApplication BuildApplication(bool fallbackPolicy, bool includeNewEndpoint)
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

    Console.WriteLine("consumer smoke passed: documented PackageReference install, runtime scan, explicit baseline creation, matching comparison, unprotected-policy failure, and structured endpoint-added failure");
}
finally
{
    File.Delete(baselinePath);
    await app.DisposeAsync();
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
