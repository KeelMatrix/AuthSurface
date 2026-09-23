using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

#pragma warning disable ASP0022
public sealed class IdentityContractTests
{
    [Fact]
    public async Task RealHostLiteralCasingDuplicateFailsWithStructuredIdentityError()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/Case", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/case", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(app.Services).ScanAsync());

        Assert.Equal("duplicate-endpoint-identity", exception.Code);
    }

    [Fact]
    public async Task RealHostParameterNameCasingDuplicateFailsWithStructuredIdentityError()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/items/{id}", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/items/{ID}", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(app.Services).ScanAsync());

        Assert.Equal("duplicate-endpoint-identity", exception.Code);
    }

    [Fact]
    public async Task RealHostGenuinelyDifferentRoutesRemainDistinct()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/alpha", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/beta", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();

        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/alpha");
        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/beta");
    }

    [Fact]
    public async Task ConstraintArgumentsRemainDistinctInCanonicalIdentity()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/items/{id:regex(^\\d+$)}", () => Results.Ok()).AllowAnonymous();
            application.MapGet("/items/{id:regex(^\\D+$)}", () => Results.Ok()).AllowAnonymous();
        });
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();

        Assert.Equal(2, report.Endpoints.Count(endpoint => endpoint.Route.StartsWith("/items/", StringComparison.Ordinal)));
        Assert.Empty(report.PolicyViolations);
    }

    [Fact]
    public async Task ProgrammaticPatternWithoutRawTextUsesCorrectCatchAllRendering()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("files")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "path",
                        null!,
                        RoutePatternParameterKind.CatchAll),
                ]),
            ]);
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new AllowAnonymousAttribute());
        var source = new DefaultEndpointDataSource([(RouteEndpoint)builder.Build()]);

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [source],
            new AllowingPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
        Assert.Equal("/files/{*path}", endpoint.Route);
    }

    [Fact]
    public async Task ProgrammaticParameterPoliciesRemainDistinctInCanonicalIdentity()
    {
        RoutePattern firstPattern = ProgrammaticPattern(new RegexRouteConstraint("^\\d+$"));
        RoutePattern secondPattern = ProgrammaticPattern(new RegexRouteConstraint("^\\D+$"));
        var source = new DefaultEndpointDataSource([
            BuildEndpoint(firstPattern),
            BuildEndpoint(secondPattern),
        ]);

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [source],
            new AllowingPolicyProvider()).ScanAsync();

        Assert.Equal(2, report.Endpoints.Count);
        Assert.NotEqual(report.Endpoints[0].Route, report.Endpoints[1].Route);
    }

    [Fact]
    public async Task SupportedProgrammaticParameterPolicyRoundTripsToStableRouteIdentity()
    {
        RoutePattern pattern = ProgrammaticPattern(new IntRouteConstraint());
        var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [source],
            new AllowingPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
        Assert.Equal("/items/{id:int}", endpoint.Route);

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(report);
        Assert.Equal(endpoint.Route, Assert.Single(baseline.Endpoints).Route);
    }

    [Fact]
    public async Task UnsupportedProgrammaticParameterPolicyFailsClosed()
    {
        RoutePattern pattern = ProgrammaticPattern(new UnsupportedParameterPolicy());
        var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("unsupported-parameter-policy", exception.Code);
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(UnsupportedParameterPolicy), exception.Message, StringComparison.Ordinal);
    }

    private static RoutePattern ProgrammaticPattern(IParameterPolicy policy) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null!,
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(policy)]),
                ]),
            ]);

    private static RouteEndpoint BuildEndpoint(RoutePattern pattern)
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new AllowAnonymousAttribute());
        return (RouteEndpoint)builder.Build();
    }

    private static WebApplication BuildApplication(Action<WebApplication> configureEndpoints)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FixtureHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAuthorization();
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        configureEndpoints(app);
        return app;
    }

    private sealed class AllowingPolicyProvider : IAuthorizationPolicyProvider
    {
        public bool AllowsCachingPolicies => false;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
            Task.FromResult(new AuthorizationPolicyBuilder().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() =>
            Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            Task.FromResult<AuthorizationPolicy?>(null);
    }

    private sealed class UnsupportedParameterPolicy : IParameterPolicy
    {
    }
}
#pragma warning restore ASP0022
