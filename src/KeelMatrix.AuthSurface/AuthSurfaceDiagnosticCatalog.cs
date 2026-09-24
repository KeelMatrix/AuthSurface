namespace KeelMatrix.AuthSurface;

internal static class AuthSurfaceDiagnosticCatalog
{
    internal const string FallbackPolicyDescription =
        "Fallback contributed to the effective authorization policy.";

    internal const string FallbackPolicyRemediation =
        "Supply a complete endpoint/default/named/direct policy path that prevents fallback from contributing, or disable strict fallback enforcement intentionally.";

    internal static string FallbackPolicyMessage(string route, string method) =>
        $"Endpoint '{route}' [{method}] {FallbackPolicyDescription.ToLowerInvariant()} Strict mode rejects this contribution. {FallbackPolicyRemediation}";
}
