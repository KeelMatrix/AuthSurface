using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class BaselineTests
{
    [Fact]
    public async Task BaselineIsDeterministicAndComparisonDoesNotRewriteOnFailure()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();

        using TemporaryDirectory directory = new();
        string first = Path.Combine(directory.Path, "one", "authsurface.json");
        string second = Path.Combine(directory.Path, "two", "authsurface.json");
        AuthSurfaceBaseline.Create(report, first, overwrite: false);
        AuthSurfaceBaseline.Create(report, second, overwrite: false);
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        byte[] before = File.ReadAllBytes(first);
        await using var changedApp = FixtureHost.BuildWebApplication(fallbackPolicy: false);
        await changedApp.StartAsync();
        AuthSurfaceReport changed = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(changed, first);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, violation => violation.Code == "endpoint-classification-changed");
        Assert.Equal(before, File.ReadAllBytes(first));
    }

    [Fact]
    public async Task SchemaAndMalformedBaselineFailuresAreStructured()
    {
        await using var app = FixtureHost.BuildWebApplication(fallbackPolicy: true);
        await app.StartAsync();
        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");

        File.WriteAllText(path, "{\"schemaVersion\":99,\"endpoints\":[]}");
        AuthSurfaceBaselineException future = Assert.Throws<AuthSurfaceBaselineException>(() => AuthSurfaceBaseline.Read(path));
        Assert.Equal("baseline-schema-unsupported", future.Code);

        File.WriteAllText(path, "not json");
        AuthSurfaceBaselineException malformed = Assert.Throws<AuthSurfaceBaselineException>(() => AuthSurfaceBaseline.Read(path));
        Assert.Equal("baseline-malformed", malformed.Code);

        File.WriteAllText(path, "{}");
        Assert.Equal("baseline-schema-unsupported", Assert.Throws<AuthSurfaceBaselineException>(() => AuthSurfaceBaseline.Read(path)).Code);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task DuplicateCanonicalIdentityFailsRatherThanDroppingAnEndpoint()
    {
        RouteEndpoint first = BuildEndpoint("/duplicate", "GET");
        RouteEndpoint second = BuildEndpoint("/duplicate/", "get");
        var source = new DefaultEndpointDataSource([first, second]);
        var provider = new TestPolicyProvider();
        AuthSurfaceScanner scanner = new([source], provider);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(async () => await scanner.ScanAsync());
        Assert.Equal("duplicate-endpoint-identity", exception.Code);
    }

    private static RouteEndpoint BuildEndpoint(string route, string method)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata([method]));
        return (RouteEndpoint)builder.Build();
    }

    private sealed class TestPolicyProvider : IAuthorizationPolicyProvider
    {
        public bool AllowsCachingPolicies => true;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(new AuthorizationPolicyBuilder().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "authsurface-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
