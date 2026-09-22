using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace KeelMatrix.AuthSurface;

internal static class AuthSurfaceCanonicalizer
{
    internal static string NormalizeRoute(RoutePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        string route = pattern.RawText ?? RenderPattern(pattern);
        route = route.Trim();
        if (route.StartsWith("~/", StringComparison.Ordinal))
        {
            route = route[1..];
        }

        string[] segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    internal static IEnumerable<string> GetMethods(RouteEndpoint endpoint)
    {
        IHttpMethodMetadata? metadata = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
        string[] methods = metadata?.HttpMethods?
            .Where(static method => !string.IsNullOrWhiteSpace(method))
            .Select(static method => method.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static method => method, StringComparer.Ordinal)
            .ToArray() ?? [];

        return methods.Length == 0 ? ["*"] : methods;
    }

    internal static string[] OrderedDistinct(IEnumerable<string?> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    internal static string[] OrderedDistinctExact(IEnumerable<string?> values) =>
        values
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    internal static string[] SplitMetadataValues(IEnumerable<string?> values) =>
        OrderedDistinct(values.SelectMany(static value => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []));

    internal static string[] CanonicalizeRequirements(AuthorizationPolicy? policy)
    {
        if (policy is null)
        {
            return [];
        }

        // AuthorizationPolicy.Requirements is the framework's combined sequence. Preserve its
        // order and duplicate entries because both are part of the effective policy identity.
        return policy.Requirements
            .Select(CanonicalizeRequirement)
            .ToArray();
    }

    internal static int CompareEndpoints(AuthSurfaceEndpoint left, AuthSurfaceEndpoint right)
    {
        int route = StringComparer.Ordinal.Compare(left.Route, right.Route);
        return route != 0 ? route : StringComparer.Ordinal.Compare(left.Method, right.Method);
    }

    internal static string CanonicalIdentity(string route, string method)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(method);

        RoutePattern pattern = RoutePatternFactory.Parse(route);
        return RenderPattern(pattern, caseFoldRouteComponents: true) + "\u001f" + method.ToUpperInvariant();
    }

    internal static string Fingerprint(
        AuthSurfaceAuthorizationKind authorizationKind,
        IEnumerable<string> policies,
        IEnumerable<string> roles,
        IEnumerable<string> schemes,
        bool usesDefaultPolicy,
        bool usesFallbackPolicy,
        IEnumerable<string> requirements)
    {
        string canonical = EncodeSequence(
        [
            authorizationKind.ToString(),
            EncodeSequence(policies),
            EncodeSequence(roles),
            EncodeSequence(schemes),
            usesDefaultPolicy.ToString(CultureInfo.InvariantCulture),
            usesFallbackPolicy.ToString(CultureInfo.InvariantCulture),
            EncodeSequence(requirements),
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static string StableTypeIdentity(Type type) =>
        (type.FullName ?? type.Name) + ", " + (type.Assembly.GetName().Name ?? "unknown");

    private static string CanonicalizeRequirement(IAuthorizationRequirement requirement)
    {
        Type type = requirement.GetType();
        string identity = StableTypeIdentity(type);
        return requirement switch
        {
            DenyAnonymousAuthorizationRequirement => "type=" + EncodeValue(identity),
            RolesAuthorizationRequirement roles => "type=" + EncodeValue(identity) + ";kind=roles;allowed=" + EncodeSequence(OrderedDistinctExact(roles.AllowedRoles)),
            ClaimsAuthorizationRequirement claims => "type=" + EncodeValue(identity) + ";kind=claims;claimType=" + EncodeValue(claims.ClaimType) + ";allowed=" + EncodeSequence(OrderedDistinctExact(claims.AllowedValues ?? [])),
            NameAuthorizationRequirement name => "type=" + EncodeValue(identity) + ";kind=name;required=" + EncodeValue(name.RequiredName),
            OperationAuthorizationRequirement operation => "type=" + EncodeValue(identity) + ";kind=operation;name=" + EncodeValue(operation.Name),
            _ => "type=" + EncodeValue(identity) + "|opaque",
        };
    }

    private static string EncodeValue(string? value) =>
        value is null
            ? "-1:"
            : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string EncodeSequence(IEnumerable<string?> values)
    {
        string?[] valuesArray = values.ToArray();
        return valuesArray.Length.ToString(CultureInfo.InvariantCulture) + "[" +
            string.Concat(valuesArray.Select(EncodeValue)) + "]";
    }

    private static string RenderPattern(RoutePattern pattern, bool caseFoldRouteComponents = false)
    {
        var builder = new StringBuilder();
        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            builder.Append('/');
            foreach (RoutePatternPart part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        builder.Append(caseFoldRouteComponents ? literal.Content.ToUpperInvariant() : literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        builder.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        builder.Append('{');
                        if (parameter.IsCatchAll)
                        {
                            builder.Append(parameter.EncodeSlashes ? '*' : "**");
                        }

                        builder.Append(caseFoldRouteComponents ? parameter.Name.ToUpperInvariant() : parameter.Name);
                        foreach (RoutePatternParameterPolicyReference policy in parameter.ParameterPolicies)
                        {
                            builder.Append(':').Append(policy.Content);
                        }

                        if (parameter.Default is not null)
                        {
                            builder.Append('=').Append(Convert.ToString(parameter.Default, CultureInfo.InvariantCulture));
                        }

                        if (parameter.IsOptional)
                        {
                            builder.Append('?');
                        }

                        builder.Append('}');
                        break;
                }
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }
}
