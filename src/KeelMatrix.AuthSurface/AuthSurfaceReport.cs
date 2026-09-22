using System.Collections.Immutable;

namespace KeelMatrix.AuthSurface;

/// <summary>Contains the complete deterministic result of one runtime endpoint scan.</summary>
public sealed class AuthSurfaceReport
{
    internal AuthSurfaceReport(IEnumerable<AuthSurfaceEndpoint> endpoints, IEnumerable<AuthSurfaceViolation> policyViolations)
    {
        Endpoints = endpoints.Select(static endpoint => endpoint.Clone()).ToImmutableArray();
        PolicyViolations = policyViolations.ToImmutableArray();
    }

    /// <summary>Gets the canonical endpoint records in deterministic order.</summary>
    public IReadOnlyList<AuthSurfaceEndpoint> Endpoints { get; }

    /// <summary>Gets policy violations found during the scan.</summary>
    public IReadOnlyList<AuthSurfaceViolation> PolicyViolations { get; }

    /// <summary>Gets a value indicating whether the default policy check passed.</summary>
    public bool IsPolicyCompliant => PolicyViolations.Count == 0;

    /// <summary>Throws when the default or configured policy check found violations.</summary>
    public void AssertPolicyCompliant()
    {
        if (!IsPolicyCompliant)
        {
            throw new AuthSurfaceVerificationException(
                new AuthSurfaceVerificationResult(PolicyViolations, usedBaseline: false));
        }
    }
}
