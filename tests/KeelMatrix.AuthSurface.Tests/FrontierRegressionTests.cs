using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class FrontierRegressionTests
{
    [Fact]
    public async Task RealAuthorizationPolicyValuesAreLosslessThroughBaselineComparison()
    {
        await using WebApplication baselineApp = BuildApplication(application =>
        {
            application.MapGet("/claim", () => Results.Ok()).WithMetadata(
                new AuthorizationPolicyBuilder().RequireClaim("scope", "read|write", " admin ", string.Empty).Build());
        });
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(baselineReport);

        await using WebApplication changedApp = BuildApplication(application =>
        {
            application.MapGet("/claim", () => Results.Ok()).WithMetadata(
                new AuthorizationPolicyBuilder().RequireClaim("scope", "read", "write", " admin ", string.Empty).Build());
        });
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(changedReport, baseline);

        Assert.False(result.IsValid);
        AuthSurfaceViolation violation = Assert.Single(
            result.Violations.Where(item => item.Code == "endpoint-requirement-changed"));
        Assert.Contains("read|write", violation.Expected, StringComparison.Ordinal);
        Assert.Contains("read", violation.Actual, StringComparison.Ordinal);
        Assert.Contains(" admin ", violation.Expected, StringComparison.Ordinal);
        Assert.Contains("0:7: admin", violation.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequirementDataIsCombinedAfterAuthorizeDataAndChangesTheBaseline()
    {
        await using WebApplication baselineApp = BuildApplication(application =>
        {
            application.MapGet("/requirement", () => Results.Ok())
                .RequireAuthorization()
                .WithMetadata(new ClaimRequirementData("read"));
        });
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(baselineReport.Endpoints.Where(item => item.Route == "/requirement"));
        Assert.True(baselineEndpoint.UsesDefaultPolicy);
        Assert.Contains(baselineEndpoint.Requirements, item => item.Contains("read", StringComparison.Ordinal));
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(baselineReport);

        await using WebApplication changedApp = BuildApplication(application =>
        {
            application.MapGet("/requirement", () => Results.Ok())
                .RequireAuthorization()
                .WithMetadata(new ClaimRequirementData("write"));
        });
        await changedApp.StartAsync();
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            await new AuthSurfaceScanner(changedApp.Services).ScanAsync(),
            baseline);

        Assert.Contains(result.Violations, item => item.Code == "endpoint-requirement-changed");
    }

    [Fact]
    public async Task ReversingDirectPolicyRequirementsChangesCanonicalTextFingerprintAndComparison()
    {
        await using WebApplication baselineApp = BuildApplication(application =>
        {
            application.MapGet("/ordered-direct", () => Results.Ok()).WithMetadata(
                new AuthorizationPolicyBuilder()
                    .RequireClaim("scope", "first")
                    .RequireClaim("scope", "second")
                    .Build());
        });
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(
            baselineReport.Endpoints.Where(item => item.Route == "/ordered-direct"));
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(baselineReport);

        await using WebApplication changedApp = BuildApplication(application =>
        {
            application.MapGet("/ordered-direct", () => Results.Ok()).WithMetadata(
                new AuthorizationPolicyBuilder()
                    .RequireClaim("scope", "second")
                    .RequireClaim("scope", "first")
                    .Build());
        });
        await changedApp.StartAsync();
        AuthSurfaceEndpoint changedEndpoint = Assert.Single(
            (await new AuthSurfaceScanner(changedApp.Services).ScanAsync()).Endpoints.Where(item => item.Route == "/ordered-direct"));

        Assert.False(baselineEndpoint.Requirements.SequenceEqual(changedEndpoint.Requirements, StringComparer.Ordinal));
        Assert.NotEqual(baselineEndpoint.RequirementFingerprint, changedEndpoint.RequirementFingerprint);

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            await new AuthSurfaceScanner(changedApp.Services).ScanAsync(),
            baseline);

        Assert.Contains(result.Violations, item => item.Code == "endpoint-requirement-changed");
    }

    [Fact]
    public async Task ReversingRequirementDataRequirementsChangesCanonicalTextFingerprintAndComparison()
    {
        await using WebApplication baselineApp = BuildApplication(application =>
        {
            application.MapGet("/ordered-data", () => Results.Ok()).WithMetadata(
                new ClaimRequirementData("first"),
                new ClaimRequirementData("second"));
        });
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(
            baselineReport.Endpoints.Where(item => item.Route == "/ordered-data"));
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(baselineReport);
        int firstIndex = Array.FindIndex(baselineEndpoint.Requirements.ToArray(), item => item.Contains("5:first", StringComparison.Ordinal));
        int secondIndex = Array.FindIndex(baselineEndpoint.Requirements.ToArray(), item => item.Contains("6:second", StringComparison.Ordinal));
        Assert.True(firstIndex >= 0);
        Assert.True(secondIndex > firstIndex);

        await using WebApplication changedApp = BuildApplication(application =>
        {
            application.MapGet("/ordered-data", () => Results.Ok()).WithMetadata(
                new ClaimRequirementData("second"),
                new ClaimRequirementData("first"));
        });
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceEndpoint changedEndpoint = Assert.Single(
            changedReport.Endpoints.Where(item => item.Route == "/ordered-data"));

        Assert.False(baselineEndpoint.Requirements.SequenceEqual(changedEndpoint.Requirements, StringComparer.Ordinal));
        Assert.NotEqual(baselineEndpoint.RequirementFingerprint, changedEndpoint.RequirementFingerprint);

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(changedReport, baseline);

        Assert.Contains(result.Violations, item => item.Code == "endpoint-requirement-changed");

        string path = Path.Combine(Path.GetTempPath(), "authsurface-order-tests", Guid.NewGuid().ToString("N"), "authsurface.json");
        try
        {
            baseline.Write(path, overwrite: false);
            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

            Assert.Equal(baselineEndpoint.Requirements, roundTrip.Endpoints.Single().Requirements);
            Assert.True(AuthSurfaceVerifier.Compare(baselineReport, roundTrip).IsValid);
        }
        finally
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MixedSourcesPreserveFrameworkCombinationOrderAndDuplicateRequirements()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/ordered-mixed", () => Results.Ok()).WithMetadata(
                new AuthorizeAttribute { Roles = "attribute" },
                new AuthorizationPolicyBuilder().RequireClaim("scope", "direct").Build(),
                new ClaimRequirementData("data"),
                new ClaimRequirementData("data"));
        });
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints.Where(item => item.Route == "/ordered-mixed"));
        int attributeIndex = Array.FindIndex(endpoint.Requirements.ToArray(), item => item.Contains("kind=roles", StringComparison.Ordinal));
        int directIndex = Array.FindIndex(endpoint.Requirements.ToArray(), item => item.Contains("6:direct", StringComparison.Ordinal));
        int firstDataIndex = Array.FindIndex(endpoint.Requirements.ToArray(), item => item.Contains("4:data", StringComparison.Ordinal));

        Assert.True(attributeIndex >= 0);
        Assert.True(directIndex > attributeIndex);
        Assert.True(firstDataIndex > directIndex);
        Assert.Equal(2, endpoint.Requirements.Count(item => item.Contains("4:data", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RolesOnlyAuthorizationDoesNotClaimDefaultPolicyContribution()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/roles", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { Roles = " admin " });
        });
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints.Where(item => item.Route == "/roles"));

        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, endpoint.AuthorizationKind);
        Assert.False(endpoint.UsesDefaultPolicy);
        Assert.Contains("admin", endpoint.Roles);
    }

    [Fact]
    public async Task DirectPolicySuppressesDefaultPolicyContributionWhenCombinedWithAuthorizeData()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/direct", () => Results.Ok())
                .WithMetadata(new AuthorizationPolicyBuilder().RequireClaim("scope", "direct").Build())
                .WithMetadata(new AuthorizeAttribute());
        });
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints.Where(item => item.Route == "/direct"));

        Assert.False(endpoint.UsesDefaultPolicy);
        Assert.Contains(endpoint.Requirements, item => item.Contains("direct", StringComparison.Ordinal));
        Assert.DoesNotContain(endpoint.Requirements, item => item.Contains("default", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublicCollectionsAndBaselineCopiesCannotBeMutatedThroughCasts()
    {
        await using WebApplication app = BuildApplication(application =>
        {
            application.MapGet("/immutable", () => Results.Ok()).WithMetadata(
                new AuthorizationPolicyBuilder().RequireClaim("scope", "read").Build());
        });
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(report);

        Assert.False(report.Endpoints is AuthSurfaceEndpoint[]);
        Assert.False(report.Endpoints[0].Requirements is string[]);
        Assert.False(baseline.Endpoints[0].Requirements is string[]);
        Assert.NotSame(report.Endpoints[0], baseline.Endpoints[0]);
        Assert.Equal(report.Endpoints[0].Requirements, baseline.Endpoints[0].Requirements);
    }

    [Fact]
    public void ComparisonReportsClassificationAndStructuredBeforeAfterDetails()
    {
        AuthSurfaceEndpoint expected = new(
            "/secure",
            "GET",
            AuthSurfaceAuthorizationKind.ExplicitProtected,
            ["Read"],
            [],
            ["Bearer"],
            usesDefaultPolicy: true,
            usesFallbackPolicy: false,
            ["requirement"],
            new string('a', 64));
        AuthSurfaceEndpoint actual = new(
            "/secure",
            "GET",
            AuthSurfaceAuthorizationKind.ExplicitAnonymous,
            ["Write"],
            [],
            ["Bearer"],
            usesDefaultPolicy: false,
            usesFallbackPolicy: false,
            ["changed"],
            new string('b', 64));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([actual], []),
            AuthSurfaceBaseline.Create(new AuthSurfaceReport([expected], [])));

        Assert.Contains(result.Violations, item => item.Code == "endpoint-classification-changed" &&
            item.Expected == "ExplicitProtected" && item.Actual == "ExplicitAnonymous");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-policy-changed" &&
            item.Expected == "[\"Read\"]" && item.Actual == "[\"Write\"]");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-requirement-changed" &&
            item.Expected!.Contains("requirement", StringComparison.Ordinal) &&
            item.Actual!.Contains("changed", StringComparison.Ordinal));
    }

    private static WebApplication BuildApplication(Action<WebApplication> configureEndpoints)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(FrontierRegressionTests).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireClaim("scope", "default").Build();
        });
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        configureEndpoints(app);
        return app;
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    private sealed class ClaimRequirementData : Attribute, IAuthorizationRequirementData
    {
        private readonly string value;

        public ClaimRequirementData(string value) => this.value = value;

        public IEnumerable<IAuthorizationRequirement> GetRequirements() =>
            [new ClaimsAuthorizationRequirement("scope", [value])];
    }
}
