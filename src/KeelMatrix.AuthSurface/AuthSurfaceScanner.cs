using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KeelMatrix.AuthSurface;

/// <summary>Discovers completed runtime route endpoints and resolves effective authorization metadata.</summary>
public sealed class AuthSurfaceScanner
{
    private readonly IReadOnlyList<EndpointDataSource> endpointDataSources;
    private readonly IAuthorizationPolicyProvider policyProvider;

    /// <summary>Initializes a scanner from application services.</summary>
    /// <param name="services">The built application's service provider.</param>
    public AuthSurfaceScanner(IServiceProvider services)
        : this(
            services?.GetServices<EndpointDataSource>() ?? throw new ArgumentNullException(nameof(services)),
            services.GetRequiredService<IAuthorizationPolicyProvider>())
    {
    }

    /// <summary>Initializes a scanner from runtime endpoint data sources and the application's policy provider.</summary>
    /// <param name="endpointDataSources">Completed runtime endpoint data sources.</param>
    /// <param name="policyProvider">The application's authorization policy provider.</param>
    public AuthSurfaceScanner(
        IEnumerable<EndpointDataSource> endpointDataSources,
        IAuthorizationPolicyProvider policyProvider)
    {
        ArgumentNullException.ThrowIfNull(endpointDataSources);
        ArgumentNullException.ThrowIfNull(policyProvider);
        this.endpointDataSources = endpointDataSources.ToArray();
        this.policyProvider = policyProvider;
    }

