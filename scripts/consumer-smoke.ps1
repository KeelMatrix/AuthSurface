[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
$project = Join-Path $root 'consumer'
New-Item -ItemType Directory -Path $feed, $project -Force | Out-Null
Copy-Item -LiteralPath $PackagePath -Destination $feed

try {
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="./feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $root 'NuGet.config') -Encoding utf8

    @'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
  </ItemGroup>
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

    Console.WriteLine("consumer smoke passed: documented package install, runtime scan, explicit baseline creation, matching comparison, unprotected-policy failure, and structured endpoint-added failure");
}
finally
{
    File.Delete(baselinePath);
    await app.DisposeAsync();
}
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs') -Encoding utf8

    $env:KEELMATRIX_NO_TELEMETRY = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DO_NOT_TRACK = '1'
    Push-Location $project
    try {
        & dotnet add package KeelMatrix.AuthSurface --version 0.1.0
        if ($LASTEXITCODE -ne 0) { throw "documented package install failed with exit code $LASTEXITCODE" }
    }
    finally {
        Pop-Location
    }

    & dotnet restore (Join-Path $project 'Consumer.csproj') --configfile (Join-Path $root 'NuGet.config') --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "consumer restore failed with exit code $LASTEXITCODE" }
    & dotnet run --project (Join-Path $project 'Consumer.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "consumer run failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
