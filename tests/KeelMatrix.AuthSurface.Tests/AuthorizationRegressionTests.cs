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

public sealed class AuthorizationRegressionTests
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
    public async Task EmptyRequirementDataPreservesConfiguredFallbackPolicy()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/empty-fallback", () => Results.Ok())
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: true);
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints
                .Where(item => item.Route == "/empty-fallback"));
        AuthorizationPolicy? frameworkPolicy = await CombineFrameworkPolicyAsync(app, "/empty-fallback");

        Assert.NotNull(frameworkPolicy);
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalizeRequirements(frameworkPolicy),
            endpoint.Requirements);
        Assert.Equal(AuthSurfaceAuthorizationKind.FallbackProtected, endpoint.AuthorizationKind);
        Assert.True(endpoint.UsesFallbackPolicy);
        Assert.False(endpoint.Requirements.Count == 0);
    }

    [Fact]
    public async Task EmptyRequirementDataWithoutFallbackMatchesFrameworkWithoutPolicy()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/empty-no-fallback", () => Results.Ok())
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: false);
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        AuthSurfaceEndpoint endpoint = Assert.Single(
            report.Endpoints.Where(item => item.Route == "/empty-no-fallback"));
        AuthorizationPolicy? frameworkPolicy = await CombineFrameworkPolicyAsync(app, "/empty-no-fallback");

        Assert.Null(frameworkPolicy);
        Assert.Equal(AuthSurfaceAuthorizationKind.Unprotected, endpoint.AuthorizationKind);
        Assert.False(endpoint.UsesDefaultPolicy);
        Assert.False(endpoint.UsesFallbackPolicy);
        Assert.Empty(endpoint.Requirements);
        Assert.Contains(report.PolicyViolations, item => item.Code == "unprotected-endpoint");
    }

    [Fact]
    public async Task EmptyRequirementDataWithDefaultPolicyMatchesFrameworkDefaultPolicy()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/empty-default", () => Results.Ok())
                .RequireAuthorization()
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: false);
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints
                .Where(item => item.Route == "/empty-default"));
        AuthorizationPolicy? frameworkPolicy = await CombineFrameworkPolicyAsync(app, "/empty-default");

        Assert.NotNull(frameworkPolicy);
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalizeRequirements(frameworkPolicy),
            endpoint.Requirements);
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, endpoint.AuthorizationKind);
        Assert.True(endpoint.UsesDefaultPolicy);
        Assert.False(endpoint.UsesFallbackPolicy);
    }

    [Fact]
    public async Task EmptyRequirementDataPreservesExplicitNamedPolicyProvenance()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/empty-explicit", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { Policy = "Policy" }, new EmptyRequirementData()),
            configureServices: services => services.AddSingleton<IAuthorizationPolicyProvider, EquivalentPolicyProvider>());
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints
                .Where(item => item.Route == "/empty-explicit"));
        AuthorizationPolicy? frameworkPolicy = await CombineFrameworkPolicyAsync(app, "/empty-explicit");

        Assert.NotNull(frameworkPolicy);
        Assert.Equal(
            AuthSurfaceCanonicalizer.CanonicalizeRequirements(frameworkPolicy),
            endpoint.Requirements);
        Assert.Equal(["Policy"], endpoint.Policies);
        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, endpoint.AuthorizationKind);
        Assert.False(endpoint.UsesDefaultPolicy);
        Assert.False(endpoint.UsesFallbackPolicy);
    }

    [Fact]
    public async Task EmptyRequirementDataFallbackContributionChangeProducesStructuredViolations()
    {
        await using WebApplication baselineApp = BuildApplication(
            application => application.MapGet("/empty-fallback-change", () => Results.Ok())
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: true);
        await baselineApp.StartAsync();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(
            await new AuthSurfaceScanner(baselineApp.Services).ScanAsync());

        await using WebApplication changedApp = BuildApplication(
            application => application.MapGet("/empty-fallback-change", () => Results.Ok())
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: false);
        await changedApp.StartAsync();
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            await new AuthSurfaceScanner(changedApp.Services).ScanAsync(),
            baseline);

        Assert.Contains(result.Violations, item => item.Code == "endpoint-classification-changed");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-fallback-policy-changed");
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
    public async Task SchemeOnlyAuthorizationUsesDefaultPolicyAndContributionChangesAreComparable()
    {
        await using WebApplication baselineApp = BuildApplication(application =>
        {
            application.MapGet("/scheme-only", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { AuthenticationSchemes = "Bearer" });
        });
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(
            baselineReport.Endpoints.Where(item => item.Route == "/scheme-only"));

        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, baselineEndpoint.AuthorizationKind);
        Assert.True(baselineEndpoint.UsesDefaultPolicy);
        Assert.Contains(baselineEndpoint.Requirements, item => item.Contains("default", StringComparison.Ordinal));

        await using WebApplication changedApp = BuildApplication(application =>
        {
            application.MapGet("/scheme-only", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { Roles = "Admin" });
        });
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceEndpoint changedEndpoint = Assert.Single(
            changedReport.Endpoints.Where(item => item.Route == "/scheme-only"));

        Assert.False(changedEndpoint.UsesDefaultPolicy);
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            changedReport,
            AuthSurfaceBaseline.Create(baselineReport));

        Assert.Contains(result.Violations, item => item.Code == "endpoint-default-policy-changed");
    }

    [Fact]
    public async Task RequirementDataIsExplicitWhileFallbackProvenanceRemainsIndependent()
    {
        await using WebApplication baselineApp = BuildApplication(
            application =>
            {
                application.MapGet("/requirement-fallback", () => Results.Ok())
                    .WithMetadata(new ClaimRequirementData("fallback"));
            },
            fallbackPolicy: true);
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(
            baselineReport.Endpoints.Where(item => item.Route == "/requirement-fallback"));

        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, baselineEndpoint.AuthorizationKind);
        Assert.True(baselineEndpoint.UsesFallbackPolicy);
        Assert.Contains(baselineEndpoint.Requirements, item => item.Contains("fallback", StringComparison.Ordinal));
        AuthSurfaceReport strictReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync(
            new AuthSurfaceScanOptions(strictFallbackPolicy: true));
        Assert.Contains(strictReport.PolicyViolations, item =>
            item.Code == "fallback-policy-endpoint" && item.Route == "/requirement-fallback");

        await using WebApplication changedApp = BuildApplication(
            application =>
            {
                application.MapGet("/requirement-fallback", () => Results.Ok())
                    .WithMetadata(new ClaimRequirementData("fallback"));
            },
            fallbackPolicy: false);
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceEndpoint changedEndpoint = Assert.Single(
            changedReport.Endpoints.Where(item => item.Route == "/requirement-fallback"));

        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, changedEndpoint.AuthorizationKind);
        Assert.False(changedEndpoint.UsesFallbackPolicy);
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            changedReport,
            AuthSurfaceBaseline.Create(baselineReport));

        Assert.Contains(result.Violations, item => item.Code == "endpoint-fallback-policy-changed");
    }

    [Fact]
    public async Task NonEmptyRequirementDataWithoutFallbackIsExplicitProtected()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/requirement-only", () => Results.Ok())
                .WithMetadata(new ClaimRequirementData("requirement")),
            fallbackPolicy: false);
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints
                .Where(item => item.Route == "/requirement-only"));

        Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitProtected, endpoint.AuthorizationKind);
        Assert.False(endpoint.UsesFallbackPolicy);
        Assert.Contains(endpoint.Requirements, item => item.Contains("requirement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllowAnonymousPrecedesExplicitDefaultAndFallbackMetadata()
    {
        await using WebApplication app = BuildApplication(
            application =>
            {
                application.MapGet("/anonymous-explicit", () => Results.Ok())
                    .WithMetadata(new AuthorizationPolicyBuilder().RequireClaim("scope", "explicit").Build())
                    .WithMetadata(new AllowAnonymousAttribute());
                application.MapGet("/anonymous-default", () => Results.Ok())
                    .RequireAuthorization()
                    .WithMetadata(new AllowAnonymousAttribute());
                application.MapGet("/anonymous-fallback", () => Results.Ok())
                    .WithMetadata(new AllowAnonymousAttribute());
            },
            fallbackPolicy: true);
        await app.StartAsync();

        AuthSurfaceReport report = await new AuthSurfaceScanner(app.Services).ScanAsync();
        foreach (string route in new[] { "/anonymous-explicit", "/anonymous-default", "/anonymous-fallback" })
        {
            AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints.Where(item => item.Route == route));
            Assert.Equal(AuthSurfaceAuthorizationKind.ExplicitAnonymous, endpoint.AuthorizationKind);
            Assert.False(endpoint.UsesFallbackPolicy);
            Assert.DoesNotContain(report.PolicyViolations, item => item.Route == route);
        }
    }

    [Fact]
    public async Task EmptyRequirementDataWithoutFallbackRemainsUnprotected()
    {
        await using WebApplication app = BuildApplication(
            application => application.MapGet("/empty-no-fallback-repeat", () => Results.Ok())
                .WithMetadata(new EmptyRequirementData()),
            fallbackPolicy: false);
        await app.StartAsync();

        AuthSurfaceEndpoint endpoint = Assert.Single(
            (await new AuthSurfaceScanner(app.Services).ScanAsync()).Endpoints
                .Where(item => item.Route == "/empty-no-fallback-repeat"));

        Assert.Equal(AuthSurfaceAuthorizationKind.Unprotected, endpoint.AuthorizationKind);
        Assert.False(endpoint.UsesFallbackPolicy);
    }

    [Fact]
    public async Task ExactNamedPolicyIdentitySurvivesEquivalentCustomProviderResolution()
    {
        static void AddEquivalentPolicyProvider(IServiceCollection services) =>
            services.AddSingleton<IAuthorizationPolicyProvider, EquivalentPolicyProvider>();

        await using WebApplication baselineApp = BuildApplication(
            application =>
            {
                application.MapGet("/named-identity", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { Policy = "Policy" });
            },
            configureServices: AddEquivalentPolicyProvider);
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceEndpoint baselineEndpoint = Assert.Single(
            baselineReport.Endpoints.Where(item => item.Route == "/named-identity"));
        Assert.Equal(["Policy"], baselineEndpoint.Policies);

        await using WebApplication changedApp = BuildApplication(
            application =>
            {
                application.MapGet("/named-identity", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { Policy = " Policy " });
            },
            configureServices: AddEquivalentPolicyProvider);
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceEndpoint changedEndpoint = Assert.Single(
            changedReport.Endpoints.Where(item => item.Route == "/named-identity"));

        Assert.Equal([" Policy "], changedEndpoint.Policies);
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            changedReport,
            AuthSurfaceBaseline.Create(baselineReport));

        Assert.Contains(result.Violations, item => item.Code == "endpoint-policy-changed");
    }

    [Fact]
    public async Task ExactNamedPolicyIdentitySurvivesDiskBaselineRoundTrip()
    {
        static void AddEquivalentPolicyProvider(IServiceCollection services) =>
            services.AddSingleton<IAuthorizationPolicyProvider, EquivalentPolicyProvider>();

        await using WebApplication baselineApp = BuildApplication(
            application => application.MapGet("/named-round-trip", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { Policy = " Policy " }),
            configureServices: AddEquivalentPolicyProvider);
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(baselineReport, path, overwrite: false);
        AuthSurfaceBaseline roundTripped = AuthSurfaceBaseline.Read(path);

        AuthSurfaceVerificationResult unchanged = AuthSurfaceVerifier.Compare(baselineReport, roundTripped);
        Assert.True(unchanged.IsValid);
        Assert.Equal([" Policy "], roundTripped.Endpoints.Single(item => item.Route == "/named-round-trip").Policies);

        await using WebApplication changedApp = BuildApplication(
            application => application.MapGet("/named-round-trip", () => Results.Ok())
                .WithMetadata(new AuthorizeAttribute { Policy = "Policy" }),
            configureServices: AddEquivalentPolicyProvider);
        await changedApp.StartAsync();
        AuthSurfaceVerificationResult changed = AuthSurfaceVerifier.Compare(
            await new AuthSurfaceScanner(changedApp.Services).ScanAsync(),
            roundTripped);

        AuthSurfaceViolation violation = Assert.Single(
            changed.Violations.Where(item => item.Code == "endpoint-policy-changed"));
        Assert.Equal("[\" Policy \"]", violation.Expected);
        Assert.Equal("[\"Policy\"]", violation.Actual);
    }

    [Fact]
    public async Task RealHostBaselineChangeMatrixCoversRuntimeSurfaceDiffs()
    {
        static void AddEquivalentPolicyProvider(IServiceCollection services) =>
            services.AddSingleton<IAuthorizationPolicyProvider, EquivalentPolicyProvider>();

        await using WebApplication baselineApp = BuildApplication(
            application =>
            {
                application.MapGet("/removed", () => Results.Ok()).RequireAuthorization();
                application.MapGet("/classification", () => Results.Ok()).RequireAuthorization();
                application.MapGet("/policy", () => Results.Ok()).RequireAuthorization("PolicyA");
                application.MapGet("/role", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { Roles = "Admin" });
                application.MapGet("/scheme", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { AuthenticationSchemes = "Bearer" });
                application.MapGet("/requirements", () => Results.Ok())
                    .WithMetadata(new ClaimRequirementData("read"));
                application.MapGet("/default", () => Results.Ok()).RequireAuthorization();
                application.MapGet("/fallback", () => Results.Ok());
            },
            fallbackPolicy: true,
            configureServices: AddEquivalentPolicyProvider);
        await baselineApp.StartAsync();
        AuthSurfaceReport baselineReport = await new AuthSurfaceScanner(baselineApp.Services).ScanAsync();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(baselineReport);

        await using WebApplication changedApp = BuildApplication(
            application =>
            {
                application.MapGet("/added", () => Results.Ok()).RequireAuthorization();
                application.MapGet("/classification", () => Results.Ok()).AllowAnonymous();
                application.MapGet("/policy", () => Results.Ok()).RequireAuthorization("PolicyB");
                application.MapGet("/role", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { Roles = "Manager" });
                application.MapGet("/scheme", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { AuthenticationSchemes = "Cookies" });
                application.MapGet("/requirements", () => Results.Ok())
                    .WithMetadata(new ClaimRequirementData("write"));
                application.MapGet("/default", () => Results.Ok())
                    .WithMetadata(new AuthorizeAttribute { Roles = "Reader" });
                application.MapGet("/fallback", () => Results.Ok());
            },
            fallbackPolicy: false,
            configureServices: AddEquivalentPolicyProvider);
        await changedApp.StartAsync();
        AuthSurfaceReport changedReport = await new AuthSurfaceScanner(changedApp.Services).ScanAsync();
        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(changedReport, baseline);

        Assert.Contains(result.Violations, item => item.Code == "endpoint-added" && item.Route == "/added");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-removed" && item.Route == "/removed");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-classification-changed" && item.Route == "/classification");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-policy-changed" && item.Route == "/policy");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-role-changed" && item.Route == "/role");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-scheme-changed" && item.Route == "/scheme");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-requirement-changed" && item.Route == "/requirements");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-default-policy-changed" && item.Route == "/default");
        Assert.Contains(result.Violations, item => item.Code == "endpoint-fallback-policy-changed" && item.Route == "/fallback");
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

    private static WebApplication BuildApplication(
        Action<WebApplication> configureEndpoints,
        bool fallbackPolicy = false,
        Action<IServiceCollection>? configureServices = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AuthorizationRegressionTests).Assembly.GetName().Name,
            EnvironmentName = Environments.Development,
        });
        builder.Services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireClaim("scope", "default").Build();
            options.FallbackPolicy = fallbackPolicy
                ? new AuthorizationPolicyBuilder().RequireClaim("scope", "fallback").Build()
                : null;
        });
        configureServices?.Invoke(builder.Services);
        WebApplication app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        configureEndpoints(app);
        return app;
    }

    private static async Task<AuthorizationPolicy?> CombineFrameworkPolicyAsync(
        WebApplication app,
        string route)
    {
        RouteEndpoint endpoint = Assert.Single(
            app.Services.GetServices<EndpointDataSource>()
                .SelectMany(static source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(item => AuthSurfaceCanonicalizer.NormalizeRoute(item.RoutePattern) == route));

        return await AuthorizationPolicy.CombineAsync(
            app.Services.GetRequiredService<IAuthorizationPolicyProvider>(),
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>());
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    private sealed class EmptyRequirementData : Attribute, IAuthorizationRequirementData
    {
        public IEnumerable<IAuthorizationRequirement> GetRequirements() => [];
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    private sealed class ClaimRequirementData : Attribute, IAuthorizationRequirementData
    {
        private readonly string value;

        public ClaimRequirementData(string value) => this.value = value;

        public IEnumerable<IAuthorizationRequirement> GetRequirements() =>
            [new ClaimsAuthorizationRequirement("scope", [value])];
    }

    private sealed class EquivalentPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider inner;

        public EquivalentPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options) =>
            inner = new DefaultAuthorizationPolicyProvider(options);

        public bool AllowsCachingPolicies => inner.AllowsCachingPolicies;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => inner.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => inner.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) =>
            policyName is "Policy" or " Policy " or "PolicyA" or "PolicyB"
                ? Task.FromResult<AuthorizationPolicy?>(new AuthorizationPolicyBuilder()
                    .RequireClaim("scope", "equivalent")
                    .Build())
                : inner.GetPolicyAsync(policyName);
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
