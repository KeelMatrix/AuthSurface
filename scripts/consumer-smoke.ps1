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
    <PackageReference Include="KeelMatrix.AuthSurface" Version="0.1.0" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $project 'Consumer.csproj') -Encoding utf8

    @'
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
});
WebApplication app = builder.Build();
app.Urls.Add("http://127.0.0.1:0");
app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
app.MapGet("/anonymous", () => Results.Ok()).AllowAnonymous();
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

    app.MapGet("/new", () => Results.Ok());
    AuthSurfaceReport changed = await scanner.ScanAsync();
    AuthSurfaceVerificationResult mismatch = AuthSurfaceVerifier.Compare(changed, baselinePath);
    if (mismatch.IsValid || !mismatch.Violations.Any(violation => violation.Code == "endpoint-added"))
    {
        throw new InvalidOperationException("consumer smoke did not report the added endpoint");
    }

    Console.WriteLine("consumer smoke passed: package reference scan, explicit baseline creation, matching comparison, and structured endpoint-added failure");
}
finally
{
    File.Delete(baselinePath);
    await app.StopAsync();
    await app.DisposeAsync();
}
'@ | Set-Content -LiteralPath (Join-Path $project 'Program.cs') -Encoding utf8

    $env:KEELMATRIX_NO_TELEMETRY = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DO_NOT_TRACK = '1'
    & dotnet restore (Join-Path $project 'Consumer.csproj') --configfile (Join-Path $root 'NuGet.config') --force-evaluate --no-cache
    if ($LASTEXITCODE -ne 0) { throw "consumer restore failed with exit code $LASTEXITCODE" }
    & dotnet run --project (Join-Path $project 'Consumer.csproj') -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "consumer run failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
