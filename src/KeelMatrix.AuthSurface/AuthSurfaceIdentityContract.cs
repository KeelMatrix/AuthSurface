namespace KeelMatrix.AuthSurface;

internal enum AuthSurfaceIdentityValidationContext
{
    Analysis,
    Baseline,
}

internal static class AuthSurfaceIdentityContract
{
    internal static void ValidateCollection(
        IEnumerable<AuthSurfaceEndpoint> endpoints,
        AuthSurfaceIdentityValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (AuthSurfaceEndpoint endpoint in endpoints)
        {
            if (context == AuthSurfaceIdentityValidationContext.Baseline)
            {
                string? persistedIdentity = GetPersistedIdentity(endpoint);
                AuthSurfaceCanonicalizer.ValidateWriterReadableIdentity(
                    endpoint.Route,
                    endpoint.Method,
                    persistedIdentity);
            }

            if (identities.Add(endpoint.Identity))
            {
                continue;
            }

            if (context == AuthSurfaceIdentityValidationContext.Baseline)
            {
                throw new AuthSurfaceBaselineException(
                    AuthSurfaceDiagnosticCode.BaselineDuplicateIdentity,
                    "The baseline contains duplicate canonical endpoint identities.");
            }

            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.DuplicateEndpointIdentity,
                $"Duplicate canonical endpoint identity '{endpoint.Route}' [{endpoint.Method}] was found; exclude or disambiguate the endpoint.");
        }
    }

    internal static string? GetPersistedIdentity(AuthSurfaceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.PersistedIdentity is not null)
        {
            return endpoint.PersistedIdentity;
        }

        try
        {
            if (string.Equals(
                endpoint.Identity,
                AuthSurfaceCanonicalizer.CanonicalIdentity(endpoint.Route, endpoint.Method),
                StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is AuthSurfaceAnalysisException or FormatException or InvalidOperationException or ArgumentException or Microsoft.AspNetCore.Routing.Patterns.RoutePatternException)
        {
            // The lossless identity token below is the supported persistence path for
            // programmatic patterns whose readable route cannot be parsed as route syntax.
            // The reader validates this token by re-rendering its route-pattern binding.
        }

        return AuthSurfaceCanonicalizer.CreatePersistedIdentity(endpoint.Route, endpoint.Identity);
    }
}
