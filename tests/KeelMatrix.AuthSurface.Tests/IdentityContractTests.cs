using AuthSurface.FixtureApp;
using System.Globalization;
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
    public void BoundedCanonicalRepresentationNormalizesDocumentedEquivalentForms()
    {
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:length(3)}", "get"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:length(3,3)}", "GET"));
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:INT}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET"));
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:httpmethod(post,GET,POST)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:httpMethod(GET,POST)}", "GET"));
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$;options=0)}", "GET"));

        string programmaticInt = AuthSurfaceCanonicalizer.NormalizeRoute(ProgrammaticPattern(new IntRouteConstraint()));
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(programmaticInt, "GET"));
        string programmaticRegex = AuthSurfaceCanonicalizer.NormalizeRoute(
            ProgrammaticPattern(new RegexRouteConstraint(
                new System.Text.RegularExpressions.Regex("^\\d+$", System.Text.RegularExpressions.RegexOptions.None))));
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(programmaticRegex, "GET"));
    }

    [Fact]
    public void BoundedCanonicalRepresentationPreservesDocumentedDistinctForms()
    {
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:length(3,3)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:length(3,4)}", "GET"));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\D+$)}", "GET"));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "POST"));
    }

    [Fact]
    public void CompositeConstraintChildOrderIsCanonicalizedWithoutCollapsingDistinctArguments()
    {
        string first = AuthSurfaceCanonicalizer.NormalizeRoute(
            ProgrammaticPattern(new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)])));
        string reordered = AuthSurfaceCanonicalizer.NormalizeRoute(
            ProgrammaticPattern(new CompositeRouteConstraint([new MinRouteConstraint(2), new IntRouteConstraint()])));
        string changed = AuthSurfaceCanonicalizer.NormalizeRoute(
            ProgrammaticPattern(new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(3)])));

        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity(first, "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(reordered, "GET"));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity(first, "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(changed, "GET"));
    }

    [Fact]
    public async Task SupportedProgrammaticParameterPoliciesRoundTripThroughPersistedBaseline()
    {
        using var directory = new TemporaryDirectory();
        var renderedRoutes = new List<string>();

        foreach (AuthSurfaceParameterPolicyContract contract in AuthSurfaceParameterPolicyMatrix.Contracts)
        {
            foreach (Func<IParameterPolicy> variant in contract.Variants)
            {
                RoutePattern pattern = ProgrammaticPattern(variant());
                var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);
                AuthSurfaceReport report = await new AuthSurfaceScanner(
                    [source],
                    new AllowingPolicyProvider()).ScanAsync();

                AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
                renderedRoutes.Add(endpoint.Route);
                string path = Path.Combine(
                    directory.Path,
                    contract.RuntimeType.Name,
                    renderedRoutes.Count.ToString(CultureInfo.InvariantCulture));
                AuthSurfaceBaseline.Create(report, path, overwrite: false);
                AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);
                AuthSurfaceEndpoint persisted = Assert.Single(roundTrip.Endpoints);

                Assert.Equal(endpoint.Route, persisted.Route);
                Assert.Equal(endpoint.Identity, persisted.Identity);
                Assert.Equal(endpoint.Requirements, persisted.Requirements);
                Assert.Equal(endpoint.RequirementFingerprint, persisted.RequirementFingerprint);
                Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
            }
        }

        Assert.Equal(renderedRoutes.Count, renderedRoutes.Distinct(StringComparer.Ordinal).Count());
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

    [Fact]
    public async Task UnsupportedNestedProgrammaticParameterPolicyFailsClosed()
    {
        RoutePattern pattern = ProgrammaticPattern(new CompositeRouteConstraint([new UnsupportedRouteConstraint()]));
        var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("unsupported-parameter-policy", exception.Code);
        Assert.Contains(nameof(UnsupportedRouteConstraint), exception.Message, StringComparison.Ordinal);
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

    private sealed class UnsupportedRouteConstraint : IRouteConstraint
    {
        public bool Match(
            Microsoft.AspNetCore.Http.HttpContext? httpContext,
            Microsoft.AspNetCore.Routing.IRouter? route,
            string routeKey,
            RouteValueDictionary values,
            RouteDirection routeDirection) => true;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "authsurface-parameter-policy-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
#pragma warning restore ASP0022
