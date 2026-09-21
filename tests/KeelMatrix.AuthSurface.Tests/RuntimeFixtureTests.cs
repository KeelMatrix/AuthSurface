using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class RuntimeFixtureTests
{
    [Fact]
    public async Task WebApplicationFixtureReadsRuntimeEndpointMetadata()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();

        AuthSurfaceReport report = await ScanAsync(app.Services);

        AuthSurfaceEndpoint explicitEndpoint = Find(report, "/explicit", "GET");
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, explicitEndpoint.AuthorizationKind);
        Assert.True(explicitEndpoint.UsesDefaultPolicy);
        Assert.Contains("Bearer", explicitEndpoint.AuthenticationSchemes);

        AuthSurfaceEndpoint groupEndpoint = Find(report, "/group/item", "GET");
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, groupEndpoint.AuthorizationKind);
        Assert.Contains("Named", groupEndpoint.Policies);
        Assert.Contains("Admin", groupEndpoint.Roles);

        AuthSurfaceEndpoint anonymous = Find(report, "/anonymous", "GET");
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitAnonymous, anonymous.AuthorizationKind);

        AuthSurfaceEndpoint fallback = Find(report, "/unprotected", "GET");
        Assert.Equal(AuthSurfaceAuthorizationKind.FallbackProtected, fallback.AuthorizationKind);
        Assert.True(fallback.UsesFallbackPolicy);
        Assert.DoesNotContain(report.PolicyViolations, violation => violation.Route == "/unprotected");

        AuthSurfaceEndpoint dynamic = Find(report, "/dynamic", "GET");
        Assert.Contains(dynamic.Requirements, requirement => requirement.EndsWith("|opaque", StringComparison.Ordinal));
        Assert.Equal(64, dynamic.RequirementFingerprint.Length);

        AuthSurfaceEndpoint direct = Find(report, "/direct-policy", "GET");
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, direct.AuthorizationKind);
        Assert.Contains(direct.Requirements, requirement => requirement.Contains("scope", StringComparison.Ordinal));

        AuthSurfaceEndpoint controller = Find(report, "/controller/protected/{id:int}", "GET");
        Assert.Contains("Admin", controller.Roles);
        Assert.Contains("Bearer", controller.AuthenticationSchemes);

        Assert.Equal(
            AuthSurfaceAuthorizationKind.ExplicitAnonymous,
            Find(report, "/controller/anonymous", "GET").AuthorizationKind);
    }

    [Fact]
    public async Task GenericHostStartupStrategyAlsoExposesRuntimeEndpoints()
    {
        Environment.SetEnvironmentVariable("AUTHSURFACE_FIXTURE_FALLBACK", "true");
        using IHost host = FixtureHost.BuildGenericHost(fallbackPolicy: true);
        await host.StartAsync();

        AuthSurfaceReport report = await ScanAsync(host.Services);

        Assert.NotEmpty(report.Endpoints);
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, Find(report, "/group/item", "GET").AuthorizationKind);
    }

    [Fact]
    public async Task MultiMethodEndpointUsesStableMethodSpecificRecords()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await ScanAsync(app.Services);

        Assert.Equal(
            ["GET", "POST"],
            report.Endpoints.Where(endpoint => endpoint.Route == "/multi")
                .SelectMany(static endpoint => endpoint.Methods)
                .OrderBy(static method => method, StringComparer.Ordinal));
    }

    [Fact]
    public async Task NoFallbackPolicyMakesUnprotectedEndpointFailClosed()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: false);
        await app.StartAsync();
        AuthSurfaceReport report = await ScanAsync(app.Services);

        Assert.Equal(AuthSurfaceAuthorizationKind.Unprotected, Find(report, "/unprotected", "GET").AuthorizationKind);
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.VerifyPolicy(report);
        Assert.Contains(result.Violations, violation => violation.Code == "unprotected-endpoint");
        Assert.Throws<AuthSurfaceVerificationException>(result.AssertValid);
    }

    [Fact]
    public async Task ExplicitExclusionsRemoveIntentionalInfrastructureEndpoints()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: false);
        await app.StartAsync();
        AuthSurfaceScanOptions options = new(["/unprotected"]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync(options);

        Assert.DoesNotContain(report.Endpoints, endpoint => endpoint.Route == "/unprotected");
        Assert.DoesNotContain(report.PolicyViolations, violation => violation.Route == "/unprotected");
    }

    private static async Task<AuthSurfaceReport> ScanAsync(IServiceProvider services)
    {
        return await new AuthSurfaceScanner(services).ScanAsync();
    }

    private static AuthSurfaceEndpoint Find(AuthSurfaceReport report, string route, string method) =>
        Assert.Single(report.Endpoints.Where(endpoint => endpoint.Route == route && endpoint.Methods.Contains(method)));
}
