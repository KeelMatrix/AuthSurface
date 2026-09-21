namespace KeelMatrix.AuthSurface;

/// <summary>Represents one canonical runtime route and HTTP method authorization contract.</summary>
public sealed class AuthSurfaceEndpoint
{
    internal AuthSurfaceEndpoint(
        string route,
        string method,
        AuthSurfaceAuthorizationKind authorizationKind,
        IEnumerable<string> policies,
        IEnumerable<string> roles,
        IEnumerable<string> authenticationSchemes,
        bool usesDefaultPolicy,
        bool usesFallbackPolicy,
        IEnumerable<string> requirements,
        string requirementFingerprint)
    {
        Route = route;
        Methods = [method];
        AuthorizationKind = authorizationKind;
        Policies = policies.ToArray();
        Roles = roles.ToArray();
        AuthenticationSchemes = authenticationSchemes.ToArray();
        UsesDefaultPolicy = usesDefaultPolicy;
        UsesFallbackPolicy = usesFallbackPolicy;
        Requirements = requirements.ToArray();
        RequirementFingerprint = requirementFingerprint;
    }

    /// <summary>Gets the normalized route pattern.</summary>
    public string Route { get; }

    /// <summary>Gets the sorted HTTP method contract. A method-less endpoint uses <c>*</c>.</summary>
    public IReadOnlyList<string> Methods { get; }

    /// <summary>Gets the effective authorization classification.</summary>
    public AuthSurfaceAuthorizationKind AuthorizationKind { get; }

    /// <summary>Gets named policy metadata in ordinal order.</summary>
    public IReadOnlyList<string> Policies { get; }

    /// <summary>Gets roles in ordinal order.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <summary>Gets authentication schemes in ordinal order.</summary>
    public IReadOnlyList<string> AuthenticationSchemes { get; }

    /// <summary>Gets a value indicating whether the default policy contributed.</summary>
    public bool UsesDefaultPolicy { get; }

    /// <summary>Gets a value indicating whether the fallback policy contributed.</summary>
    public bool UsesFallbackPolicy { get; }

    /// <summary>Gets canonical supported requirement descriptions.</summary>
    public IReadOnlyList<string> Requirements { get; }

    /// <summary>Gets the SHA-256 fingerprint of the effective authorization requirements.</summary>
    public string RequirementFingerprint { get; }

    internal string Method => Methods[0];

    internal string Identity => AuthSurfaceCanonicalizer.CanonicalIdentity(Route, Method);
}
