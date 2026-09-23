using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
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

    internal static string[] OrderedDistinctNonBlankExact(IEnumerable<string?> values) =>
        values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
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

    internal static string Fingerprint(IEnumerable<string> requirements)
    {
        string canonical = EncodeSequence(requirements);
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
                            builder.Append(':').Append(RenderParameterPolicy(policy, parameter.Name));
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

    private static string RenderParameterPolicy(RoutePatternParameterPolicyReference policy, string parameterName)
    {
        if (policy.Content is not null)
        {
            return policy.Content;
        }

        if (policy.ParameterPolicy is null)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                $"Route parameter '{parameterName}' has a parameter policy without stable content; use a supported framework constraint or a parsed route pattern.");
        }

        return RenderParameterPolicy(policy.ParameterPolicy, parameterName);
    }

    private static string RenderParameterPolicy(IParameterPolicy policy, string parameterName) =>
        policy switch
        {
            AlphaRouteConstraint => "alpha",
            BoolRouteConstraint => "bool",
            DateTimeRouteConstraint => "datetime",
            DecimalRouteConstraint => "decimal",
            DoubleRouteConstraint => "double",
            FileNameRouteConstraint => "file",
            FloatRouteConstraint => "float",
            GuidRouteConstraint => "guid",
            IntRouteConstraint => "int",
            LongRouteConstraint => "long",
            NonFileNameRouteConstraint => "nonfile",
            RequiredRouteConstraint => "required",
            MinLengthRouteConstraint constraint => $"minlength({constraint.MinLength.ToString(CultureInfo.InvariantCulture)})",
            MaxLengthRouteConstraint constraint => $"maxlength({constraint.MaxLength.ToString(CultureInfo.InvariantCulture)})",
            LengthRouteConstraint constraint => $"length({constraint.MinLength.ToString(CultureInfo.InvariantCulture)},{constraint.MaxLength.ToString(CultureInfo.InvariantCulture)})",
            MinRouteConstraint constraint => $"min({constraint.Min.ToString(CultureInfo.InvariantCulture)})",
            MaxRouteConstraint constraint => $"max({constraint.Max.ToString(CultureInfo.InvariantCulture)})",
            RangeRouteConstraint constraint => $"range({constraint.Min.ToString(CultureInfo.InvariantCulture)},{constraint.Max.ToString(CultureInfo.InvariantCulture)})",
            HttpMethodRouteConstraint constraint => RenderHttpMethodPolicy(constraint),
            CompositeRouteConstraint constraint => RenderCompositePolicy(constraint.Constraints, parameterName, "composite"),
            OptionalRouteConstraint constraint => RenderCompositePolicy([constraint.InnerConstraint], parameterName, "optional"),
            RegexRouteConstraint constraint => RenderRegexPolicy(constraint),
            _ => throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                $"Route parameter '{parameterName}' uses unsupported parameter policy type '{StableTypeIdentity(policy.GetType())}'; AuthSurface cannot produce a stable route identity. Use a parsed route constraint or exclude the endpoint explicitly."),
        };

    private static string RenderHttpMethodPolicy(HttpMethodRouteConstraint policy) =>
        "httpMethod(" + string.Join(',', policy.AllowedMethods.OrderBy(static method => method, StringComparer.Ordinal)) + ")";

    private static string RenderCompositePolicy(
        IEnumerable<IRouteConstraint> constraints,
        string parameterName,
        string name)
    {
        var rendered = new List<string>();
        foreach (IRouteConstraint constraint in constraints)
        {
            if (constraint is not IParameterPolicy parameterPolicy)
            {
                throw new AuthSurfaceAnalysisException(
                    AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                    $"Route parameter '{parameterName}' uses a composite constraint with an unsupported member type '{StableTypeIdentity(constraint.GetType())}'; AuthSurface cannot produce a stable route identity.");
            }

            rendered.Add(RenderParameterPolicy(parameterPolicy, parameterName));
        }

        return name + "(" + string.Join(',', rendered) + ")";
    }

    private static string RenderRegexPolicy(RegexRouteConstraint policy) =>
        // Regex.ToString() is the framework Regex representation of its stable pattern,
        // not an arbitrary application policy object's diagnostic string.
        "regex(" + policy.Constraint + ";options=" + ((int)policy.Constraint.Options).ToString(CultureInfo.InvariantCulture) + ")";
}
