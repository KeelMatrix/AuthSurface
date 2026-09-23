using KeelMatrix.AuthSurface;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class FingerprintContractTests
{
    [Theory]
    [InlineData("policy", "endpoint-policy-changed")]
    [InlineData("role", "endpoint-role-changed")]
    [InlineData("scheme", "endpoint-scheme-changed")]
    [InlineData("classification", "endpoint-classification-changed")]
    [InlineData("default", "endpoint-default-policy-changed")]
    [InlineData("fallback", "endpoint-fallback-policy-changed")]
    public void NonRequirementMetadataChangeDoesNotReportRequirementChange(
        string change,
        string expectedCode)
    {
        AuthSurfaceEndpoint before = Endpoint();
        AuthSurfaceEndpoint after = change switch
        {
            "policy" => Endpoint(policies: ["Write"]),
            "role" => Endpoint(roles: ["Writer"]),
            "scheme" => Endpoint(schemes: ["Cookies"]),
            "classification" => Endpoint(kind: AuthSurfaceAuthorizationKind.FallbackProtected),
            "default" => Endpoint(usesDefaultPolicy: false),
            "fallback" => Endpoint(usesFallbackPolicy: true),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(new AuthSurfaceReport([before], []));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([after], []),
            baseline);

        Assert.Equal(before.Requirements, after.Requirements);
        Assert.Equal(before.RequirementFingerprint, after.RequirementFingerprint);
        Assert.Equal([expectedCode], result.Violations.Select(static violation => violation.Code));
        Assert.DoesNotContain(result.Violations, static violation => violation.Code == "endpoint-requirement-changed");
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("reordered")]
    public void RequirementSequenceChangeReportsRequirementChange(string change)
    {
        AuthSurfaceEndpoint before = Endpoint(requirements: ["permission|read", "permission|write"]);
        AuthSurfaceEndpoint after = change switch
        {
            "changed" => Endpoint(requirements: ["permission|read", "permission|admin"]),
            "reordered" => Endpoint(requirements: ["permission|write", "permission|read"]),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(new AuthSurfaceReport([before], []));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([after], []),
            baseline);

        Assert.NotEqual(before.RequirementFingerprint, after.RequirementFingerprint);
        Assert.Equal(
            ["endpoint-requirement-changed"],
            result.Violations.Select(static violation => violation.Code));
    }

    [Fact]
    public void RequirementChangedChangesFingerprintWithoutChangingClassification()
    {
        const AuthSurfaceAuthorizationKind beforeKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        const AuthSurfaceAuthorizationKind afterKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        string before = Fingerprint(["permission|read"]);
        string after = Fingerprint(["permission|write"]);

        Assert.Equal(beforeKind, afterKind);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ComparisonIgnoresFingerprintOnlyChange()
    {
        AuthSurfaceEndpoint before = Endpoint(requirementFingerprint: new string('a', 64));
        AuthSurfaceEndpoint after = Endpoint(requirementFingerprint: new string('b', 64));
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(new AuthSurfaceReport([before], []));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([after], []),
            baseline);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void EndpointRemovalIsASeparateViolation()
    {
        AuthSurfaceEndpoint before = Endpoint();
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(new AuthSurfaceReport([before], []));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([], []),
            baseline);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, violation => violation.Code == "endpoint-removed");
    }

    private static AuthSurfaceEndpoint Endpoint(
        AuthSurfaceAuthorizationKind kind = AuthSurfaceAuthorizationKind.ExplicitProtected,
        string[]? policies = null,
        string[]? roles = null,
        string[]? schemes = null,
        bool usesDefaultPolicy = true,
        bool usesFallbackPolicy = false,
        string[]? requirements = null,
        string? requirementFingerprint = null)
    {
        policies ??= ["Read"];
        roles ??= [];
        schemes ??= ["Bearer"];
        requirements ??= ["permission|read"];
        requirementFingerprint ??= AuthSurfaceCanonicalizer.Fingerprint(requirements);

        return new AuthSurfaceEndpoint(
            "/secure",
            "GET",
            kind,
            policies,
            roles,
            schemes,
            usesDefaultPolicy,
            usesFallbackPolicy,
            requirements,
            requirementFingerprint);
    }

    private static string Fingerprint(IEnumerable<string> requirements) =>
        AuthSurfaceCanonicalizer.Fingerprint(requirements);
}
