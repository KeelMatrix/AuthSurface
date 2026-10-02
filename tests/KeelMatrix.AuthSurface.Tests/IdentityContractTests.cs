using System.Globalization;
using System.Text;
using System.Text.Json;
using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

    [Theory]
    [MemberData(nameof(ReservedProgrammaticMarkerCases))]
    public void ReservedProgrammaticMarkerNeverAliasesItsRuntimePolicy(
        string textualPolicy,
        IParameterPolicy runtimePolicy)
    {
        string textualIdentity = AuthSurfaceCanonicalizer.CanonicalIdentity(
            TextualPattern(textualPolicy),
            "GET");
        string programmaticIdentity = AuthSurfaceCanonicalizer.CanonicalIdentity(
            ProgrammaticPattern(runtimePolicy),
            "GET");

        Assert.Contains("text:programmatic:", textualIdentity, StringComparison.Ordinal);
        Assert.Contains("programmatic:", programmaticIdentity, StringComparison.Ordinal);
        Assert.NotEqual(textualIdentity, programmaticIdentity);
    }

    [Fact]
    public async Task ReservedProgrammaticMarkerProvenanceChangeIsAReadableBaselineDiff()
    {
        RoutePattern textualPattern = TextualPattern("programmatic:int");
        RoutePattern runtimePattern = ProgrammaticPattern(new IntRouteConstraint());
        var textualSource = new DefaultEndpointDataSource([BuildEndpoint(textualPattern)]);
        var runtimeSource = new DefaultEndpointDataSource([BuildEndpoint(runtimePattern)]);

        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(
            [textualSource],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceReport currentReport = await new AuthSurfaceScanner(
            [runtimeSource],
            new AllowingPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(baselineReport.Endpoints);
        Assert.Equal(baselineEndpoint.Route, Assert.Single(currentReport.Endpoints).Route);
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(baselineReport, path, overwrite: false);

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(currentReport, path);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, violation => violation.Code == "endpoint-added");
        Assert.Contains(result.Violations, violation => violation.Code == "endpoint-removed");
    }

    [Fact]
    public void RawTextDoesNotDiscardMergedDefaultsOrParameterPolicies()
    {
        RoutePattern first = RoutePatternFactory.Parse(
            "/items/{id}",
            new RouteValueDictionary(new { id = "first" }),
            new { id = new IntRouteConstraint() });
        RoutePattern second = RoutePatternFactory.Parse(
            "/items/{id}",
            new RouteValueDictionary(new { id = "second" }),
            new { id = new LongRouteConstraint() });

        Assert.Equal(first.RawText, second.RawText);
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.NormalizeRoute(first),
            AuthSurfaceCanonicalizer.NormalizeRoute(second));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity(first, "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(second, "GET"));
    }

    [Fact]
    public async Task LiteralBracesAndParameterSegmentsHaveDistinctIdentities()
    {
        var source = new DefaultEndpointDataSource([
            BuildEndpoint(RoutePatternFactory.Parse("/literal/{{id}}")),
            BuildEndpoint(RoutePatternFactory.Parse("/literal/{id}")),
        ]);

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [source],
            new AllowingPolicyProvider()).ScanAsync();

        Assert.Equal(2, report.Endpoints.Count);
        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/literal/{{id}}" && endpoint.Methods[0] == "GET");
        Assert.Contains(report.Endpoints, endpoint => endpoint.Route == "/literal/{id}" && endpoint.Methods[0] == "GET");
    }

    [Fact]
    public async Task ProgrammaticRegexQuantifierIsEscapedAndRoundTripsThroughBaseline()
    {
        RoutePattern pattern = ProgrammaticPattern(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
            "^\\d{1,3}$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled)));
        var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [source],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
        Assert.Contains("programmatic:regex64(", endpoint.Route, StringComparison.Ordinal);

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

        Assert.Equal(endpoint.Route, Assert.Single(roundTrip.Endpoints).Route);
        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
    }

    [Fact]
    public async Task ProgrammaticDefaultWithQuestionMarkRoundTripsThroughPersistedIdentity()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        "x?",
                        RoutePatternParameterKind.Standard),
                ]),
            ]);
        var source = new DefaultEndpointDataSource([BuildEndpoint(pattern)]);
        AuthSurfaceReport report = await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);

        string json = File.ReadAllText(path);
        Assert.Contains("\"identity\"", json, StringComparison.Ordinal);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

        Assert.Equal(Assert.Single(report.Endpoints).Identity, Assert.Single(roundTrip.Endpoints).Identity);
        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
    }

    [Theory]
    [InlineData("foo=bar", "baz", "/items/{id:foo=bar:baz}")]
    [InlineData("foo:bar", null, "/items/{id:foo:bar}")]
    public async Task ProgrammaticContentPolicyPayloadsRoundTripThroughPersistedIdentity(
        string firstPolicy,
        string? secondPolicy,
        string expectedRoute)
    {
        var policies = new List<RoutePatternParameterPolicyReference>
        {
            RoutePatternFactory.ParameterPolicy(firstPolicy),
        };
        if (secondPolicy is not null)
        {
            policies.Add(RoutePatternFactory.ParameterPolicy(secondPolicy));
        }

        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null!,
                        RoutePatternParameterKind.Standard,
                        policies),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
        Assert.Equal(expectedRoute, endpoint.Route);

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
    }

    [Fact]
    public async Task ProgrammaticRegexDelimiterPayloadRoundTripsThroughPersistedIdentity()
    {
        RoutePattern pattern = ProgrammaticPattern(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
            "[)]:payload",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled)));
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
        Assert.Equal("/items/{id:programmatic:regex64(WyldOnBheWxvYWQ;options=521)}", endpoint.Route);

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
    }

    [Fact]
    public async Task ProgrammaticRegexDelimiterPayloadRejectsStaleMarkerMutationWithoutRewriting()
    {
        RoutePattern pattern = ProgrammaticPattern(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
            "[)]:payload",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled)));
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string mutatedIdentity = RewritePersistedIdentity(
            persistedIdentity,
            identity => ReplaceStructuralRoute(
                identity,
                "/items/{id:programmatic:regex64(WyldOnBheWxvYWQ;options=521:text:)}"));
        File.WriteAllText(path, json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PersistedIdentityBindingRoundTripsGeneratedDelimiterContextFamily()
    {
        RoutePattern[] patterns =
        [
            ProgrammaticContentPoliciesPattern("foo=bar", "baz"),
            ProgrammaticContentPoliciesPattern("foo:bar"),
            ProgrammaticContentPoliciesPattern("foo:text:bar", "programmatic:int"),
            ProgrammaticContentPoliciesPattern("regex([)]:payload)"),
            ProgrammaticPattern(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                "[()]:=payload",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                System.Text.RegularExpressions.RegexOptions.Compiled))),
            ProgrammaticPatternWithDefault(
                new OptionalRouteConstraint(new CompositeRouteConstraint([
                    new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                        "[)]:payload",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                        System.Text.RegularExpressions.RegexOptions.Compiled)),
                    new MinRouteConstraint(2),
                ])),
                "Default/with:slash"),
            CatchAllPattern("x?/foo:bar"),
            MultipleParameterDelimiterPattern(),
        ];

        using var directory = new TemporaryDirectory();
        int index = 0;
        foreach (RoutePattern pattern in patterns)
        {
            AuthSurfaceReport report = await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
                new AllowingPolicyProvider()).ScanAsync();
            AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
            string path = Path.Combine(directory.Path, (++index).ToString(CultureInfo.InvariantCulture), "authsurface.json");

            AuthSurfaceBaseline.Create(report, path, overwrite: false);
            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

            Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid, endpoint.Route);
            Assert.Equal(endpoint.Identity, Assert.Single(roundTrip.Endpoints).Identity);
        }
    }

    [Fact]
    public async Task PersistedIdentityRejectsTextMarkerInjectedIntoProgrammaticDefault()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        "x?:foo",
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(new IntRouteConstraint())]),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string mutatedIdentity = RewritePersistedIdentity(
            persistedIdentity,
            identity => ReplaceStructuralRoute(identity, "/items/{id:programmatic:int=x?:text:foo}"));
        File.WriteAllText(path, json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("policy-token")]
    [InlineData("regex-policy-token")]
    [InlineData("composite-policy-token")]
    [InlineData("optional-policy-token")]
    [InlineData("default")]
    [InlineData("catch-all-default")]
    [InlineData("slash-default")]
    [InlineData("literal-marker-default")]
    [InlineData("mixed-case-policy-token")]
    [InlineData("regex-payload")]
    [InlineData("nested-payload")]
    [InlineData("escaped-brace-payload")]
    public async Task PersistedIdentityRejectsTextMarkerInjectionFamilyWithoutRewriting(string mutationCase)
    {
        RoutePattern pattern = mutationCase switch
        {
            "policy-token" => ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?"),
            "regex-policy-token" => ProgrammaticPatternWithDefault(ProgrammaticRegex(), "x?"),
            "composite-policy-token" => ProgrammaticPatternWithDefault(
                new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
                "x?"),
            "optional-policy-token" => ProgrammaticPatternWithDefault(
                new OptionalRouteConstraint(new IntRouteConstraint()),
                "x?"),
            "default" => ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?:foo"),
            "catch-all-default" => CatchAllPattern("x?/foo"),
            "slash-default" => ProgrammaticPatternWithDefault(new IntRouteConstraint(), "foo/bar"),
            "literal-marker-default" => ProgrammaticPatternWithDefault(new IntRouteConstraint(), "literal:text:marker"),
            "mixed-case-policy-token" => ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?"),
            "regex-payload" => ProgrammaticPatternWithDefault(ProgrammaticRegex(), "x?"),
            "nested-payload" => ProgrammaticPatternWithDefault(
                new OptionalRouteConstraint(new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)])),
                "x?"),
            "escaped-brace-payload" => ProgrammaticPatternWithDefault(
                new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                    "^\\d{1,3}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                    System.Text.RegularExpressions.RegexOptions.Compiled)),
                "x?"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutationCase)),
        };

        await AssertPersistedIdentityMutationRejected(
            pattern,
            structuralRoute => ApplyMarkerMutation(structuralRoute, mutationCase));
    }

    [Fact]
    public async Task AcceptedUnparseableRepresentationsRoundTripTheirExactWriterTokens()
    {
        foreach (RoutePattern pattern in new[]
        {
            ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?"),
            ProgrammaticPatternWithDefault(ProgrammaticRegex(), "x?"),
            ProgrammaticPatternWithDefault(
                new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
                "x?"),
            ProgrammaticPatternWithDefault(new OptionalRouteConstraint(new IntRouteConstraint()), "x?"),
            CatchAllPattern("x?/foo"),
            ProgrammaticPatternWithDefault(new IntRouteConstraint(), "foo/bar"),
            ProgrammaticPatternWithDefault(new IntRouteConstraint(), "literal:text:marker"),
            ProgrammaticPatternWithDefault(
                new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                    "^\\d{1,3}$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                    System.Text.RegularExpressions.RegexOptions.Compiled)),
                "x?"),
        })
        {
            using var directory = new TemporaryDirectory();
            string path = Path.Combine(directory.Path, "authsurface.json");
            AuthSurfaceReport report = await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
                new AllowingPolicyProvider()).ScanAsync();

            AuthSurfaceBaseline.Create(report, path, overwrite: false);
            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

            Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
            Assert.Equal(Assert.Single(report.Endpoints).Identity, Assert.Single(roundTrip.Endpoints).Identity);
        }
    }

    [Fact]
    public async Task ProgrammaticPolicyAndDefaultRoundTripThroughPersistedIdentity()
    {
        foreach (IParameterPolicy policy in new IParameterPolicy[]
        {
            new IntRouteConstraint(),
            new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                "^\\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                System.Text.RegularExpressions.RegexOptions.Compiled)),
        })
        {
            RoutePattern pattern = RoutePatternFactory.Pattern(
                rawText: null!,
                segments:
                [
                    RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                    RoutePatternFactory.Segment([
                        RoutePatternFactory.ParameterPart(
                            "id",
                            "x?",
                            RoutePatternParameterKind.Standard,
                            [RoutePatternFactory.ParameterPolicy(policy)]),
                    ]),
                ]);
            AuthSurfaceReport report = await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
                new AllowingPolicyProvider()).ScanAsync();

            using var directory = new TemporaryDirectory();
            string path = Path.Combine(directory.Path, "authsurface.json");
            AuthSurfaceBaseline.Create(report, path, overwrite: false);
            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

            Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
            Assert.Equal(Assert.Single(report.Endpoints).Identity, Assert.Single(roundTrip.Endpoints).Identity);
        }
    }

    [Theory]
    [InlineData("/other/{id}")]
    [InlineData("/items/{id=y?}")]
    [InlineData("/items/{id:programmatic:long=x?}")]
    [InlineData("/items/{id:programmatic:regex(^\\D+$;options=521)=x?}")]
    public async Task PersistedIdentityRejectsChangedUnparseableStructuralRouteWithoutRewriting(string replacementRoute)
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        "x?",
                        RoutePatternParameterKind.Standard),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string mutatedIdentity = RewritePersistedIdentity(
            persistedIdentity,
            identity => ReplaceStructuralRoute(identity, replacementRoute));
        File.WriteAllText(path, json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PersistedIdentityRejectsMethodMutationInsideUnparseableStructuralTokenWithoutRewriting()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart("id", "x?", RoutePatternParameterKind.Standard),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string mutatedIdentity = RewritePersistedIdentity(
            persistedIdentity,
            identity => identity[..identity.LastIndexOf('\u001f')] + "\u001fPOST");
        File.WriteAllText(path, json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task PersistedIdentityRejectsMethodKeyDisagreementWithoutRewriting()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart("id", "x?", RoutePatternParameterKind.Standard),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        byte[] before = File.ReadAllBytes(path);
        string mutated = File.ReadAllText(path).Replace("\"GET\"", "\"POST\"", StringComparison.Ordinal);
        File.WriteAllText(path, mutated);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.NotEmpty(before);
        Assert.Equal(mutated, File.ReadAllText(path));
    }

    [Fact]
    public void BaselineRejectsDuplicateRouteMethodRecordsForUnparseableRoute()
    {
        string fingerprint = AuthSurfaceCanonicalizer.Fingerprint([]);
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("same")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart("id", "x?", RoutePatternParameterKind.Standard),
                ]),
            ]);
        string canonicalIdentity = AuthSurfaceCanonicalizer.CanonicalIdentity(pattern, "GET");
        string firstIdentity = AuthSurfaceCanonicalizer.CreatePersistedIdentity(
            pattern,
            "/same/{id=x?}",
            "GET",
            canonicalIdentity);
        string endpoint(string identity) =>
            "{\"route\":\"/same/{id=x?}\",\"identity\":\"" + identity + "\",\"methods\":[\"GET\"]," +
            "\"authorization\":\"ExplicitAnonymous\",\"policies\":[],\"roles\":[],\"schemes\":[]," +
            "\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false,\"requirements\":[]," +
            "\"requirementFingerprint\":\"" + fingerprint + "\"}";

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"endpoints\":[" + endpoint(firstIdentity) + "," + endpoint(firstIdentity) + "]}");

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-duplicate-identity", exception.Code);
    }

    [Fact]
    public void BaselineRejectsPersistedIdentityThatDoesNotMatchParseableRoute()
    {
        string fingerprint = AuthSurfaceCanonicalizer.Fingerprint([]);
        string identity = AuthSurfaceCanonicalizer.CreatePersistedIdentity(
            "/same",
            AuthSurfaceCanonicalizer.CanonicalIdentity("/other", "GET"));
        string json = "{\"schemaVersion\":1,\"endpoints\":[{\"route\":\"/same\",\"identity\":\"" + identity + "\",\"methods\":[\"GET\"]," +
            "\"authorization\":\"ExplicitAnonymous\",\"policies\":[],\"roles\":[],\"schemes\":[]," +
            "\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false,\"requirements\":[]," +
            "\"requirementFingerprint\":\"" + fingerprint + "\"}]}";

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, json);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
    }

    [Fact]
    public void LegacyAmbiguousPersistedIdentityFailsClosed()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart("id", "x?", RoutePatternParameterKind.Standard),
                ]),
            ]);
        string identity = AuthSurfaceCanonicalizer.CanonicalIdentity(pattern, "GET");
        string legacyToken = AuthSurfaceCanonicalizer.CreatePersistedIdentity(
            "/items/{id=x?}",
            identity);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceCanonicalizer.ReadPersistedIdentity(legacyToken, "/items/{id=x?}", "GET"));

        Assert.Equal("baseline-malformed", exception.Code);
    }

    [Fact]
    public void RouteNormalizationPreservesSlashPayloads()
    {
        string route = AuthSurfaceCanonicalizer.NormalizeRoute(
            RoutePatternFactory.Parse("/items/{id:regex(^a//b$)}"));

        Assert.Contains("^a//b$", route, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DerivedRolesRequirementIsOpaqueAndDoesNotPopulateRoles()
    {
        var policy = new AuthorizationPolicyBuilder()
            .AddRequirements(new DerivedRolesRequirement("custom-state", ["Admin"]))
            .Build();
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/derived-role"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(policy);
        RouteEndpoint endpoint = (RouteEndpoint)builder.Build();

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([endpoint])],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceEndpoint record = Assert.Single(report.Endpoints);

        Assert.Empty(record.Roles);
        Assert.Contains(record.Requirements, value => value.Contains(nameof(DerivedRolesRequirement), StringComparison.Ordinal) && value.EndsWith("|opaque", StringComparison.Ordinal));
        Assert.DoesNotContain(record.Requirements, value => value.Contains("kind=roles", StringComparison.Ordinal));

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);
        Assert.Contains(Assert.Single(roundTrip.Endpoints).Requirements, value => value.EndsWith("|opaque", StringComparison.Ordinal));
        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid);
    }

    [Fact]
    public async Task DeepTextualPolicyFailsClosedBeforeUnboundedRecursion()
    {
        string policy = "int";
        for (int index = 0; index < 64; index++)
        {
            policy = "optional(" + policy + ")";
        }

        var source = new DefaultEndpointDataSource([BuildEndpoint(RoutePatternFactory.Parse("/items/{id:" + policy + "}"))]);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-deep", exception.Code);
    }

    [Fact]
    public async Task DeepProgrammaticCompositeFailsClosedBeforeUnboundedRecursion()
    {
        IParameterPolicy policy = new IntRouteConstraint();
        for (int index = 0; index < 64; index++)
        {
            policy = new OptionalRouteConstraint((IRouteConstraint)policy);
        }

        var source = new DefaultEndpointDataSource([BuildEndpoint(ProgrammaticPattern(policy))]);
        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-deep", exception.Code);
    }

    [Fact]
    public async Task LargeRequirementDataCollectionFailsClosedWithoutPartialReport()
    {
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/large-metadata"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new LargeRequirementData());
        RouteEndpoint endpoint = (RouteEndpoint)builder.Build();

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([endpoint])],
                new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("metadata-limit", exception.Code);
    }

    [Fact]
    public async Task LargeDirectPolicyFailsClosedBeforeRequirementCanonicalization()
    {
        var policyBuilder = new AuthorizationPolicyBuilder();
        policyBuilder.AddRequirements(
            Enumerable.Range(0, AuthSurfaceCanonicalizer.MaximumEffectiveRequirementCount + 1)
                .Select(static index => (IAuthorizationRequirement)new ClaimsAuthorizationRequirement(
                    "scope",
                    [index.ToString(CultureInfo.InvariantCulture)]))
                .ToArray());
        RouteEndpointBuilder builder = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/large-direct-policy"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(policyBuilder.Build());

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([(RouteEndpoint)builder.Build()])],
                new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
    }

    [Fact]
    public async Task CombinedAuthorizationValueMaterializationHonorsScanBudget()
    {
        int valuesPerCollection = AuthSurfaceCanonicalizer.MaximumNestedValueCount / 3 + 1;
        string[] roles = Enumerable.Range(0, valuesPerCollection)
            .Select(static index => "Role" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        string[] claims = Enumerable.Range(0, valuesPerCollection)
            .Select(static index => "Claim" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        string[] schemes = Enumerable.Range(0, valuesPerCollection)
            .Select(static index => "Scheme" + index.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var policy = new AuthorizationPolicyBuilder()
            .RequireRole(roles)
            .RequireClaim("scope", claims)
            .AddAuthenticationSchemes(schemes)
            .Build();
        RouteEndpointBuilder builder = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/combined-values"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(policy);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([(RouteEndpoint)builder.Build()])],
                new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("resource-limit", exception.Code);
    }

    [Fact]
    public async Task MethodExpansionCannotExceedOutputRecordBound()
    {
        RouteEndpointBuilder builder = new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/many-methods"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(
            Enumerable.Range(0, AuthSurfaceCanonicalizer.MaximumEndpointCount + 1)
                .Select(static index => "M" + index.ToString("D6", CultureInfo.InvariantCulture))));
        builder.Metadata.Add(new AllowAnonymousAttribute());

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([(RouteEndpoint)builder.Build()])],
                new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("endpoint-limit", exception.Code);
    }

    [Fact]
    public async Task ProgrammaticHttpMethodConstraintFailsClosedAtNestedValueBound()
    {
        IParameterPolicy policy = new HttpMethodRouteConstraint(
            Enumerable.Range(0, AuthSurfaceCanonicalizer.MaximumNestedValueCount + 1)
                .Select(static index => "M" + index.ToString("D6", CultureInfo.InvariantCulture))
                .ToArray());

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(ProgrammaticPattern(policy))])],
                new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("route-policy-too-complex", exception.Code);
    }

    [Fact]
    public async Task LongRouteFailsClosedWithBoundedDiagnostic()
    {
        var source = new DefaultEndpointDataSource([
            BuildEndpoint(RoutePatternFactory.Parse("/" + new string('a', 20_000))),
        ]);

        AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
            async () => await new AuthSurfaceScanner([source], new AllowingPolicyProvider()).ScanAsync());

        Assert.Equal("route-pattern-too-large", exception.Code);
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

        string programmaticInt = AuthSurfaceCanonicalizer.NormalizeRoute(ProgrammaticPattern(new IntRouteConstraint()));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity(programmaticInt, "GET"));
        string programmaticRegex = AuthSurfaceCanonicalizer.NormalizeRoute(
            ProgrammaticPattern(new RegexRouteConstraint(
                new System.Text.RegularExpressions.Regex("^\\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled))));
        Assert.NotEqual(
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
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(^\\d+$;options=0)}", "GET"));
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(foo)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(foo;options=521)}", "GET"));
    }

    [Fact]
    public void PolicyTokenCasingIsInvariantBeyondTheFormerSpellings()
    {
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:iNt}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET"));
    }

    [Theory]
    [InlineData("regex(ab,1)")]
    [InlineData("regex(a{{1,3}})")]
    [InlineData("regex([a-z]{{1,3}})")]
    public void InlineRegexArgumentsKeepCommasAsPatternText(string policy)
    {
        string first = AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:" + policy + "}", "GET");
        string second = AuthSurfaceCanonicalizer.CanonicalIdentity(
            "/items/{id:" + policy.Replace(',', ';') + "}",
            "GET");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void InlineRegexLiteralOptionsTextIsNotProgrammaticMetadata()
    {
        Assert.NotEqual(
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(foo;options=0)}", "GET"),
            AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:regex(foo)}", "GET"));
    }

    [Fact]
    public void UnsupportedProgrammaticRegexOptionsFailClosed()
    {
        Assert.Throws<AuthSurfaceAnalysisException>(() =>
            AuthSurfaceCanonicalizer.NormalizeRoute(
                ProgrammaticPattern(new RegexRouteConstraint(
                    new System.Text.RegularExpressions.Regex("foo", System.Text.RegularExpressions.RegexOptions.None)))));
    }

    [Fact]
    public async Task TextualTokenIdentityDoesNotAssumeTheDefaultRouteOptionsMap()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/items/{id:int}", () => Results.Ok()).AllowAnonymous(),
            services => services.AddRouting(options => options.ConstraintMap["int"] = typeof(RemappedIntConstraint)));
        await app.StartAsync();

        RouteEndpoint endpoint = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(routeEndpoint => routeEndpoint.RoutePattern.RawText == "/items/{id:int}");
        Assert.Equal(
            typeof(RemappedIntConstraint),
            app.Services.GetRequiredService<IOptions<RouteOptions>>().Value.ConstraintMap["int"]);

        string textual = AuthSurfaceCanonicalizer.CanonicalIdentity("/items/{id:int}", "GET");
        string builtInProgrammatic = AuthSurfaceCanonicalizer.CanonicalIdentity(
            AuthSurfaceCanonicalizer.NormalizeRoute(ProgrammaticPattern(new IntRouteConstraint())),
            "GET");

        Assert.NotEqual(textual, builtInProgrammatic);
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

    [Fact]
    public async Task DerivedBuiltInWrapperPoliciesFailClosedBeforeWrapperDispatch()
    {
        foreach (IParameterPolicy policy in new IParameterPolicy[]
        {
            new DerivedCompositeConstraint([new IntRouteConstraint()]),
            new DerivedOptionalConstraint(new IntRouteConstraint()),
        })
        {
            AuthSurfaceAnalysisException exception = await Assert.ThrowsAsync<AuthSurfaceAnalysisException>(
                async () => await new AuthSurfaceScanner(
                    [new DefaultEndpointDataSource([BuildEndpoint(ProgrammaticPattern(policy))])],
                    new AllowingPolicyProvider()).ScanAsync());

            Assert.Equal("unsupported-parameter-policy", exception.Code);
        }
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

    private static RoutePattern ProgrammaticContentPoliciesPattern(params string[] policies) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("MiXeD")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null!,
                        RoutePatternParameterKind.Standard,
                        policies.Select(RoutePatternFactory.ParameterPolicy).ToArray()),
                ]),
            ]);

    private static RoutePattern MultipleParameterDelimiterPattern() =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "first",
                        "x?/with:slash",
                        RoutePatternParameterKind.Standard,
                        [
                            RoutePatternFactory.ParameterPolicy("foo=bar"),
                            RoutePatternFactory.ParameterPolicy("baz"),
                        ]),
                ]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "second",
                        "fallback",
                        RoutePatternParameterKind.CatchAll,
                        [RoutePatternFactory.ParameterPolicy(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                            "[)]:payload",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                            System.Text.RegularExpressions.RegexOptions.Compiled))) ]),
                ]),
            ]);

    private static RoutePattern ProgrammaticPatternWithDefault(IParameterPolicy policy, object defaultValue) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        defaultValue,
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(policy)]),
                ]),
            ]);

    private static RegexRouteConstraint ProgrammaticRegex() =>
        new(new System.Text.RegularExpressions.Regex(
            "^\\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled));

    private static RoutePattern CatchAllPattern(object defaultValue) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("files")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "path",
                        defaultValue,
                        RoutePatternParameterKind.CatchAll,
                        [RoutePatternFactory.ParameterPolicy(new IntRouteConstraint())]),
                ]),
            ]);

    private static async Task AssertPersistedIdentityMutationRejected(
        RoutePattern pattern,
        Func<string, string> rewriteStructuralRoute)
    {
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string mutatedIdentity = RewritePersistedIdentity(
            persistedIdentity,
            identity =>
            {
                int methodSeparator = identity.LastIndexOf('\u001f');
                Assert.True(methodSeparator > 0);
                string structuralRoute = identity[..methodSeparator];
                string mutatedRoute = rewriteStructuralRoute(structuralRoute);
                Assert.NotEqual(structuralRoute, mutatedRoute);
                return mutatedRoute + identity[methodSeparator..];
            });
        File.WriteAllText(path, json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static string ApplyMarkerMutation(string structuralRoute, string mutationCase) =>
        mutationCase switch
        {
            "policy-token" or "regex-policy-token" or "composite-policy-token" or "optional-policy-token" =>
                structuralRoute.Replace(":programmatic:", ":programmatic:text:", StringComparison.Ordinal),
            "mixed-case-policy-token" =>
                structuralRoute.Replace(":programmatic:", ":programmatic:Text:", StringComparison.Ordinal),
            "default" => structuralRoute.Replace("=x?:foo", "=x?:text:foo", StringComparison.Ordinal),
            "catch-all-default" => structuralRoute.Replace("=x?/foo", "=x?:text:foo", StringComparison.Ordinal),
            "slash-default" => structuralRoute.Replace("=foo/bar", "=foo/:text:bar", StringComparison.Ordinal),
            "literal-marker-default" =>
                structuralRoute.Replace("=literal:text:marker", "=literal:text:text:marker", StringComparison.Ordinal),
            "regex-payload" => structuralRoute.Replace(";options=521", ";options=521:text:", StringComparison.Ordinal),
            "nested-payload" => structuralRoute.Replace("int,min", "int:text:min", StringComparison.Ordinal),
            "escaped-brace-payload" => structuralRoute.Replace("regex64(", "regex64(A", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutationCase)),
        };

    public static IEnumerable<object[]> ReservedProgrammaticMarkerCases() =>
    new (string TextualPolicy, Func<IParameterPolicy> RuntimePolicy)[]
    {
        ("programmatic:int", static () => new IntRouteConstraint()),
        ("programmatic:composite(int,min(2))", static () => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)])),
        ("programmatic:optional(int)", static () => new OptionalRouteConstraint(new IntRouteConstraint())),
        ("programmatic:regex64(XlxkKyQ;options=521)", static () => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
            "^\\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled))),
        ("programmatic:httpMethod(GET,POST)", static () => new HttpMethodRouteConstraint(["POST", "GET"])),
    }
    .Select(static row => new object[] { row.TextualPolicy, row.RuntimePolicy() });

    private static RoutePattern TextualPattern(string policy) =>
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

    private static string ReplaceStructuralRoute(string identity, string replacementRoute)
    {
        int methodSeparator = identity.LastIndexOf('\u001f');
        Assert.True(methodSeparator > 0);
        return replacementRoute + identity[methodSeparator..];
    }

    private static string RewritePersistedIdentity(
        string persistedIdentity,
        Func<string, string> rewriteIdentity)
    {
        Assert.StartsWith("v1:", persistedIdentity, StringComparison.Ordinal);
        string encoded = persistedIdentity[3..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        string payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        int separator = payload.IndexOf('\u001e');
        Assert.True(separator > 0);
        string identityToken = payload[(separator + 1)..];
        string rewrittenToken;
        if (identityToken.StartsWith(PersistedIdentityBinding.Prefix, StringComparison.Ordinal))
        {
            string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encodedBinding += new string('=', (4 - encodedBinding.Length % 4) % 4);
            PersistedIdentityBinding binding = PersistedIdentityBinding.Deserialize(
                Convert.FromBase64String(encodedBinding));
            string structuralIdentity = binding.IdentityRoute! + '\u001f' + binding.Method.ToUpperInvariant();
            string mutatedIdentity = rewriteIdentity(structuralIdentity);
            int methodSeparator = mutatedIdentity.LastIndexOf('\u001f');
            Assert.True(methodSeparator > 0);
            string mutatedRoute = mutatedIdentity[..methodSeparator];
            string mutatedMethod = mutatedIdentity[(methodSeparator + 1)..];
            Assert.NotEqual(structuralIdentity, mutatedIdentity);
            string mutatedBinding = Convert.ToBase64String(
                    binding
                        .WithIdentityRoute(mutatedRoute)
                        .WithMethod(mutatedMethod)
                        .Serialize())
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            rewrittenToken = PersistedIdentityBinding.Prefix + mutatedBinding;
        }
        else
        {
            rewrittenToken = rewriteIdentity(identityToken);
        }

        string rewrittenPayload = payload[..(separator + 1)] + rewrittenToken;
        return "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(rewrittenPayload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static RouteEndpoint BuildEndpoint(RoutePattern pattern)
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new AllowAnonymousAttribute());
        return (RouteEndpoint)builder.Build();
    }

    private sealed class DerivedRolesRequirement : RolesAuthorizationRequirement
    {
        public DerivedRolesRequirement(string state, IEnumerable<string> allowedRoles)
            : base(allowedRoles)
        {
            State = state;
        }

        public string State { get; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    private sealed class LargeRequirementData : Attribute, IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements() => new LargeRequirementSequence();

        private sealed class LargeRequirementSequence : ICollection<IAuthorizationRequirement>
        {
            public int Count => AuthSurfaceCanonicalizer.MaximumMetadataItems + 1;

            public bool IsReadOnly => true;

            public void Add(IAuthorizationRequirement item) => throw new NotSupportedException();

            public void Clear() => throw new NotSupportedException();

            public bool Contains(IAuthorizationRequirement item) => false;

            public void CopyTo(IAuthorizationRequirement[] array, int arrayIndex) => throw new NotSupportedException();

            public bool Remove(IAuthorizationRequirement item) => throw new NotSupportedException();

            public IEnumerator<IAuthorizationRequirement> GetEnumerator()
            {
                for (int index = 0; index < Count; index++)
                {
                    yield return new ClaimsAuthorizationRequirement("scope", [index.ToString(CultureInfo.InvariantCulture)]);
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    private static WebApplication BuildApplication(
        Action<WebApplication> configureEndpoints,
        Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FixtureHost).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        configureServices?.Invoke(builder.Services);
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

    private sealed class DerivedCompositeConstraint : CompositeRouteConstraint
    {
        public DerivedCompositeConstraint(IEnumerable<IRouteConstraint> constraints)
            : base(constraints)
        {
        }
    }

    private sealed class DerivedOptionalConstraint : OptionalRouteConstraint
    {
        public DerivedOptionalConstraint(IRouteConstraint innerConstraint)
            : base(innerConstraint)
        {
        }
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

    private sealed class RemappedIntConstraint : IRouteConstraint
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
