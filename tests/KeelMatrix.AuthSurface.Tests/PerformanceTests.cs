using System.Globalization;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
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

    [Fact]
    public async Task CumulativeRoutePolicyWorkAcrossIndividuallyValidEndpointsFailsClosed()
    {
        string policy = "regex(" + new string('a', 7_000) + ")";
        var endpoints = Enumerable.Range(0, 8)
            .Select(index => BuildAnonymousEndpoint($"/cumulative/{index}/{IdPolicy(policy)}"))
            .ToArray();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource(endpoints)],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-complex", exception.Code);
        Assert.Contains("scan-wide", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodExpansionMultipliesRoutePolicyWorkWithinOneEndpoint()
    {
        string policy = "regex(" + new string('a', 3_000) + ")";
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse($"/method-complexity/{{id:{policy}}}"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(
            Enumerable.Range(0, 50).Select(static index => "M" + index.ToString("D2", CultureInfo.InvariantCulture))));
        builder.Metadata.Add(new AllowAnonymousAttribute());

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([(RouteEndpoint)builder.Build()])],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-complex", exception.Code);
        Assert.Contains("scan-wide", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgrammaticHttpMethodConstraintExpansionCountsAcrossEndpoints()
    {
        var allowedMethods = Enumerable.Repeat("M", 10_000).ToArray();
        var endpoints = Enumerable.Range(0, 6)
            .Select(index => BuildProgrammaticAnonymousEndpoint(
                index,
                new HttpMethodRouteConstraint(allowedMethods)))
            .ToArray();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource(endpoints)],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-complex", exception.Code);
        Assert.Contains("scan-wide", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CumulativeAuthorizationMetadataAcrossIndividuallyValidEndpointsFailsClosed()
    {
        int metadataPerEndpoint = AuthSurfaceCanonicalizer.MaximumMetadataItems / 2 + 1;
        var endpoints = Enumerable.Range(0, 2)
            .Select(index => BuildEndpointWithMetadata($"/metadata/{index}", metadataPerEndpoint))
            .ToArray();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource(endpoints)],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
        Assert.Contains("cumulative authorization metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CumulativeRequirementDataExpansionFailsBeforePerEndpointLimits()
    {
        const int requirementsPerEndpoint = 5_000;
        var endpoints = Enumerable.Range(0, 2)
            .Select(index => BuildEndpointWithRequirementData($"/requirement-data/{index}", requirementsPerEndpoint))
            .ToArray();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource(endpoints)],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
        Assert.Contains("cumulative requirement-data expansion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequirementDataExpansionRejectsAtScanBoundaryBeforeMaterializingOverflow()
    {
        var data = new CountingRequirementData(AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements + 1);
        RouteEndpoint endpoint = BuildEndpointWithRequirementData("/requirement-data-boundary", data);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([endpoint])],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
        Assert.Equal(AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements, data.MaterializedCount);
    }

    [Fact]
    public async Task RequirementDataExactlyAtScanBoundaryIsAccepted()
    {
        var data = new CountingRequirementData(AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements);
        RouteEndpoint endpoint = BuildEndpointWithRequirementData("/requirement-data-exact-boundary", data);

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([endpoint])],
            new NullPolicyProvider()).ScanAsync();

        Assert.Equal(AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements, data.MaterializedCount);
        Assert.Equal(AuthSurfaceCanonicalizer.MaximumScanRequirementDataRequirements, Assert.Single(report.Endpoints).Requirements.Count);
    }

    [Fact]
    public async Task RequirementDataExpansionAcrossMetadataObjectsStopsAtAggregateBoundary()
    {
        var first = new CountingRequirementData(4_096);
        var second = new CountingRequirementData(4_097);
        RouteEndpoint endpoint = BuildEndpointWithRequirementData("/requirement-data/metadata", first, second);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([endpoint])],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
        Assert.Equal(4_096, first.MaterializedCount);
        Assert.Equal(4_096, second.MaterializedCount);
    }

    [Fact]
    public async Task RequirementDataExpansionAcrossEndpointsStopsAtAggregateBoundary()
    {
        var first = new CountingRequirementData(4_096);
        var second = new CountingRequirementData(4_097);
        RouteEndpoint firstEndpoint = BuildEndpointWithRequirementData("/requirement-data/first", first);
        RouteEndpoint secondEndpoint = BuildEndpointWithRequirementData("/requirement-data/second", second);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([firstEndpoint, secondEndpoint])],
                new NullPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
        Assert.Equal(4_096, first.MaterializedCount);
        Assert.Equal(4_096, second.MaterializedCount);
    }

    [Fact]
    public async Task RequirementDataCancellationIsObservedBetweenYields()
    {
        using var cancellation = new CancellationTokenSource();
        var data = new CancellingRequirementData(cancellation, cancelAfter: 100);
        RouteEndpoint endpoint = BuildEndpointWithRequirementData("/requirement-data/cancel", data);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([endpoint])],
                new NullPolicyProvider()).ScanAsync(cancellationToken: cancellation.Token));

        Assert.Equal(100, data.MaterializedCount);
    }

    [Fact]
    public void ASingleRouteStillHonorsThePerRoutePolicyExpressionLimit()
    {
        string policy = "regex(" + new string('a', AuthSurfaceCanonicalizer.MaximumRoutePolicyLength) + ")";
        RouteEndpoint endpoint = BuildAnonymousEndpoint($"/per-route/{{id:{policy}}}");

        AuthSurfaceAnalysisException exception = Assert.Throws<AuthSurfaceAnalysisException>(
            () => AuthSurfaceCanonicalizer.CanonicalIdentity(endpoint.RoutePattern, "GET"));

        Assert.Equal("route-policy-too-complex", exception.Code);
        Assert.DoesNotContain("scan-wide", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsObservedBetweenCumulativeExpansions()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new CancellingEndpointDataSource(
            [
                BuildAnonymousEndpoint("/cancel/first"),
                BuildAnonymousEndpoint("/cancel/second"),
            ],
            cancellation);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await new AuthSurfaceScanner([source], new NullPolicyProvider())
                .ScanAsync(cancellationToken: cancellation.Token));
    }

    private static string IdPolicy(string policy) => $"{{id:{policy}}}";

    private static RouteEndpoint BuildAnonymousEndpoint(
        string route,
        IParameterPolicy? parameterPolicy = null)
    {
        RoutePattern pattern = parameterPolicy is null
            ? RoutePatternFactory.Parse(route)
            : RoutePatternFactory.Pattern(
                rawText: null!,
                segments:
                [
                    RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart(route.Trim('/'))]),
                    RoutePatternFactory.Segment([
                        RoutePatternFactory.ParameterPart(
                            "id",
                            null!,
                            RoutePatternParameterKind.Standard,
                            [RoutePatternFactory.ParameterPolicy(parameterPolicy)]),
                    ]),
                ]);
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new AllowAnonymousAttribute());
        return (RouteEndpoint)builder.Build();
    }

    private static RouteEndpoint BuildEndpointWithMetadata(string route, int metadataCount)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        for (int index = 0; index < metadataCount; index++)
        {
            builder.Metadata.Add(new AllowAnonymousAttribute());
        }

        return (RouteEndpoint)builder.Build();
    }

    private static RouteEndpoint BuildEndpointWithRequirementData(string route, int requirementCount)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new ManyRequirementData(requirementCount));
        return (RouteEndpoint)builder.Build();
    }

    private static RouteEndpoint BuildEndpointWithRequirementData(
        string route,
        params IAuthorizationRequirementData[] data)
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(route),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        foreach (IAuthorizationRequirementData item in data)
        {
            builder.Metadata.Add(item);
        }
        return (RouteEndpoint)builder.Build();
    }

    private static RouteEndpoint BuildProgrammaticAnonymousEndpoint(
        int index,
        IParameterPolicy parameterPolicy)
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("programmatic-methods")]),
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart(index.ToString(CultureInfo.InvariantCulture))]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null!,
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(parameterPolicy)]),
                ]),
            ]);
        var builder = new RouteEndpointBuilder(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new AllowAnonymousAttribute());
        return (RouteEndpoint)builder.Build();
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

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() =>
            Task.FromResult(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(null);
    }

    private sealed class ManyRequirementData(int count) : IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements()
        {
            for (int index = 0; index < count; index++)
            {
                yield return new MarkerRequirement();
            }
        }
    }

    private sealed class CountingRequirementData(int count) : IAuthorizationRequirementData
    {
        public int MaterializedCount { get; private set; }

        public IEnumerable<IAuthorizationRequirement> GetRequirements()
        {
            for (int index = 0; index < count; index++)
            {
                yield return new MarkerRequirement();
                MaterializedCount++;
            }
        }
    }

    private sealed class CancellingRequirementData(
        CancellationTokenSource cancellation,
        int cancelAfter) : IAuthorizationRequirementData
    {
        public int MaterializedCount { get; private set; }

        public IEnumerable<IAuthorizationRequirement> GetRequirements()
        {
            for (int index = 0; ; index++)
            {
                yield return new MarkerRequirement();
                MaterializedCount++;
                if (MaterializedCount == cancelAfter)
                {
                    cancellation.Cancel();
                }
            }
        }
    }

    private sealed class MarkerRequirement : IAuthorizationRequirement
    {
    }

    private sealed class CancellingEndpointDataSource(
        IReadOnlyList<Endpoint> endpoints,
        CancellationTokenSource cancellation) : EndpointDataSource
    {
        public override IReadOnlyList<Endpoint> Endpoints => new CancellingEndpointList(endpoints, cancellation);

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() =>
            new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
    }

    private sealed class CancellingEndpointList(
        IReadOnlyList<Endpoint> endpoints,
        CancellationTokenSource cancellation) : IReadOnlyList<Endpoint>
    {
        public int Count => endpoints.Count;

        public Endpoint this[int index] => endpoints[index];

        public IEnumerator<Endpoint> GetEnumerator()
        {
            yield return endpoints[0];
            cancellation.Cancel();
            for (int index = 1; index < endpoints.Count; index++)
            {
                yield return endpoints[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
