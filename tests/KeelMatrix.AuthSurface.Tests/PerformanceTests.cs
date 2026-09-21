using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public async Task BoundedSyntheticFixtureEnumeratesEachDataSourceOnce()
    {
        const int endpointCount = 2_000;
        var endpoints = new List<Endpoint>(endpointCount);
        for (int index = 0; index < endpointCount; index++)
        {
            var builder = new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse($"/synthetic/{index}"),
                index);
            builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
            endpoints.Add(builder.Build());
        }

        var source = new CountingEndpointDataSource(endpoints);
        var scanner = new AuthSurfaceScanner([source], new NullPolicyProvider());
        AuthSurfaceReport report = await scanner.ScanAsync();

        Assert.Equal(endpointCount, report.Endpoints.Count);
        Assert.Equal(1, source.ReadCount);
    }

    private sealed class CountingEndpointDataSource(IReadOnlyList<Endpoint> endpoints) : EndpointDataSource
    {
        public int ReadCount { get; private set; }

        public override IReadOnlyList<Endpoint> Endpoints
        {
            get
            {
                ReadCount++;
                return endpoints;
            }
        }

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
    }

    private sealed class NullPolicyProvider : IAuthorizationPolicyProvider
    {
        public bool AllowsCachingPolicies => true;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(new AuthorizationPolicyBuilder().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(null);
    }
}
