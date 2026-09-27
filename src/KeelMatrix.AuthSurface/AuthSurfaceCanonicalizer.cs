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
    internal const int MaximumScanRoutePolicyWork = 100_000;
    internal const int MaximumScanMetadataItems = 100_000;
    internal const int MaximumScanRequirementDataRequirements = 8_192;
    internal const int MaximumPersistedIdentityLength = 65_536;
    internal const int MaximumInputEndpointCount = 100_000;
    internal const int MaximumMethodsPerEndpoint = 1_048_576;
    internal const int MaximumEffectiveRequirementCount = 100_000;
    internal const int MaximumNestedValueCount = 100_000;
    internal const int MaximumMetadataValueLength = 8_192;
    internal const int MaximumNestedValueCharacters = 1_048_576;
    internal const int MaximumCanonicalRequirementCharacters = 1_048_576;

    private const string PersistedIdentityPrefix = "v1:";
    private const char PersistedIdentitySeparator = '\u001e';
    private const char IdentityMethodSeparator = '\u001f';

    private const string TextualPolicyPrefix = "text:";
    private const string ProgrammaticPolicyPrefix = "programmatic:";

    internal static string NormalizeRoute(RoutePattern pattern, CancellationToken cancellationToken = default)
        => NormalizeRoute(pattern, scanBudget: null, cancellationToken);

    internal static string NormalizeRoute(
        RoutePattern pattern,
        AuthSurfaceScanBudget? scanBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var budget = new AuthSurfaceCanonicalizationBudget(scanBudget);
        PersistedIdentityBinding binding = CreatePersistedIdentityBinding(pattern, string.Empty, budget, cancellationToken);
        string route = RenderPersistedIdentityBinding(
            binding,
            canonicalize: false,
            canonicalizationBudget: budget,
            cancellationToken: cancellationToken).Trim();
        if (route.StartsWith("~/", StringComparison.Ordinal))
        {
            route = route[1..];
        }

        if (route.Length > MaximumRoutePatternLength)
        {
            throw RoutePatternTooLarge();
        }

        ValidateRouteShape(route, "route");
        return route.Length == 0 ? "/" : route;
    }

    internal static string[] GetMethods(RouteEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        IHttpMethodMetadata? metadata = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>();
        if (metadata?.HttpMethods is null)
        {
            return ["*"];
        }

        var methods = new List<string>();
        int methodCharacters = 0;
        foreach (string? method in metadata.HttpMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            if (method.Length > MaximumMetadataValueLength)
            {
                throw ResourceLimit("An endpoint HTTP method exceeds the supported metadata value length.");
            }

            string normalizedMethod = method.Trim().ToUpperInvariant();
            methodCharacters = checked(methodCharacters + normalizedMethod.Length);
            if (methodCharacters > MaximumNestedValueCharacters)
            {
                throw ResourceLimit($"An endpoint's HTTP method metadata exceeds the supported {MaximumNestedValueCharacters:N0}-character bound.");
            }

            if (methods.Count >= MaximumMethodsPerEndpoint)
            {
                throw ResourceLimit($"An endpoint exceeds the supported {MaximumMethodsPerEndpoint:N0}-method bound.");
            }

            methods.Add(normalizedMethod);
        }

        string[] normalized = methods.Distinct(StringComparer.Ordinal).OrderBy(static method => method, StringComparer.Ordinal).ToArray();

        return normalized.Length == 0 ? ["*"] : normalized;
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

    internal static string[] CanonicalizeRequirements(
        AuthorizationPolicy? policy,
        AuthSurfaceScanBudget? scanBudget = null,
        CancellationToken cancellationToken = default)
    {
        if (policy is null)
        {
            return [];
        }

        if (policy.Requirements.Count > MaximumEffectiveRequirementCount)
        {
            throw ResourceLimit($"The effective authorization policy exceeds the supported {MaximumEffectiveRequirementCount:N0}-requirement bound.");
        }

        // AuthorizationPolicy.Requirements is the framework's combined sequence. Preserve its
        // order and duplicate entries because both are part of the effective policy identity.
        var requirements = new List<string>(policy.Requirements.Count);
        int characters = 0;
        foreach (IAuthorizationRequirement requirement in policy.Requirements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string canonical = CanonicalizeRequirement(requirement, scanBudget, cancellationToken);
            characters = checked(characters + canonical.Length);
            if (characters > MaximumCanonicalRequirementCharacters)
            {
                throw ResourceLimit($"The canonical authorization requirements exceed the supported {MaximumCanonicalRequirementCharacters:N0}-character bound.");
            }

            scanBudget?.ConsumeCanonicalRequirement(canonical.Length);
            requirements.Add(canonical);
        }

        return requirements.ToArray();
    }

    internal static int CompareEndpoints(AuthSurfaceEndpoint left, AuthSurfaceEndpoint right)
    {
        int route = StringComparer.Ordinal.Compare(left.Route, right.Route);
        return route != 0 ? route : StringComparer.Ordinal.Compare(left.Method, right.Method);
    }

    internal static string CanonicalIdentity(string route, string method, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(method);
        if (route.Length > MaximumRoutePatternLength)
        {
            throw RoutePatternTooLarge();
        }

        return CanonicalIdentity(RoutePatternFactory.Parse(route), method, cancellationToken);
    }

    internal static string CanonicalIdentity(
        RoutePattern pattern,
        string method,
        CancellationToken cancellationToken = default)
        => CanonicalIdentity(pattern, method, scanBudget: null, cancellationToken);

    internal static string CanonicalIdentity(
        RoutePattern pattern,
        string method,
        AuthSurfaceScanBudget? scanBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(method);
        var budget = new AuthSurfaceCanonicalizationBudget(scanBudget);
        PersistedIdentityBinding binding = CreatePersistedIdentityBinding(pattern, method, budget, cancellationToken);
        return RenderPersistedIdentityBinding(
            binding,
            canonicalize: true,
            canonicalizationBudget: budget,
            cancellationToken: cancellationToken) +
            IdentityMethodSeparator + method.ToUpperInvariant();
    }

    internal static string CreatePersistedIdentity(string route, string identity)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(identity);
        ValidateRouteShape(route, "route");
        ValidateIdentityKey(identity, identity[(identity.LastIndexOf(IdentityMethodSeparator) + 1)..]);
        return EncodePersistedIdentity(route, identity);
    }

    internal static string CreatePersistedIdentity(
        RoutePattern pattern,
        string route,
        string method,
        string identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(identity);

        PersistedIdentityBinding binding = CreatePersistedIdentityBinding(
            pattern,
            method,
            new AuthSurfaceCanonicalizationBudget(),
            cancellationToken);
        string renderedRoute = RenderPersistedIdentityBinding(
            binding,
            canonicalize: false,
            canonicalizationBudget: new AuthSurfaceCanonicalizationBudget(),
            cancellationToken: cancellationToken);
        string renderedIdentity = RenderPersistedIdentityBinding(
            binding,
            canonicalize: true,
            canonicalizationBudget: new AuthSurfaceCanonicalizationBudget(),
            cancellationToken: cancellationToken) +
            IdentityMethodSeparator + method.ToUpperInvariant();
        if (!string.Equals(renderedRoute, route, StringComparison.Ordinal) ||
            !string.Equals(renderedIdentity, identity, StringComparison.Ordinal))
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                "The route pattern cannot be represented by the persisted identity binding.");
        }

        binding = binding.WithIdentityRoute(renderedIdentity[..renderedIdentity.LastIndexOf(IdentityMethodSeparator)]);

        string encodedBinding = Convert.ToBase64String(binding.Serialize())
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
        return EncodePersistedIdentity(route, PersistedIdentityBinding.Prefix + encodedBinding);
    }

    internal static string ReadPersistedIdentity(string persistedIdentity, string route, string method)
    {
        ArgumentNullException.ThrowIfNull(persistedIdentity);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(method);

        try
        {
            if (!persistedIdentity.StartsWith(PersistedIdentityPrefix, StringComparison.Ordinal) ||
                persistedIdentity.Length > MaximumPersistedIdentityLength)
            {
                throw new FormatException("The persisted identity prefix or size is invalid.");
            }

            string encoded = persistedIdentity[PersistedIdentityPrefix.Length..];
            if (encoded.Length == 0 || encoded.Any(static character =>
                    !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            {
                throw new FormatException("The persisted identity encoding is invalid.");
            }

            int padding = (4 - encoded.Length % 4) % 4;
            string padded = encoded.Replace("-", "+", StringComparison.Ordinal).Replace("_", "/", StringComparison.Ordinal) + new string('=', padding);
            string payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(Convert.FromBase64String(padded));
            int separator = payload.IndexOf(PersistedIdentitySeparator);
            if (separator <= 0 || separator == payload.Length - 1 ||
                !string.Equals(payload[..separator], route, StringComparison.Ordinal))
            {
                throw new FormatException("The persisted identity route does not match the endpoint route.");
            }

            string identityToken = payload[(separator + 1)..];
            ValidateRouteShape(route, "route");
            if (identityToken.StartsWith(PersistedIdentityBinding.Prefix, StringComparison.Ordinal))
            {
                string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..];
                if (encodedBinding.Length == 0 || encodedBinding.Any(static character =>
                        !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
                {
                    throw new FormatException("The persisted identity binding encoding is invalid.");
                }

                int bindingPadding = (4 - encodedBinding.Length % 4) % 4;
                string paddedBinding = encodedBinding
                    .Replace("-", "+", StringComparison.Ordinal)
                    .Replace("_", "/", StringComparison.Ordinal) + new string('=', bindingPadding);
                PersistedIdentityBinding binding = PersistedIdentityBinding.Deserialize(Convert.FromBase64String(paddedBinding));
                if (!string.Equals(binding.Method, method, StringComparison.Ordinal))
                {
                    throw new FormatException("The persisted identity binding is not bound to the endpoint HTTP method.");
                }

                string renderedRoute = RenderPersistedIdentityBinding(binding, canonicalize: false);
                if (!string.Equals(renderedRoute, route, StringComparison.Ordinal))
                {
                    throw new FormatException("The persisted identity binding does not match the endpoint route.");
                }

                string renderedIdentityRoute = RenderPersistedIdentityBinding(binding, canonicalize: true);
                if (!string.Equals(binding.IdentityRoute, renderedIdentityRoute, StringComparison.Ordinal))
                {
                    throw new FormatException("The persisted identity binding does not match its canonical route representation.");
                }

                string identity = renderedIdentityRoute + IdentityMethodSeparator + method.ToUpperInvariant();
                ValidateIdentityKey(identity, method);
                return identity;
            }

            ValidateIdentityKey(identityToken, method);
            ValidateLegacyIdentityRouteBinding(route, method, identityToken);
            return identityToken;
        }
        catch (AuthSurfaceBaselineException)
        {
            throw;
        }
        catch (AuthSurfaceAnalysisException exception)
        {
            throw new AuthSurfaceBaselineException(
                AuthSurfaceDiagnosticCode.BaselineMalformed,
                "A baseline endpoint has an invalid persisted identity.",
                exception);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or ArgumentException or OverflowException or EndOfStreamException or IOException)
        {
            throw new AuthSurfaceBaselineException(
                AuthSurfaceDiagnosticCode.BaselineMalformed,
                "A baseline endpoint has an invalid persisted identity.",
                exception);
        }
    }

    internal static void ValidateRouteShape(string route, string description)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > MaximumRoutePatternLength || route[0] != '/')
        {
            throw new FormatException($"The persisted {description} is not a bounded absolute route pattern.");
        }

        int depth = 0;
        for (int index = 0; index < route.Length; index++)
        {
            char character = route[index];
            if (char.IsControl(character))
            {
                throw new FormatException($"The persisted {description} contains a control character.");
            }

            if (character == '{' && index + 1 < route.Length && route[index + 1] == '{')
            {
                index++;
                continue;
            }

            if (character == '}' && index + 1 < route.Length && route[index + 1] == '}')
            {
                index++;
                continue;
            }

            if (character == '{')
            {
                depth++;
                if (depth > MaximumRoutePolicyDepth)
                {
                    throw RoutePolicyTooDeep();
                }
            }
            else if (character == '}' && --depth < 0)
            {
                throw new FormatException($"The persisted {description} has an unmatched closing brace.");
            }
        }

        if (depth != 0)
        {
            throw new FormatException($"The persisted {description} has an unmatched opening brace.");
        }
    }

    private static void ValidateIdentityKey(string identity, string method)
    {
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > MaximumPersistedIdentityLength)
        {
            throw new FormatException("The canonical identity is empty or exceeds the supported bound.");
        }

        int separator = identity.LastIndexOf(IdentityMethodSeparator);
        if (separator <= 0 || separator == identity.Length - 1 ||
            !string.Equals(identity[(separator + 1)..], method, StringComparison.Ordinal))
        {
            throw new FormatException("The canonical identity is not bound to the endpoint HTTP method.");
        }

        ValidateRouteShape(identity[..separator], "canonical identity");
    }

    private static void ValidateLegacyIdentityRouteBinding(string route, string method, string identity)
    {
        try
        {
            string expected = CanonicalIdentity(route, method);
            if (!string.Equals(identity, expected, StringComparison.Ordinal))
            {
                throw new FormatException("The persisted identity does not match the endpoint route and HTTP method.");
            }
        }
        catch (RoutePatternException)
        {
            // A legacy token is accepted without a grammar only when its structural route is
            // exactly the stored display route. Any other unparseable legacy representation is
            // ambiguous and must fail closed; new writer output uses the binding format above.
            int separator = identity.LastIndexOf(IdentityMethodSeparator);
            if (separator <= 0 || !string.Equals(identity[..separator], route, StringComparison.Ordinal))
            {
                throw new FormatException("The legacy persisted identity cannot be proven to match an unparseable endpoint route.");
            }
        }
    }

    private static string EncodePersistedIdentity(string route, string identityToken)
    {
        string payload = route + PersistedIdentitySeparator + identityToken;
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        string encoded = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
        string result = PersistedIdentityPrefix + encoded;
        if (result.Length > MaximumPersistedIdentityLength)
        {
            throw RoutePatternTooLarge();
        }

        return result;
    }

    private static PersistedIdentityBinding CreatePersistedIdentityBinding(
        RoutePattern pattern,
        string method,
        AuthSurfaceCanonicalizationBudget budget,
        CancellationToken cancellationToken)
    {
        var segments = new List<PersistedIdentityBinding.Segment>();
        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = new List<PersistedIdentityBinding.Part>();
            foreach (RoutePatternPart part in segment.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        parts.Add(new PersistedIdentityBinding.LiteralPart(literal.Content));
                        break;
                    case RoutePatternSeparatorPart separator:
                        parts.Add(new PersistedIdentityBinding.SeparatorPart(separator.Content));
                        break;
                    case RoutePatternParameterPart parameter:
                        {
                            string? defaultValue = parameter.Default is null
                                ? null
                                : Convert.ToString(parameter.Default, CultureInfo.InvariantCulture) ?? string.Empty;
                            var policies = new List<PersistedIdentityBinding.Policy>();
                            foreach (RoutePatternParameterPolicyReference policy in parameter.ParameterPolicies)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (policy.Content is not null)
                                {
                                    policies.Add(new PersistedIdentityBinding.Policy(isContent: true, content: policy.Content));
                                }
                                else if (policy.ParameterPolicy is not null)
                                {
                                    policies.Add(new PersistedIdentityBinding.Policy(
                                        isContent: false,
                                        content: AuthSurfaceParameterPolicyRegistry.Render(
                                            policy.ParameterPolicy,
                                            parameter.Name,
                                            budget,
                                            cancellationToken)));
                                }
                                else
                                {
                                    throw new AuthSurfaceAnalysisException(
                                        AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                                        $"Route parameter '{parameter.Name}' has a parameter policy without stable content; use a supported framework constraint or a parsed route pattern.");
                                }
                            }

                            parts.Add(new PersistedIdentityBinding.ParameterPart(
                                parameter.Name,
                                parameter.IsCatchAll,
                                parameter.EncodeSlashes,
                                parameter.IsOptional,
                                defaultValue,
                                policies));
                            break;
                        }
                    default:
                        throw new AuthSurfaceAnalysisException(
                            AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                            "The route pattern contains an unsupported route part.");
                }
            }

            segments.Add(new PersistedIdentityBinding.Segment(parts));
        }

        return new PersistedIdentityBinding(method, segments);
    }

    private static string RenderPersistedIdentityBinding(
        PersistedIdentityBinding binding,
        bool canonicalize,
        AuthSurfaceCanonicalizationBudget? canonicalizationBudget = null,
        CancellationToken cancellationToken = default)
    {
        AuthSurfaceCanonicalizationBudget budget = canonicalizationBudget ?? new AuthSurfaceCanonicalizationBudget();
        var builder = new StringBuilder();
        foreach (PersistedIdentityBinding.Segment segment in binding.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.Visit();
            builder.Append('/');
            foreach (PersistedIdentityBinding.Part part in segment.Parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                budget.Visit();
                switch (part)
                {
                    case PersistedIdentityBinding.LiteralPart literal:
                        AppendBounded(
                            builder,
                            EscapeRouteSyntax(literal.Content),
                            canonicalize,
                            budget);
                        break;
                    case PersistedIdentityBinding.SeparatorPart separator:
                        AppendBounded(builder, separator.Content, canonicalize, budget);
                        break;
                    case PersistedIdentityBinding.ParameterPart parameter:
                        builder.Append('{');
                        if (parameter.IsCatchAll)
                        {
                            builder.Append(parameter.EncodeSlashes ? "*" : "**");
                        }

                        AppendBounded(builder, parameter.Name, canonicalize, budget);
                        foreach (PersistedIdentityBinding.Policy policy in parameter.Policies)
                        {
                            builder.Append(':');
                            string policyText = policy.IsContent
                                ? canonicalize
                                    ? CanonicalizePolicyContent(policy.Content, budget, depth: 0, cancellationToken)
                                    : policy.Content
                                : canonicalize
                                    ? CanonicalizeGeneratedPolicyContent(policy.Content, budget, depth: 0, cancellationToken)
                                    : policy.Content;
                            AppendBounded(builder, EscapeRouteSyntax(policyText), false, budget);
                        }

                        if (parameter.Default is not null)
                        {
                            builder.Append('=');
                            AppendBounded(builder, EscapeRouteSyntax(parameter.Default), false, budget);
                        }

                        if (parameter.IsOptional)
                        {
                            builder.Append('?');
                        }

                        builder.Append('}');
                        break;
                    default:
                        throw new FormatException("The persisted identity binding contains an unknown route part.");
                }

                AuthSurfaceCanonicalizationBudget.CheckRouteLength(builder.Length);
            }
        }

        string result = builder.Length == 0 ? "/" : builder.ToString();
        if (!canonicalize)
        {
            result = result.Trim();
            if (result.StartsWith("~/", StringComparison.Ordinal))
            {
                result = result[1..];
            }
        }

        return result.Length == 0 ? "/" : result;
    }

    internal static string Fingerprint(IEnumerable<string> requirements, CancellationToken cancellationToken = default)
    {
        string canonical = EncodeSequence(
            requirements,
            MaximumEffectiveRequirementCount,
            MaximumCanonicalRequirementCharacters,
            cancellationToken);
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

    internal static AuthSurfaceAnalysisException ScanRoutePolicyTooComplex() =>
        new(AuthSurfaceDiagnosticCode.RoutePolicyTooComplex,
            $"The scan-wide route rendering and policy expansion exceeds the supported cumulative work bound of {MaximumScanRoutePolicyWork:N0} operations.");

    internal static AuthSurfaceAnalysisException ResourceLimit(string message) =>
        new(AuthSurfaceDiagnosticCode.ResourceLimit, message);

    internal static string[] BoundedDistinctValues(
        IEnumerable<string?> values,
        bool splitCommaSeparated,
        bool trimValues,
        bool ignoreBlank,
        AuthSurfaceScanBudget? scanBudget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var materialized = new List<string>();
        int characters = 0;
        foreach (string? value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is null || (ignoreBlank && string.IsNullOrWhiteSpace(value)))
            {
                continue;
            }

            if (value.Length > MaximumMetadataValueLength)
            {
                throw ResourceLimit($"An authorization metadata value exceeds the supported {MaximumMetadataValueLength:N0}-character bound.");
            }

            IEnumerable<string> pieces = splitCommaSeparated
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [trimValues ? value.Trim() : value];
            foreach (string piece in pieces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ignoreBlank && string.IsNullOrWhiteSpace(piece))
                {
                    continue;
                }

                if (piece.Length > MaximumMetadataValueLength)
                {
                    throw ResourceLimit($"An authorization metadata value exceeds the supported {MaximumMetadataValueLength:N0}-character bound.");
                }

                string normalized = trimValues ? piece.Trim() : piece;
                characters = checked(characters + normalized.Length);
                if (characters > MaximumNestedValueCharacters)
                {
                    throw ResourceLimit($"Authorization metadata exceeds the supported {MaximumNestedValueCharacters:N0}-character bound.");
                }

                if (materialized.Count >= MaximumNestedValueCount)
                {
                    throw ResourceLimit($"Authorization metadata exceeds the supported {MaximumNestedValueCount:N0}-value bound.");
                }

                scanBudget?.ConsumeNestedValue(normalized.Length);
                materialized.Add(normalized);
            }
        }

        return materialized.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
    }

    internal static string ValidatePersistedMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || method.Length > MaximumMetadataValueLength ||
            method.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new FormatException("The persisted HTTP method is invalid or exceeds the supported bound.");
        }

        string normalized = method.Trim().ToUpperInvariant();
        if (!string.Equals(normalized, method, StringComparison.Ordinal))
        {
            throw new FormatException("The persisted HTTP method must be canonical uppercase text.");
        }

        return normalized;
    }

    private static string CanonicalizeRequirement(
        IAuthorizationRequirement requirement,
        AuthSurfaceScanBudget? scanBudget,
        CancellationToken cancellationToken)
    {
        Type type = requirement.GetType();
        string identity = StableTypeIdentity(type);
        if (identity.Length > MaximumMetadataValueLength)
        {
            throw ResourceLimit("An authorization requirement type identity exceeds the supported metadata value length.");
        }

        if (type == typeof(DenyAnonymousAuthorizationRequirement))
        {
            return "type=" + EncodeValue(identity);
        }

        if (type == typeof(RolesAuthorizationRequirement))
        {
            var roles = (RolesAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=roles;allowed=" + EncodeSequence(
                BoundedDistinctValues(
                    roles.AllowedRoles,
                    splitCommaSeparated: false,
                    trimValues: false,
                    ignoreBlank: false,
                    scanBudget,
                    cancellationToken),
                cancellationToken: cancellationToken);
        }

        if (type == typeof(ClaimsAuthorizationRequirement))
        {
            var claims = (ClaimsAuthorizationRequirement)requirement;
            scanBudget?.ConsumeNestedValue(claims.ClaimType.Length);
            return "type=" + EncodeValue(identity) + ";kind=claims;claimType=" + EncodeValue(BoundedRequirementValue(claims.ClaimType)) + ";allowed=" + EncodeSequence(
                BoundedDistinctValues(
                    claims.AllowedValues ?? [],
                    splitCommaSeparated: false,
                    trimValues: false,
                    ignoreBlank: false,
                    scanBudget,
                    cancellationToken),
                cancellationToken: cancellationToken);
        }

        if (type == typeof(NameAuthorizationRequirement))
        {
            var name = (NameAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=name;required=" + EncodeValue(BoundedRequirementValue(name.RequiredName));
        }

        if (type == typeof(OperationAuthorizationRequirement))
        {
            var operation = (OperationAuthorizationRequirement)requirement;
            return "type=" + EncodeValue(identity) + ";kind=operation;name=" + EncodeValue(BoundedRequirementValue(operation.Name));
        }

        // Derived/custom requirements are intentionally opaque. Their complete behavior is not
        // represented, so serializing a recognized base-class value would be misleading.
        return "type=" + EncodeValue(identity) + "|opaque";
    }

    private static string? BoundedRequirementValue(string? value)
    {
        if (value is not null && value.Length > MaximumMetadataValueLength)
        {
            throw ResourceLimit($"An authorization requirement value exceeds the supported {MaximumMetadataValueLength:N0}-character bound.");
        }

        return value;
    }

    private static string EncodeValue(string? value) =>
        value is null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;

    private static string EncodeSequence(
        IEnumerable<string?> values,
        int maximumCount = MaximumNestedValueCount,
        int maximumCharacters = MaximumNestedValueCharacters,
        CancellationToken cancellationToken = default)
    {
        var valuesArray = new List<string?>();
        int characters = 0;
        foreach (string? value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (valuesArray.Count >= maximumCount)
            {
                throw ResourceLimit($"A canonical sequence exceeds the supported {maximumCount:N0}-value bound.");
            }

            if (value is not null)
            {
                characters = checked(characters + value.Length);
                if (characters > maximumCharacters)
                {
                    throw ResourceLimit($"A canonical sequence exceeds the supported {maximumCharacters:N0}-character bound.");
                }
            }

            valuesArray.Add(value);
        }

        var builder = new StringBuilder();
        builder.Append(valuesArray.Count.ToString(CultureInfo.InvariantCulture));
        builder.Append('[');
        foreach (string? value in valuesArray)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(EncodeValue(value));
        }

        builder.Append(']');
        return builder.ToString();
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

    private static string CanonicalizePolicyContent(
        string content,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        string trimmed = content.Trim();
        budget.EnterPolicy(depth, trimmed.Length);
        return TextualPolicyPrefix + CanonicalizeTextualPolicyContent(trimmed, budget, depth, cancellationToken);
    }

    private static string CanonicalizeGeneratedPolicyContent(
        string content,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        string trimmed = content.Trim();
        budget.EnterPolicy(depth, trimmed.Length);
        string payload = trimmed.StartsWith(ProgrammaticPolicyPrefix, StringComparison.Ordinal)
            ? trimmed[ProgrammaticPolicyPrefix.Length..]
            : trimmed;
        return ProgrammaticPolicyPrefix + CanonicalizeProgrammaticPolicyContent(
            payload,
            budget,
            depth,
            cancellationToken);
    }

    private static string CanonicalizeTextualPolicyContent(
        string trimmed,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        int open = trimmed.IndexOf('(');
        if (open < 1 || !trimmed.EndsWith(')'))
        {
            return CanonicalizePolicyToken(trimmed);
        }

        string token = CanonicalizePolicyToken(trimmed[..open]);
        string arguments = trimmed[(open + 1)..^1];
        string[] parts = SplitPolicyArguments(token, arguments, budget, cancellationToken);
        switch (token)
        {
            case "regex" when parts.Length == 1:
                return "regex(" + parts[0] + ")";
            case "length" when parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length):
                return $"length({length.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
            case "httpMethod":
                return "httpMethod(" + string.Join(',', parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(static part => part.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "composite":
                return "composite(" + string.Join(',', parts.Select(part => CanonicalizePolicyContent(part, budget, depth + 1, cancellationToken)).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "optional" when parts.Length == 1:
                return "optional(" + CanonicalizePolicyContent(parts[0], budget, depth + 1, cancellationToken) + ")";
            default:
                return token + "(" + arguments.Trim() + ")";
        }
    }

    private static string CanonicalizeProgrammaticPolicyContent(
        string content,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
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
        string[] parts = SplitPolicyArguments(token, arguments, budget, cancellationToken);
        switch (token)
        {
            case "length" when parts.Length == 1 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int length):
                return $"length({length.ToString(CultureInfo.InvariantCulture)},{length.ToString(CultureInfo.InvariantCulture)})";
            case "httpMethod":
                return "httpMethod(" + string.Join(',', parts.Where(static part => !string.IsNullOrWhiteSpace(part)).Select(static part => part.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "regex":
                return "regex(" + arguments.Trim() + ")";
            case "composite":
                return "composite(" + string.Join(',', parts.Select(part => CanonicalizeProgrammaticPolicyContent(part, budget, depth + 1, cancellationToken)).OrderBy(static part => part, StringComparer.Ordinal)) + ")";
            case "optional" when parts.Length == 1:
                return "optional(" + CanonicalizeProgrammaticPolicyContent(parts[0], budget, depth + 1, cancellationToken) + ")";
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

    private static string[] SplitPolicyArguments(
        string token,
        string arguments,
        AuthSurfaceCanonicalizationBudget budget,
        CancellationToken cancellationToken)
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
            cancellationToken.ThrowIfCancellationRequested();
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
    private readonly AuthSurfaceScanBudget? scanBudget;
    private int work;

    internal AuthSurfaceCanonicalizationBudget(AuthSurfaceScanBudget? scanBudget = null) => this.scanBudget = scanBudget;

    internal void Visit(int amount = 1)
    {
        if (amount < 0 || work > AuthSurfaceCanonicalizer.MaximumRoutePolicyWork - amount)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooComplex();
        }

        scanBudget?.ConsumeRoutePolicyWork(amount);
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
