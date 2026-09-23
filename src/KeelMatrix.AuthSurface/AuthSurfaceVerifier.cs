using System.Text.Json;

namespace KeelMatrix.AuthSurface;

/// <summary>Performs policy and deterministic baseline comparisons.</summary>
public static class AuthSurfaceVerifier
{
    /// <summary>Verifies the scan's default policy without using a baseline.</summary>
    /// <param name="report">The completed scan report.</param>
    /// <returns>The structured verification result.</returns>
    public static AuthSurfaceVerificationResult VerifyPolicy(AuthSurfaceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return EvaluatePolicy(report);
    }

    internal static AuthSurfaceVerificationResult EvaluatePolicy(AuthSurfaceReport report)
    {
        AuthSurfaceVerificationResult result = new(report.PolicyViolations, usedBaseline: false);
        AuthSurfaceTelemetryCoordinator.RecordEvaluation(report.Endpoints.Count, result.Violations.Count, usedBaseline: false);
        return result;
    }

    /// <summary>Compares a scan with a validated baseline.</summary>
    /// <param name="report">The current scan report.</param>
    /// <param name="baseline">The earlier accepted baseline.</param>
    /// <returns>The structured comparison result.</returns>
    public static AuthSurfaceVerificationResult Compare(AuthSurfaceReport report, AuthSurfaceBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(baseline);

        var violations = new List<AuthSurfaceViolation>(report.PolicyViolations);
        Dictionary<string, AuthSurfaceEndpoint> current = report.Endpoints.ToDictionary(
            static endpoint => endpoint.Identity,
            StringComparer.Ordinal);
        Dictionary<string, AuthSurfaceEndpoint> expected = baseline.Endpoints.ToDictionary(
            static endpoint => endpoint.Identity,
            StringComparer.Ordinal);

        foreach ((string identity, AuthSurfaceEndpoint endpoint) in current)
        {
            if (!expected.TryGetValue(identity, out AuthSurfaceEndpoint? expectedEndpoint))
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        AuthSurfaceDiagnosticCode.EndpointAdded,
                        $"Endpoint '{endpoint.Route}' [{endpoint.Method}] was added to the authorization surface.",
                        endpoint.Route,
                        endpoint.Method,
                        expected: "baseline-entry",
                        actual: endpoint.AuthorizationKind.ToString()));
                continue;
            }

            if (!Equivalent(expectedEndpoint, endpoint))
            {
                violations.AddRange(Diff(expectedEndpoint, endpoint));
            }
        }

        foreach ((string identity, AuthSurfaceEndpoint endpoint) in expected)
        {
            if (!current.ContainsKey(identity))
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        AuthSurfaceDiagnosticCode.EndpointRemoved,
                        $"Endpoint '{endpoint.Route}' [{endpoint.Method}] was removed from the authorization surface.",
                        endpoint.Route,
                        endpoint.Method,
                        expected: "current-entry",
                        actual: "missing"));
            }
        }

        violations.Sort(CompareViolations);
        AuthSurfaceVerificationResult result = new(violations, usedBaseline: true);
        AuthSurfaceTelemetryCoordinator.RecordEvaluation(report.Endpoints.Count, result.Violations.Count, usedBaseline: true);
        return result;
    }

    /// <summary>Reads a local baseline and compares it with a scan without writing the file.</summary>
    /// <param name="report">The current scan report.</param>
    /// <param name="baselinePath">The local baseline path.</param>
    /// <returns>The structured comparison result.</returns>
    public static AuthSurfaceVerificationResult Compare(AuthSurfaceReport report, string baselinePath) =>
        Compare(report, AuthSurfaceBaseline.Read(baselinePath));

    private static bool Equivalent(AuthSurfaceEndpoint expected, AuthSurfaceEndpoint actual) =>
        expected.Route == actual.Route &&
        expected.AuthorizationKind == actual.AuthorizationKind &&
        expected.UsesDefaultPolicy == actual.UsesDefaultPolicy &&
        expected.UsesFallbackPolicy == actual.UsesFallbackPolicy &&
        expected.Policies.SequenceEqual(actual.Policies, StringComparer.Ordinal) &&
        expected.Roles.SequenceEqual(actual.Roles, StringComparer.Ordinal) &&
        expected.AuthenticationSchemes.SequenceEqual(actual.AuthenticationSchemes, StringComparer.Ordinal) &&
        expected.Requirements.SequenceEqual(actual.Requirements, StringComparer.Ordinal);

    private static IEnumerable<AuthSurfaceViolation> Diff(AuthSurfaceEndpoint expected, AuthSurfaceEndpoint actual)
    {
        if (expected.Route != actual.Route)
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointRouteChanged,
                "route pattern",
                expected,
                actual,
                expected.Route,
                actual.Route);
        }

        if (expected.AuthorizationKind != actual.AuthorizationKind)
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointClassificationChanged,
                "authorization classification",
                expected,
                actual,
                expected.AuthorizationKind.ToString(),
                actual.AuthorizationKind.ToString());
        }

        if (!expected.Policies.SequenceEqual(actual.Policies, StringComparer.Ordinal))
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointPolicyChanged,
                "named policies",
                expected,
                actual,
                FormatList(expected.Policies),
                FormatList(actual.Policies));
        }

        if (!expected.Roles.SequenceEqual(actual.Roles, StringComparer.Ordinal))
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointRoleChanged,
                "roles",
                expected,
                actual,
                FormatList(expected.Roles),
                FormatList(actual.Roles));
        }

        if (!expected.AuthenticationSchemes.SequenceEqual(actual.AuthenticationSchemes, StringComparer.Ordinal))
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointSchemeChanged,
                "authentication schemes",
                expected,
                actual,
                FormatList(expected.AuthenticationSchemes),
                FormatList(actual.AuthenticationSchemes));
        }

        if (expected.UsesDefaultPolicy != actual.UsesDefaultPolicy)
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointDefaultPolicyChanged,
                "default-policy contribution",
                expected,
                actual,
                expected.UsesDefaultPolicy.ToString().ToLowerInvariant(),
                actual.UsesDefaultPolicy.ToString().ToLowerInvariant());
        }

        if (expected.UsesFallbackPolicy != actual.UsesFallbackPolicy)
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointFallbackPolicyChanged,
                "fallback-policy contribution",
                expected,
                actual,
                expected.UsesFallbackPolicy.ToString().ToLowerInvariant(),
                actual.UsesFallbackPolicy.ToString().ToLowerInvariant());
        }

        if (!expected.Requirements.SequenceEqual(actual.Requirements, StringComparer.Ordinal))
        {
            yield return Change(
                AuthSurfaceDiagnosticCode.EndpointRequirementChanged,
                "effective requirements",
                expected,
                actual,
                FormatRequirements(expected),
                FormatRequirements(actual));
        }
    }

    private static AuthSurfaceViolation Change(
        AuthSurfaceDiagnosticCode code,
        string label,
        AuthSurfaceEndpoint expected,
        AuthSurfaceEndpoint actual,
        string expectedValue,
        string actualValue) =>
        new(
            code,
            $"Endpoint '{actual.Route}' [{actual.Method}] {label} changed from {expectedValue} to {actualValue}.",
            actual.Route,
            actual.Method,
            expectedValue,
            actualValue);

    private static string FormatList(IEnumerable<string> values) =>
        JsonSerializer.Serialize(values.ToArray());

    private static string FormatRequirements(AuthSurfaceEndpoint endpoint) =>
        "requirements=" + FormatList(endpoint.Requirements) +
        "; fingerprint=" + endpoint.RequirementFingerprint;

    private static int CompareViolations(AuthSurfaceViolation left, AuthSurfaceViolation right)
    {
        int route = StringComparer.Ordinal.Compare(left.Route, right.Route);
        if (route != 0)
        {
            return route;
        }

        int method = StringComparer.Ordinal.Compare(left.Method, right.Method);
        return method != 0 ? method : StringComparer.Ordinal.Compare(left.Code, right.Code);
    }
}
