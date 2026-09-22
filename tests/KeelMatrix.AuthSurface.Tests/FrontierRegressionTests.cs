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
