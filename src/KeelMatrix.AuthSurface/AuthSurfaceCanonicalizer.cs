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
        return RenderPattern(pattern, caseFoldRouteComponents: true, canonicalizePolicies: true) + "\u001f" + method.ToUpperInvariant();
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

    private static string RenderPattern(
        RoutePattern pattern,
        bool caseFoldRouteComponents = false,
        bool canonicalizePolicies = false)
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
                            builder.Append(':').Append(RenderParameterPolicy(policy, parameter.Name, canonicalizePolicies));
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

    private static string RenderParameterPolicy(
        RoutePatternParameterPolicyReference policy,
        string parameterName,
        bool canonicalizePolicies)
    {
        if (policy.Content is not null)
        {
            return canonicalizePolicies ? CanonicalizePolicyContent(policy.Content) : policy.Content;
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
        AuthSurfaceParameterPolicyRegistry.Render(policy, parameterName);

    private static string CanonicalizePolicyContent(string content)
    {
        string trimmed = content.Trim();
        int open = trimmed.IndexOf('(');
        if (open < 1 || !trimmed.EndsWith(')'))
        {
            return CanonicalizePolicyToken(trimmed);
        }

        string token = CanonicalizePolicyToken(trimmed[..open]);
        string arguments = trimmed[(open + 1)..^1];
        string[] parts = SplitPolicyArguments(arguments);
        switch (token)
        {
            case "length" when parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length):
                return $"length({length.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
            case "httpMethod":
                return "httpMethod(" + string.Join(',', parts
                    .Where(static part => !string.IsNullOrWhiteSpace(part))
                    .Select(static part => part.Trim().ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "regex" when parts.Length == 1:
                int optionsMarker = arguments.LastIndexOf(";options=", StringComparison.OrdinalIgnoreCase);
                if (optionsMarker > 0)
                {
                    string regexText = arguments[..optionsMarker];
                    string options = arguments[(optionsMarker + ";options=".Length)..].Trim();
                    return "regex(" + regexText + ";options=" + options + ")";
                }

                return "regex(" + parts[0] + ";options=0)";
            case "regex":
                return "regex(" + string.Join(';', parts) + ")";
            case "composite":
                return "composite(" + string.Join(',', parts.Select(CanonicalizePolicyContent).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "optional" when parts.Length == 1:
                return "optional(" + CanonicalizePolicyContent(parts[0]) + ")";
            default:
                return token + "(" + arguments + ")";
        }
    }

    private static string CanonicalizePolicyToken(string token) =>
        token.Trim() switch
        {
            "ALPHA" or "Alpha" or "alpha" => "alpha",
            "BOOL" or "Bool" or "bool" => "bool",
            "DATETIME" or "DateTime" or "datetime" => "datetime",
            "DECIMAL" or "Decimal" or "decimal" => "decimal",
            "DOUBLE" or "Double" or "double" => "double",
            "FILE" or "File" or "file" => "file",
            "FLOAT" or "Float" or "float" => "float",
            "GUID" or "Guid" or "guid" => "guid",
            "INT" or "Int" or "int" => "int",
            "LONG" or "Long" or "long" => "long",
            "NONFILE" or "NonFile" or "nonfile" => "nonfile",
            "REQUIRED" or "Required" or "required" => "required",
            "MINLENGTH" or "MinLength" or "minlength" => "minlength",
            "MAXLENGTH" or "MaxLength" or "maxlength" => "maxlength",
            "LENGTH" or "Length" or "length" => "length",
            "MIN" or "Min" or "min" => "min",
            "MAX" or "Max" or "max" => "max",
            "RANGE" or "Range" or "range" => "range",
            "HTTPMETHOD" or "HttpMethod" or "httpMethod" or "httpmethod" => "httpMethod",
            "COMPOSITE" or "Composite" or "composite" => "composite",
            "OPTIONAL" or "Optional" or "optional" => "optional",
            "REGEX" or "Regex" or "regex" => "regex",
            _ => token.Trim(),
        };

    private static string[] SplitPolicyArguments(string arguments)
    {
        if (arguments.Length == 0)
        {
            return [];
        }

        var parts = new List<string>();
        int start = 0;
        int depth = 0;
        for (int index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..index].Trim());
                    start = index + 1;
                    break;
            }
        }

        parts.Add(arguments[start..].Trim());
        return parts.ToArray();
    }
}