    /// <summary>Scans runtime route endpoints once and returns a deterministic report.</summary>
    /// <param name="options">Optional inclusion and strictness options.</param>
    /// <param name="cancellationToken">Token used to cancel policy resolution.</param>
    /// <returns>The completed scan report.</returns>
    public async Task<AuthSurfaceReport> ScanAsync(
        AuthSurfaceScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AuthSurfaceScanOptions();
        var endpoints = new List<AuthSurfaceEndpoint>();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (EndpointDataSource dataSource in endpointDataSources)
        {
            foreach (Endpoint endpoint in dataSource.Endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (endpoints.Count >= AuthSurfaceCanonicalizer.MaximumEndpointCount)
                {
                    throw new AuthSurfaceAnalysisException(
                        AuthSurfaceDiagnosticCode.EndpointLimit,
                        $"The scan exceeds the supported {AuthSurfaceCanonicalizer.MaximumEndpointCount:N0}-endpoint bound.");
                }

                if (endpoint is not RouteEndpoint routeEndpoint)
                {
                    continue;
                }

                if (options.EndpointFilter is not null && !options.EndpointFilter(routeEndpoint))
                {
                    continue;
                }

                string route = AuthSurfaceCanonicalizer.NormalizeRoute(routeEndpoint.RoutePattern);
                if (options.ExcludedRoutePatterns.Contains(route))
                {
                    continue;
                }

                EndpointAuthorizationFacts facts = await ResolveAuthorizationAsync(
                    routeEndpoint,
                    route,
                    cancellationToken).ConfigureAwait(false);

                foreach (string method in AuthSurfaceCanonicalizer.GetMethods(routeEndpoint))
                {
                    var record = new AuthSurfaceEndpoint(
                        route,
                        method,
                        facts.AuthorizationKind,
                        facts.Policies,
                        facts.Roles,
                        facts.AuthenticationSchemes,
                        facts.UsesDefaultPolicy,
                        facts.UsesFallbackPolicy,
                        facts.Requirements,
                        facts.RequirementFingerprint,
                        AuthSurfaceCanonicalizer.CanonicalIdentity(routeEndpoint.RoutePattern, method));

                    if (!identities.Add(record.Identity))
                    {
                        throw new AuthSurfaceAnalysisException(
                            AuthSurfaceDiagnosticCode.DuplicateEndpointIdentity,
                            $"Duplicate canonical endpoint identity '{route}' [{method}] was found; exclude or disambiguate the endpoint.");
                    }

                    endpoints.Add(record);
                }
            }
        }

        endpoints.Sort(AuthSurfaceCanonicalizer.CompareEndpoints);
        var violations = new List<AuthSurfaceViolation>();
        foreach (AuthSurfaceEndpoint endpoint in endpoints)
        {
            if (endpoint.AuthorizationKind == AuthSurfaceAuthorizationKind.Unprotected)
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        AuthSurfaceDiagnosticCode.UnprotectedEndpoint,
                        $"Endpoint '{endpoint.Route}' [{endpoint.Methods[0]}] is unprotected; add explicit authorization, allow anonymous metadata, or an intentional exclusion.",
                        endpoint.Route,
                        endpoint.Methods[0],
                        expected: "protected-or-explicit-anonymous",
                        actual: endpoint.AuthorizationKind.ToString()));
            }
            else if (options.StrictFallbackPolicy && endpoint.UsesFallbackPolicy)
            {
                violations.Add(
                    new AuthSurfaceViolation(
                        AuthSurfaceDiagnosticCode.FallbackPolicyEndpoint,
                        AuthSurfaceDiagnosticCatalog.FallbackPolicyMessage(endpoint.Route, endpoint.Methods[0]),
                        endpoint.Route,
                        endpoint.Methods[0],
                        expected: "uses-fallback-policy=false",
                        actual: "uses-fallback-policy=true"));
            }
        }

        return new AuthSurfaceReport(endpoints, violations);
    }

    private async Task<EndpointAuthorizationFacts> ResolveAuthorizationAsync(
        RouteEndpoint endpoint,
        string route,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IAuthorizeData> authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        IReadOnlyList<AuthorizationPolicy> explicitPolicies = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();
        IReadOnlyList<IAuthorizationRequirementData> requirementData =
            endpoint.Metadata.GetOrderedMetadata<IAuthorizationRequirementData>();
        bool hasEndpointAuthorization = authorizeData.Count > 0 || explicitPolicies.Count > 0;
        bool hasAnyAuthorizationMetadata = hasEndpointAuthorization || requirementData.Count > 0;
        if (authorizeData.Count + explicitPolicies.Count + requirementData.Count > AuthSurfaceCanonicalizer.MaximumMetadataItems)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.MetadataLimit,
                $"Endpoint '{route}' exceeds the supported authorization metadata bound of {AuthSurfaceCanonicalizer.MaximumMetadataItems:N0} items.");
        }
        bool isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        AuthorizationPolicy? effectivePolicy = null;
        var requirementDataRequirements = new List<IAuthorizationRequirement>();
        var policyContributions = new PolicyContributionTracker(policyProvider);

        if (!isAnonymous || hasAnyAuthorizationMetadata)
        {
            try
            {
                effectivePolicy = await AuthorizationPolicy.CombineAsync(
                    policyContributions,
                    authorizeData,
                    explicitPolicies).WaitAsync(cancellationToken).ConfigureAwait(false);

                foreach (IAuthorizationRequirementData metadata in requirementData)
                {
                    foreach (IAuthorizationRequirement requirement in metadata.GetRequirements())
                    {
                        if (requirementDataRequirements.Count >= AuthSurfaceCanonicalizer.MaximumMetadataItems)
                        {
                            throw new AuthSurfaceAnalysisException(
                                AuthSurfaceDiagnosticCode.MetadataLimit,
                                $"Endpoint '{route}' exceeds the supported authorization requirement-data bound of {AuthSurfaceCanonicalizer.MaximumMetadataItems:N0} requirements.");
                        }

                        requirementDataRequirements.Add(requirement);
                    }
                }

                if (requirementData.Count > 0)
                {
                    var requirementPolicyBuilder = new AuthorizationPolicyBuilder();
                    foreach (IAuthorizationRequirement requirement in requirementDataRequirements)
                    {
                        requirementPolicyBuilder.AddRequirements(requirement);
                    }
                    AuthorizationPolicy requirementPolicy = requirementPolicyBuilder.Build();
                    effectivePolicy = effectivePolicy is null
                        ? requirementPolicy
                        : AuthorizationPolicy.Combine(effectivePolicy, requirementPolicy);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException && exception is not AuthSurfaceAnalysisException)
            {
                throw new AuthSurfaceAnalysisException(
                    AuthSurfaceDiagnosticCode.PolicyResolutionFailed,
                    $"Authorization policy resolution failed for '{route}'. Register a resolvable policy provider and retry.");
            }
        }

        AuthSurfaceAuthorizationKind kind;
        bool usesDefault = !isAnonymous && policyContributions.UsedDefaultPolicy;
        bool usesFallback = !isAnonymous && policyContributions.UsedFallbackPolicy;
        if (isAnonymous)
        {
            kind = AuthSurfaceAuthorizationKind.ExplicitAnonymous;
        }
        else if (hasEndpointAuthorization || requirementDataRequirements.Count > 0)
        {
            kind = AuthSurfaceAuthorizationKind.ExplicitProtected;
        }
        else if (usesFallback)
        {
            kind = AuthSurfaceAuthorizationKind.FallbackProtected;
        }
        else
        {
            kind = AuthSurfaceAuthorizationKind.Unprotected;
        }

        string[] policies = AuthSurfaceCanonicalizer.OrderedDistinctNonBlankExact(
            authorizeData.Select(static data => data.Policy).Where(static value => !string.IsNullOrWhiteSpace(value))!);
        string[] rolesFromMetadata = AuthSurfaceCanonicalizer.SplitMetadataValues(
            authorizeData.Select(static data => data.Roles));
        string[] schemesFromMetadata = AuthSurfaceCanonicalizer.SplitMetadataValues(
            authorizeData.Select(static data => data.AuthenticationSchemes));
        string[] policyRoles = effectivePolicy is null
            ? []
            : effectivePolicy.Requirements
                .Where(static requirement => requirement.GetType() == typeof(RolesAuthorizationRequirement))
                .Cast<RolesAuthorizationRequirement>()
                .SelectMany(static requirement => requirement.AllowedRoles)
                .ToArray();
        string[] roles = rolesFromMetadata
            .Concat(AuthSurfaceCanonicalizer.OrderedDistinctExact(policyRoles))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] schemes = schemesFromMetadata
            .Concat(AuthSurfaceCanonicalizer.OrderedDistinctExact(effectivePolicy?.AuthenticationSchemes ?? []))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        string[] requirements = AuthSurfaceCanonicalizer.CanonicalizeRequirements(effectivePolicy);
        string fingerprint = AuthSurfaceCanonicalizer.Fingerprint(requirements);

        return new EndpointAuthorizationFacts(
            kind,
            policies,
            roles,
            schemes,
            usesDefault,
            usesFallback,
            requirements,
            fingerprint);
    }

    private sealed class PolicyContributionTracker : IAuthorizationPolicyProvider
    {
        private readonly IAuthorizationPolicyProvider inner;

        public PolicyContributionTracker(IAuthorizationPolicyProvider inner) => this.inner = inner;

        public bool UsedDefaultPolicy { get; private set; }

        public bool UsedFallbackPolicy { get; private set; }

        public bool AllowsCachingPolicies => inner.AllowsCachingPolicies;

        public async Task<AuthorizationPolicy> GetDefaultPolicyAsync()
        {
            AuthorizationPolicy policy = await inner.GetDefaultPolicyAsync().ConfigureAwait(false);
            UsedDefaultPolicy = true;
            return policy;
        }

        public async Task<AuthorizationPolicy?> GetFallbackPolicyAsync()
        {
            AuthorizationPolicy? policy = await inner.GetFallbackPolicyAsync().ConfigureAwait(false);
            UsedFallbackPolicy = policy is not null;
            return policy;
        }

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => inner.GetPolicyAsync(policyName);
    }

    private sealed record EndpointAuthorizationFacts(
        AuthSurfaceAuthorizationKind AuthorizationKind,
        string[] Policies,
        string[] Roles,
        string[] AuthenticationSchemes,
        bool UsesDefaultPolicy,
        bool UsesFallbackPolicy,
        string[] Requirements,
        string RequirementFingerprint);
}
