using Microsoft.AspNetCore.Routing;

namespace KeelMatrix.AuthSurface;

/// <summary>Controls endpoint inclusion and authorization strictness for a scan.</summary>
public sealed class AuthSurfaceScanOptions
{
    private readonly HashSet<string> excludedRoutePatterns = new(StringComparer.Ordinal);

    /// <summary>Initializes scan options with optional route-pattern exclusions.</summary>
    /// <param name="excludedRoutePatterns">Route patterns that should not be included.</param>
    /// <param name="strictFallbackPolicy">Whether fallback-protected endpoints should be violations.</param>
    public AuthSurfaceScanOptions(
        IEnumerable<string>? excludedRoutePatterns = null,
        bool strictFallbackPolicy = false)
    {
        if (excludedRoutePatterns is not null)
        {
            foreach (string pattern in excludedRoutePatterns)
            {
                AddExcludedRoutePattern(pattern);
            }
        }

        StrictFallbackPolicy = strictFallbackPolicy;
    }

    /// <summary>Gets the route patterns excluded from this scan.</summary>
    public IReadOnlySet<string> ExcludedRoutePatterns => excludedRoutePatterns;

    /// <summary>Gets or sets whether fallback-protected endpoints are rejected.</summary>
    public bool StrictFallbackPolicy { get; set; }

    /// <summary>
    /// Gets or sets an optional runtime endpoint filter. Returning <see langword="false"/> excludes the endpoint.
    /// </summary>
    public Func<RouteEndpoint, bool>? EndpointFilter { get; set; }

    /// <summary>Adds a normalized route pattern to the exclusion set.</summary>
    /// <param name="routePattern">The route pattern to exclude.</param>
    public void AddExcludedRoutePattern(string routePattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePattern);
        excludedRoutePatterns.Add(routePattern);
    }
}
