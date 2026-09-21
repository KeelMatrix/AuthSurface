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
                        "endpoint-added",
                        $"Endpoint '{endpoint.Route}' [{endpoint.Method}] was added to the authorization surface.",
                        endpoint.Route,
                        endpoint.Method,
                        expected: "baseline-entry",
                        actual: endpoint.AuthorizationKind.ToString()));
                continue;
            }

            if (!Equivalent(expectedEndpoint, endpoint))
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        "endpoint-changed",
                        $"Endpoint '{endpoint.Route}' [{endpoint.Method}] authorization metadata changed.",
                        endpoint.Route,
                        endpoint.Method,
                        expected: expectedEndpoint.RequirementFingerprint,
                        actual: endpoint.RequirementFingerprint));
            }
        }

        foreach ((string identity, AuthSurfaceEndpoint endpoint) in expected)
        {
            if (!current.ContainsKey(identity))
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        "endpoint-removed",
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
        expected.AuthorizationKind == actual.AuthorizationKind &&
        expected.UsesDefaultPolicy == actual.UsesDefaultPolicy &&
        expected.UsesFallbackPolicy == actual.UsesFallbackPolicy &&
        expected.RequirementFingerprint == actual.RequirementFingerprint &&
        expected.Policies.SequenceEqual(actual.Policies, StringComparer.Ordinal) &&
        expected.Roles.SequenceEqual(actual.Roles, StringComparer.Ordinal) &&
        expected.AuthenticationSchemes.SequenceEqual(actual.AuthenticationSchemes, StringComparer.Ordinal) &&
        expected.Requirements.SequenceEqual(actual.Requirements, StringComparer.Ordinal);

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
