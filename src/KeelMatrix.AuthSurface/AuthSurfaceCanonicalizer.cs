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
    internal const int MaximumEndpointCount = 100_000;
    internal const int MaximumMetadataItems = 100_000;
    internal const int MaximumRoutePatternLength = 16_384;
    internal const int MaximumRoutePolicyLength = 8_192;
    internal const int MaximumRoutePolicyDepth = 32;
    internal const int MaximumRoutePolicyWork = 100_000;

    private const string TextualPolicyPrefix = "text:";
    private const string ProgrammaticPolicyPrefix = "programmatic:";

    internal static string NormalizeRoute(RoutePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var budget = new AuthSurfaceCanonicalizationBudget();
        string route = RenderPattern(pattern, false, false, true, budget).Trim();
        if (route.StartsWith("~/", StringComparison.Ordinal))
        {
            route = route[1..];
        }

        if (route.Length > MaximumRoutePatternLength)
        {
            throw RoutePatternTooLarge();
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
        return policy.Requirements.Select(CanonicalizeRequirement).ToArray();
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
        if (route.Length > MaximumRoutePatternLength)
        {
            throw RoutePatternTooLarge();
        }

        return CanonicalIdentity(RoutePatternFactory.Parse(route), method);
    }

    internal static string CanonicalIdentity(RoutePattern pattern, string method)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(method);
        return RenderPattern(
            pattern,
            caseFoldRouteComponents: true,
            canonicalizePolicies: true,
            escapeRouteSyntax: true,
            new AuthSurfaceCanonicalizationBudget()) + "\u001f" + method.ToUpperInvariant();
    }

    internal static string Fingerprint(IEnumerable<string> requirements)
    {
        string canonical = EncodeSequence(requirements);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal static string StableTypeIdentity(Type type) =>
        (type.FullName ?? type.Name) + ", " + (type.Assembly.GetName().Name ?? "unknown");

    internal static AuthSurfaceAnalysisException RoutePatternTooLarge() =>
        new(AuthSurfaceDiagnosticCode.RoutePatternTooLarge,
            $"The route pattern exceeds the supported {MaximumRoutePatternLength:N0}-character bound.");

    internal static AuthSurfaceAnalysisException RoutePolicyTooDeep() =>
        new(AuthSurfaceDiagnosticCode.RoutePolicyTooDeep,
            $"The route policy expression exceeds the supported nesting depth of {MaximumRoutePolicyDepth:N0}.");

    internal static AuthSurfaceAnalysisException RoutePolicyTooComplex() =>
        new(AuthSurfaceDiagnosticCode.RoutePolicyTooComplex,
            $"The route policy expression exceeds the supported work bound of {MaximumRoutePolicyWork:N0} operations.");

    private static string CanonicalizeRequirement(IAuthorizationRequirement requirement)
    {
        Type type = requirement.GetType();
        string identity = StableTypeIdentity(type);
        if (type == typeof(DenyAnonymousAuthorizationRequirement))
        {
            return "type=" + EncodeValue(identity);
        }

        if (type == typeof(RolesAuthorizationRequirement))
        {
            var roles = (RolesAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=roles;allowed=" + EncodeSequence(OrderedDistinctExact(roles.AllowedRoles));
        }

        if (type == typeof(ClaimsAuthorizationRequirement))
        {
            var claims = (ClaimsAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=claims;claimType=" + EncodeValue(claims.ClaimType) + ";allowed=" + EncodeSequence(OrderedDistinctExact(claims.AllowedValues ?? []));
        }

        if (type == typeof(NameAuthorizationRequirement))
        {
            var name = (NameAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=name;required=" + EncodeValue(name.RequiredName);
        }

        if (type == typeof(OperationAuthorizationRequirement))
        {
            var operation = (OperationAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=operation;name=" + EncodeValue(operation.Name);
        }

        // Derived/custom requirements are intentionally opaque. Their complete behavior is not
        // represented, so serializing a recognized base-class value would be misleading.
        return "type=" + EncodeValue(identity) + "|opaque";
    }

    private static string EncodeValue(string? value) =>
        value is null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string EncodeSequence(IEnumerable<string?> values)
    {
        string?[] valuesArray = values.ToArray();
        return valuesArray.Length.ToString(CultureInfo.InvariantCulture) + "[" +
            string.Concat(valuesArray.Select(EncodeValue)) + "]";
    }

    private static string RenderPattern(
        RoutePattern pattern,
        bool caseFoldRouteComponents,
        bool canonicalizePolicies,
        bool escapeRouteSyntax,
        AuthSurfaceCanonicalizationBudget budget)
    {
        var builder = new StringBuilder();
        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            budget.Visit();
            builder.Append('/');
            foreach (RoutePatternPart part in segment.Parts)
            {
                budget.Visit();
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        AppendBounded(builder, escapeRouteSyntax ? EscapeRouteSyntax(literal.Content) : literal.Content, caseFoldRouteComponents, budget);
                        break;
                    case RoutePatternSeparatorPart separator:
                        AppendBounded(builder, separator.Content, caseFoldRouteComponents, budget);
                        break;
                    case RoutePatternParameterPart parameter:
                        builder.Append('{');
                        if (parameter.IsCatchAll)
                        {
                            builder.Append(parameter.EncodeSlashes ? '*' : "**");
                        }

                        AppendBounded(builder, parameter.Name, caseFoldRouteComponents, budget);
                        foreach (RoutePatternParameterPolicyReference policy in parameter.ParameterPolicies)
                        {
                            builder.Append(':');
                            string policyText = RenderParameterPolicy(policy, parameter.Name, canonicalizePolicies, budget);
                            AppendBounded(builder, escapeRouteSyntax ? EscapeRouteSyntax(policyText) : policyText, false, budget);
                        }

                        if (parameter.Default is not null)
                        {
                            builder.Append('=');
                            string defaultValue = Convert.ToString(parameter.Default, CultureInfo.InvariantCulture) ?? string.Empty;
                            AppendBounded(builder, escapeRouteSyntax ? EscapeRouteSyntax(defaultValue) : defaultValue, false, budget);
                        }

                        if (parameter.IsOptional)
                        {
                            builder.Append('?');
                        }

                        builder.Append('}');
                        break;
                }

                AuthSurfaceCanonicalizationBudget.CheckRouteLength(builder.Length);
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }

    private static void AppendBounded(
        StringBuilder builder,
        string value,
        bool caseFold,
        AuthSurfaceCanonicalizationBudget budget)
    {
        builder.Append(caseFold ? value.ToUpperInvariant() : value);
        AuthSurfaceCanonicalizationBudget.CheckRouteLength(builder.Length);
    }

    private static string EscapeRouteSyntax(string value) =>
        value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    private static string RenderParameterPolicy(
        RoutePatternParameterPolicyReference policy,
        string parameterName,
        bool canonicalizePolicies,
        AuthSurfaceCanonicalizationBudget budget)
    {
        if (policy.Content is not null)
        {
            return canonicalizePolicies
                ? CanonicalizePolicyContent(policy.Content, budget, depth: 0)
                : policy.Content;
        }

        if (policy.ParameterPolicy is null)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                $"Route parameter '{parameterName}' has a parameter policy without stable content; use a supported framework constraint or a parsed route pattern.");
        }

        string rendered = AuthSurfaceParameterPolicyRegistry.Render(policy.ParameterPolicy, parameterName, budget);
        return canonicalizePolicies ? CanonicalizePolicyContent(rendered, budget, depth: 0) : rendered;
    }

    private static string CanonicalizePolicyContent(string content, AuthSurfaceCanonicalizationBudget budget, int depth)
    {
        string trimmed = content.Trim();
        budget.EnterPolicy(depth, trimmed.Length);
        if (trimmed.StartsWith(ProgrammaticPolicyPrefix, StringComparison.Ordinal))
        {
            return ProgrammaticPolicyPrefix + CanonicalizeProgrammaticPolicyContent(trimmed[ProgrammaticPolicyPrefix.Length..], budget, depth);
        }

        return TextualPolicyPrefix + CanonicalizeTextualPolicyContent(trimmed, budget, depth);
    }

    private static string CanonicalizeTextualPolicyContent(string trimmed, AuthSurfaceCanonicalizationBudget budget, int depth)
    {
        int open = trimmed.IndexOf('(');
        if (open < 1 || !trimmed.EndsWith(')'))
        {
            return CanonicalizePolicyToken(trimmed);
        }

        string token = CanonicalizePolicyToken(trimmed[..open]);
        string arguments = trimmed[(open + 1)..^1];
        string[] parts = SplitPolicyArguments(token, arguments, budget);
        switch (token)
        {
            case "regex" when parts.Length == 1:
                return "regex(" + parts[0] + ")";
            case "length" when parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length):
                return $"length({length.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
            case "httpMethod":
                return "httpMethod(" + string.Join(',', parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(static part => part.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "composite":
                return "composite(" + string.Join(',', parts.Select(part => CanonicalizePolicyContent(part, budget, depth + 1)).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "optional" when parts.Length == 1:
                return "optional(" + CanonicalizePolicyContent(parts[0], budget, depth + 1) + ")";
            default:
                return token + "(" + arguments.Trim() + ")";
        }
    }

    private static string CanonicalizeProgrammaticPolicyContent(string content, AuthSurfaceCanonicalizationBudget budget, int depth)
    {
        string trimmed = content.Trim();
        budget.EnterPolicy(depth, trimmed.Length);
        int open = trimmed.IndexOf('(');
        if (open < 1 || !trimmed.EndsWith(')'))
        {
            return CanonicalizePolicyToken(trimmed);
        }

        string token = CanonicalizePolicyToken(trimmed[..open]);
        string arguments = trimmed[(open + 1)..^1];
        string[] parts = SplitPolicyArguments(token, arguments, budget);
        switch (token)
        {
            case "length" when parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length):
                return $"length({length.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
            case "httpMethod":
                return "httpMethod(" + string.Join(',', parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(static part => part.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "regex":
                return "regex(" + arguments.Trim() + ")";
            case "composite":
                return "composite(" + string.Join(',', parts.Select(part => CanonicalizeProgrammaticPolicyContent(part, budget, depth + 1)).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "optional" when parts.Length == 1:
                return "optional(" + CanonicalizeProgrammaticPolicyContent(parts[0], budget, depth + 1) + ")";
            default:
                return token + "(" + arguments.Trim() + ")";
        }
    }

    private static string CanonicalizePolicyToken(string token) =>
        token.Trim().ToLowerInvariant() switch
        {
            "alpha" => "alpha",
            "bool" => "bool",
            "datetime" => "datetime",
            "decimal" => "decimal",
            "double" => "double",
            "file" => "file",
            "float" => "float",
            "guid" => "guid",
            "int" => "int",
            "long" => "long",
            "nonfile" => "nonfile",
            "required" => "required",
            "minlength" => "minlength",
            "maxlength" => "maxlength",
            "length" => "length",
            "min" => "min",
            "max" => "max",
            "range" => "range",
            "httpmethod" => "httpMethod",
            "composite" => "composite",
            "optional" => "optional",
            "regex" => "regex",
            _ => token.Trim().ToLowerInvariant(),
        };

    private static string[] SplitPolicyArguments(string token, string arguments, AuthSurfaceCanonicalizationBudget budget)
    {
        if (arguments.Length == 0)
        {
            return [];
        }

        budget.Visit(arguments.Length);
        if (token == "regex")
        {
            return [arguments.Trim()];
        }

        var parts = new List<string>();
        int start = 0;
        int depth = 0;
        for (int index = 0; index < arguments.Length; index++)
        {
            budget.Visit();
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

internal sealed class AuthSurfaceCanonicalizationBudget
{
    private int work;

    internal void Visit(int amount = 1)
    {
        if (amount < 0 || work > AuthSurfaceCanonicalizer.MaximumRoutePolicyWork - amount)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooComplex();
        }

        work += amount;
    }

    internal static void CheckRouteLength(int length)
    {
        if (length > AuthSurfaceCanonicalizer.MaximumRoutePatternLength)
        {
            throw AuthSurfaceCanonicalizer.RoutePatternTooLarge();
        }
    }

    internal void EnterPolicy(int depth, int length)
    {
        if (depth >= AuthSurfaceCanonicalizer.MaximumRoutePolicyDepth)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooDeep();
        }

        if (length > AuthSurfaceCanonicalizer.MaximumRoutePolicyLength)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooComplex();
        }

        Visit(Math.Max(length, 1));
    }
}
