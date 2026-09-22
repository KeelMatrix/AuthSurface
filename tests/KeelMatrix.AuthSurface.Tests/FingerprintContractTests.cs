using KeelMatrix.AuthSurface;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class FingerprintContractTests
{
    [Fact]
    public void PolicyAddedChangesFingerprintWithoutChangingClassification()
    {
        const AuthSurfaceAuthorizationKind beforeKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        const AuthSurfaceAuthorizationKind afterKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        string before = Fingerprint(beforeKind, policies: ["Read"]);
        string after = Fingerprint(afterKind, policies: ["Read", "Write"]);

        Assert.Equal(beforeKind, afterKind);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void RoleAddedChangesFingerprintWithoutChangingClassification()
    {
        const AuthSurfaceAuthorizationKind beforeKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        const AuthSurfaceAuthorizationKind afterKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        string before = Fingerprint(beforeKind, roles: ["Reader"]);
        string after = Fingerprint(afterKind, roles: ["Reader", "Writer"]);

        Assert.Equal(beforeKind, afterKind);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void SchemeAddedChangesFingerprintWithoutChangingClassification()
    {
        const AuthSurfaceAuthorizationKind beforeKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        const AuthSurfaceAuthorizationKind afterKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        string before = Fingerprint(beforeKind, schemes: ["Bearer"]);
        string after = Fingerprint(afterKind, schemes: ["Bearer", "Cookies"]);

        Assert.Equal(beforeKind, afterKind);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void RequirementChangedChangesFingerprintWithoutChangingClassification()
    {
        const AuthSurfaceAuthorizationKind beforeKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        const AuthSurfaceAuthorizationKind afterKind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        string before = Fingerprint(beforeKind, requirements: ["permission|read"]);
        string after = Fingerprint(afterKind, requirements: ["permission|write"]);

        Assert.Equal(beforeKind, afterKind);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ComparisonReportsFingerprintOnlyChange()
    {
        AuthSurfaceEndpoint before = Endpoint(requirementFingerprint: new string('a', 64));
        AuthSurfaceEndpoint after = Endpoint(requirementFingerprint: new string('b', 64));
        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(new AuthSurfaceReport([before], []));

        AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([after], []),
            baseline);

        Assert.False(result.IsValid);
        AuthSurfaceViolation violation = Assert.Single(result.Violations);
        Assert.Equal("endpoint-requirement-changed", violation.Code);
        Assert.Contains("fingerprint=", violation.Expected, StringComparison.Ordinal);
        Assert.Contains("fingerprint=", violation.Actual, StringComparison.Ordinal);
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
        string[]? policies = null,
        string[]? roles = null,
        string[]? schemes = null,
        string[]? requirements = null,
        string? requirementFingerprint = null)
    {
        policies ??= ["Read"];
        roles ??= [];
        schemes ??= ["Bearer"];
        requirements ??= ["permission|read"];
        requirementFingerprint ??= AuthSurfaceCanonicalizer.Fingerprint(
            AuthSurfaceAuthorizationKind.ExplicitProtected,
            policies,
            roles,
            schemes,
            usesDefaultPolicy: true,
            usesFallbackPolicy: false,
            requirements);

        return new AuthSurfaceEndpoint(
            "/secure",
            "GET",
            AuthSurfaceAuthorizationKind.ExplicitProtected,
            policies,
            roles,
            schemes,
            usesDefaultPolicy: true,
            usesFallbackPolicy: false,
            requirements,
            requirementFingerprint);
    }

    private static string Fingerprint(
        AuthSurfaceAuthorizationKind kind,
        IEnumerable<string>? policies = null,
        IEnumerable<string>? roles = null,
        IEnumerable<string>? schemes = null,
        IEnumerable<string>? requirements = null) =>
        AuthSurfaceCanonicalizer.Fingerprint(
            kind,
            policies ?? [],
            roles ?? [],
            schemes ?? [],
            usesDefaultPolicy: true,
            usesFallbackPolicy: false,
            requirements ?? []);
}
