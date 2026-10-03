using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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
    /// <param name="cancellationToken">Token used to cancel scanner-owned endpoint enumeration, route canonicalization, policy resolution, and checks between requirement-data callback yields; arbitrary callback code is not interruptible.</param>
    /// <returns>The completed scan report.</returns>
    public async Task<AuthSurfaceReport> ScanAsync(
        AuthSurfaceScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AuthSurfaceScanOptions();
        var endpoints = new List<AuthSurfaceEndpoint>();
        var scanBudget = new AuthSurfaceScanBudget();
        int inputEndpointCount = 0;

        foreach (EndpointDataSource dataSource in endpointDataSources)
        {
            foreach (Endpoint endpoint in dataSource.Endpoints)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++inputEndpointCount > AuthSurfaceCanonicalizer.MaximumInputEndpointCount)
                {
                    throw AuthSurfaceCanonicalizer.ResourceLimit(
                        $"The scan input exceeds the supported {AuthSurfaceCanonicalizer.MaximumInputEndpointCount:N0}-endpoint bound.");
                }

                if (endpoint is not RouteEndpoint routeEndpoint)
                {
                    continue;
                }

                if (options.EndpointFilter is not null && !options.EndpointFilter(routeEndpoint))
                {
                    continue;
                }

                string route = AuthSurfaceCanonicalizer.NormalizeRoute(
                    routeEndpoint.RoutePattern,
                    scanBudget,
                    cancellationToken);
                if (options.ExcludedRoutePatterns.Contains(route))
                {
                    continue;
                }

                string[] methods = AuthSurfaceCanonicalizer.GetMethods(routeEndpoint, cancellationToken);
                if (methods.Length > AuthSurfaceCanonicalizer.MaximumEndpointCount - endpoints.Count)
                {
                    throw new AuthSurfaceAnalysisException(
                        AuthSurfaceDiagnosticCode.EndpointLimit,
                        $"The scan would emit more than the supported {AuthSurfaceCanonicalizer.MaximumEndpointCount:N0}-endpoint bound.");
                }

                scanBudget.ConsumeMethods(methods, cancellationToken);

                EndpointAuthorizationFacts facts = await ResolveAuthorizationAsync(
                    routeEndpoint,
                    route,
                    scanBudget,
                    cancellationToken).ConfigureAwait(false);

                foreach (string method in methods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string identity = AuthSurfaceCanonicalizer.CanonicalIdentity(
                        routeEndpoint.RoutePattern,
                        method,
                        scanBudget,
                        cancellationToken);
                    string? persistedIdentity;
                    try
                    {
                        persistedIdentity = string.Equals(
                            identity,
                            AuthSurfaceCanonicalizer.CanonicalIdentity(route, method, cancellationToken),
                            StringComparison.Ordinal)
                            ? null
                            : AuthSurfaceCanonicalizer.CreatePersistedIdentity(
                                routeEndpoint.RoutePattern,
                                route,
                                method,
                                identity,
                                cancellationToken);
                    }
                    catch (Exception exception) when (exception is RoutePatternException or AuthSurfaceAnalysisException or FormatException or InvalidOperationException or ArgumentException)
                    {
                        persistedIdentity = AuthSurfaceCanonicalizer.CreatePersistedIdentity(
                            routeEndpoint.RoutePattern,
                            route,
                            method,
                            identity,
                            cancellationToken);
                    }

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
                        identity,
                        persistedIdentity);

                    endpoints.Add(record);
                }
            }
        }

        AuthSurfaceIdentityContract.ValidateCollection(
            endpoints,
            AuthSurfaceIdentityValidationContext.Analysis);
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
        AuthSurfaceScanBudget scanBudget,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IAuthorizeData> authorizeData = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        IReadOnlyList<AuthorizationPolicy> explicitPolicies = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>();
        IReadOnlyList<IAuthorizationRequirementData> requirementData =
            endpoint.Metadata.GetOrderedMetadata<IAuthorizationRequirementData>();
        IReadOnlyList<IAllowAnonymous> anonymousMetadata = endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>();
        int authorizationMetadataCount = authorizeData.Count + explicitPolicies.Count + requirementData.Count + anonymousMetadata.Count;
        bool hasEndpointAuthorization = authorizeData.Count > 0 || explicitPolicies.Count > 0;
        bool hasAnyAuthorizationMetadata = hasEndpointAuthorization || requirementData.Count > 0;
        if (authorizationMetadataCount > AuthSurfaceCanonicalizer.MaximumMetadataItems)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.MetadataLimit,
                $"Endpoint '{route}' exceeds the supported authorization metadata bound of {AuthSurfaceCanonicalizer.MaximumMetadataItems:N0} items.");
        }
        scanBudget.ConsumeMetadataItems(authorizationMetadataCount);
        bool isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        AuthorizationPolicy? effectivePolicy = null;
        var requirementDataRequirements = new List<IAuthorizationRequirement>();
        var policyContributions = new PolicyContributionTracker(policyProvider);

        try
        {
            effectivePolicy = await AuthorizationPolicy.CombineAsync(
                policyContributions,
                authorizeData,
                explicitPolicies).WaitAsync(cancellationToken).ConfigureAwait(false);

            foreach (IAuthorizationRequirementData metadata in requirementData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<IAuthorizationRequirement> requirementSequence = metadata.GetRequirements();
                if (requirementSequence.TryGetNonEnumeratedCount(out int requirementCount) &&
                    requirementCount > AuthSurfaceCanonicalizer.MaximumMetadataItems - requirementDataRequirements.Count)
                {
                    throw new AuthSurfaceAnalysisException(
                        AuthSurfaceDiagnosticCode.MetadataLimit,
                        $"Endpoint '{route}' exceeds the supported authorization requirement-data bound of {AuthSurfaceCanonicalizer.MaximumMetadataItems:N0} requirements.");
                }

                foreach (IAuthorizationRequirement requirement in requirementSequence)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (requirementDataRequirements.Count >= AuthSurfaceCanonicalizer.MaximumMetadataItems)
                    {
                        throw new AuthSurfaceAnalysisException(
                            AuthSurfaceDiagnosticCode.MetadataLimit,
                            $"Endpoint '{route}' exceeds the supported authorization requirement-data bound of {AuthSurfaceCanonicalizer.MaximumMetadataItems:N0} requirements.");
                    }

                    scanBudget.ConsumeRequirementDataRequirements(1);
                    requirementDataRequirements.Add(requirement);
                }
            }

            if (requirementData.Count > 0)
            {
                var requirementPolicyBuilder = new AuthorizationPolicyBuilder();
                foreach (IAuthorizationRequirement requirement in requirementDataRequirements)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    requirementPolicyBuilder.AddRequirements(requirement);
                }

                AuthorizationPolicy requirementPolicy = requirementPolicyBuilder.Build();
                effectivePolicy = effectivePolicy is null
                    ? requirementPolicy
                    : AuthorizationPolicy.Combine(effectivePolicy, requirementPolicy);
            }

            if (effectivePolicy?.Requirements.Count > AuthSurfaceCanonicalizer.MaximumEffectiveRequirementCount)
            {
                throw AuthSurfaceCanonicalizer.ResourceLimit(
                    $"Endpoint '{route}' exceeds the supported effective authorization requirement bound of {AuthSurfaceCanonicalizer.MaximumEffectiveRequirementCount:N0} requirements.");
            }

            if (effectivePolicy is not null)
            {
                scanBudget.ConsumeEffectiveRequirements(effectivePolicy.Requirements.Count);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException && exception is not AuthSurfaceAnalysisException)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.PolicyResolutionFailed,
                $"Authorization policy resolution failed for '{route}'. Register a resolvable policy provider and retry.");
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

        string[] policies = AuthSurfaceCanonicalizer.BoundedDistinctValues(
            authorizeData.Select(static data => data.Policy),
            splitCommaSeparated: false,
            trimValues: false,
            ignoreBlank: true,
            cancellationToken: cancellationToken,
            scanBudget: scanBudget);
        string[] rolesFromMetadata = AuthSurfaceCanonicalizer.BoundedDistinctValues(
            authorizeData.Select(static data => data.Roles),
            splitCommaSeparated: true,
            trimValues: true,
            ignoreBlank: true,
            cancellationToken: cancellationToken,
            scanBudget: scanBudget);
        string[] schemesFromMetadata = AuthSurfaceCanonicalizer.BoundedDistinctValues(
            authorizeData.Select(static data => data.AuthenticationSchemes),
            splitCommaSeparated: true,
            trimValues: true,
            ignoreBlank: true,
            cancellationToken: cancellationToken,
            scanBudget: scanBudget);
        IEnumerable<string> policyRoles = effectivePolicy is null
            ? []
            : effectivePolicy.Requirements
                .Where(static requirement => requirement.GetType() == typeof(RolesAuthorizationRequirement))
                .Cast<RolesAuthorizationRequirement>()
                .SelectMany(static requirement => requirement.AllowedRoles);
        string[] roles = AuthSurfaceCanonicalizer.BoundedDistinctValues(
            rolesFromMetadata.Cast<string?>().Concat(policyRoles),
            splitCommaSeparated: false,
            trimValues: false,
            ignoreBlank: false,
            cancellationToken: cancellationToken,
            scanBudget: scanBudget);
        string[] schemes = AuthSurfaceCanonicalizer.BoundedDistinctValues(
            schemesFromMetadata.Cast<string?>().Concat(effectivePolicy?.AuthenticationSchemes ?? []),
            splitCommaSeparated: false,
            trimValues: false,
            ignoreBlank: false,
            cancellationToken: cancellationToken,
            scanBudget: scanBudget);
        string[] requirements = AuthSurfaceCanonicalizer.CanonicalizeRequirements(
            effectivePolicy,
            scanBudget,
            cancellationToken);
        string fingerprint = AuthSurfaceCanonicalizer.Fingerprint(requirements, cancellationToken);

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
