using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;

namespace KeelMatrix.AuthSurface;

internal sealed class AuthSurfaceParameterPolicySpec
{
    private readonly Func<IParameterPolicy, string, string> renderer;

    internal AuthSurfaceParameterPolicySpec(
        Type runtimeType,
        Func<IParameterPolicy, string, string> renderer)
    {
        RuntimeType = runtimeType;
        this.renderer = renderer;
    }

    internal Type RuntimeType { get; }

    internal string Render(IParameterPolicy policy, string parameterName) => renderer(policy, parameterName);
}

internal static class AuthSurfaceParameterPolicyRegistry
{
    private const RegexOptions FrameworkInlineRegexOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string RegexToken = "regex64";
    private const string RegexOptionsSuffix = ";options=521";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static IReadOnlyList<AuthSurfaceParameterPolicySpec> Supported { get; } =
    [
        new(typeof(AlphaRouteConstraint), static (_, _) => "alpha"),
        new(typeof(BoolRouteConstraint), static (_, _) => "bool"),
        new(typeof(CompositeRouteConstraint), static (policy, parameterName) =>
            RenderCompositePolicy(((CompositeRouteConstraint)policy).Constraints, parameterName, "composite")),
        new(typeof(DateTimeRouteConstraint), static (_, _) => "datetime"),
        new(typeof(DecimalRouteConstraint), static (_, _) => "decimal"),
        new(typeof(DoubleRouteConstraint), static (_, _) => "double"),
        new(typeof(FileNameRouteConstraint), static (_, _) => "file"),
        new(typeof(FloatRouteConstraint), static (_, _) => "float"),
        new(typeof(GuidRouteConstraint), static (_, _) => "guid"),
        new(typeof(HttpMethodRouteConstraint), static (policy, _) => RenderHttpMethodPolicy((HttpMethodRouteConstraint)policy, null)),
        new(typeof(IntRouteConstraint), static (_, _) => "int"),
        new(typeof(LengthRouteConstraint), static (policy, _) =>
        {
            LengthRouteConstraint constraint = (LengthRouteConstraint)policy;
            return $"length({constraint.MinLength.ToString(CultureInfo.InvariantCulture)},{constraint.MaxLength.ToString(CultureInfo.InvariantCulture)})";
        }),
        new(typeof(LongRouteConstraint), static (_, _) => "long"),
        new(typeof(MaxLengthRouteConstraint), static (policy, _) => $"maxlength({((MaxLengthRouteConstraint)policy).MaxLength.ToString(CultureInfo.InvariantCulture)})"),
        new(typeof(MaxRouteConstraint), static (policy, _) => $"max({((MaxRouteConstraint)policy).Max.ToString(CultureInfo.InvariantCulture)})"),
        new(typeof(MinLengthRouteConstraint), static (policy, _) => $"minlength({((MinLengthRouteConstraint)policy).MinLength.ToString(CultureInfo.InvariantCulture)})"),
        new(typeof(MinRouteConstraint), static (policy, _) => $"min({((MinRouteConstraint)policy).Min.ToString(CultureInfo.InvariantCulture)})"),
        new(typeof(NonFileNameRouteConstraint), static (_, _) => "nonfile"),
        new(typeof(OptionalRouteConstraint), static (policy, parameterName) =>
            RenderCompositePolicy([((OptionalRouteConstraint)policy).InnerConstraint], parameterName, "optional")),
        new(typeof(RangeRouteConstraint), static (policy, _) =>
        {
            RangeRouteConstraint constraint = (RangeRouteConstraint)policy;
            return $"range({constraint.Min.ToString(CultureInfo.InvariantCulture)},{constraint.Max.ToString(CultureInfo.InvariantCulture)})";
        }),
        new(typeof(RegexRouteConstraint), static (policy, parameterName) =>
        {
            RegexRouteConstraint constraint = (RegexRouteConstraint)policy;
            return RenderRegexPolicy(constraint, parameterName);
        }),
        new(typeof(RequiredRouteConstraint), static (_, _) => "required"),
    ];

    private static readonly Dictionary<Type, AuthSurfaceParameterPolicySpec> Specs =
        Supported.ToDictionary(static spec => spec.RuntimeType);

    internal static string Render(
        IParameterPolicy policy,
        string parameterName,
        AuthSurfaceCanonicalizationBudget budget,
        CancellationToken cancellationToken = default)
        => "programmatic:" + RenderUnwrapped(policy, parameterName, budget, depth: 0, cancellationToken);

    internal static IParameterPolicy Create(string content, string parameterName)
    {
        if (!content.StartsWith("programmatic:", StringComparison.Ordinal))
        {
            throw InvalidGeneratedPolicy(content, parameterName);
        }

        IParameterPolicy policy = CreateUnwrapped(content["programmatic:".Length..], parameterName, depth: 0);
        string rendered = Render(policy, parameterName, new AuthSurfaceCanonicalizationBudget());
        if (!string.Equals(rendered, content, StringComparison.Ordinal))
        {
            throw InvalidGeneratedPolicy(content, parameterName);
        }

        return policy;
    }

    private static IParameterPolicy CreateUnwrapped(string expression, string parameterName, int depth)
    {
        if (depth >= AuthSurfaceCanonicalizer.MaximumRoutePolicyDepth)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooDeep();
        }

        if (expression.Length == 0)
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        int open = expression.IndexOf('(');
        if (open < 0)
        {
            return expression switch
            {
                "alpha" => new AlphaRouteConstraint(),
                "bool" => new BoolRouteConstraint(),
                "datetime" => new DateTimeRouteConstraint(),
                "decimal" => new DecimalRouteConstraint(),
                "double" => new DoubleRouteConstraint(),
                "file" => new FileNameRouteConstraint(),
                "float" => new FloatRouteConstraint(),
                "guid" => new GuidRouteConstraint(),
                "int" => new IntRouteConstraint(),
                "long" => new LongRouteConstraint(),
                "nonfile" => new NonFileNameRouteConstraint(),
                "required" => new RequiredRouteConstraint(),
                _ => throw InvalidGeneratedPolicy(expression, parameterName),
            };
        }

        if (open == 0 || !expression.EndsWith(')'))
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        string token = expression[..open];
        string arguments = expression[(open + 1)..^1];
        if (token == RegexToken)
        {
            if (!arguments.EndsWith(RegexOptionsSuffix, StringComparison.Ordinal))
            {
                throw InvalidGeneratedPolicy(expression, parameterName);
            }

            string encodedPattern = arguments[..^RegexOptionsSuffix.Length];
            string pattern = DecodeBase64UrlUtf8(encodedPattern, expression, parameterName);
            try
            {
                return new RegexRouteConstraint(new Regex(pattern, FrameworkInlineRegexOptions));
            }
            catch (ArgumentException)
            {
                throw InvalidGeneratedPolicy(expression, parameterName);
            }
        }

        string[] parts = SplitArguments(arguments, expression, parameterName);
        return token switch
        {
            "composite" => new CompositeRouteConstraint(parts.Select(part => AsRouteConstraint(
                CreateUnwrapped(part, parameterName, depth + 1),
                expression,
                parameterName)).ToArray()),
            "optional" when parts.Length == 1 => new OptionalRouteConstraint(AsRouteConstraint(
                CreateUnwrapped(parts[0], parameterName, depth + 1),
                expression,
                parameterName)),
            "httpMethod" => new HttpMethodRouteConstraint(parts),
            "length" when parts.Length == 2 => new LengthRouteConstraint(
                ParseInt(parts[0], expression, parameterName),
                ParseInt(parts[1], expression, parameterName)),
            "minlength" when parts.Length == 1 => new MinLengthRouteConstraint(
                ParseInt(parts[0], expression, parameterName)),
            "maxlength" when parts.Length == 1 => new MaxLengthRouteConstraint(
                ParseInt(parts[0], expression, parameterName)),
            "min" when parts.Length == 1 => new MinRouteConstraint(
                ParseInt(parts[0], expression, parameterName)),
            "max" when parts.Length == 1 => new MaxRouteConstraint(
                ParseInt(parts[0], expression, parameterName)),
            "range" when parts.Length == 2 => new RangeRouteConstraint(
                ParseInt(parts[0], expression, parameterName),
                ParseInt(parts[1], expression, parameterName)),
            _ => throw InvalidGeneratedPolicy(expression, parameterName),
        };
    }

    private static IRouteConstraint AsRouteConstraint(
        IParameterPolicy policy,
        string expression,
        string parameterName) => policy as IRouteConstraint
            ?? throw InvalidGeneratedPolicy(expression, parameterName);

    private static int ParseInt(string value, string expression, string parameterName) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw InvalidGeneratedPolicy(expression, parameterName);

    private static string[] SplitArguments(string arguments, string expression, string parameterName)
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
            if (arguments.AsSpan(index).StartsWith(RegexToken + "("))
            {
                int regexEnd = FindRegexExpressionEnd(arguments, index, expression, parameterName);
                index = regexEnd;
                continue;
            }

            switch (arguments[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth < 0)
                    {
                        throw InvalidGeneratedPolicy(expression, parameterName);
                    }

                    break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..index]);
                    start = index + 1;
                    break;
            }
        }

        if (depth != 0)
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        parts.Add(arguments[start..]);
        return parts.Select(static part => part.Trim()).ToArray();
    }

    private static int FindRegexExpressionEnd(
        string arguments,
        int start,
        string expression,
        string parameterName)
    {
        string suffix = RegexOptionsSuffix + ")";
        int search = start + RegexToken.Length + 1;
        while (search < arguments.Length)
        {
            int suffixStart = arguments.IndexOf(suffix, search, StringComparison.Ordinal);
            if (suffixStart < 0)
            {
                break;
            }

            int end = suffixStart + suffix.Length;
            if (end == arguments.Length || arguments[end] is ',' or ')')
            {
                return end - 1;
            }

            search = suffixStart + 1;
        }

        throw InvalidGeneratedPolicy(expression, parameterName);
    }

    private static string DecodeBase64UrlUtf8(string encoded, string expression, string parameterName)
    {
        if (encoded.Length % 4 == 1 || encoded.Any(static character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        string padded = encoded
            .Replace('-', '+')
            .Replace('_', '/') +
            new string('=', (4 - encoded.Length % 4) % 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        if (!string.Equals(EncodeBase64Url(bytes), encoded, StringComparison.Ordinal))
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidGeneratedPolicy(expression, parameterName);
        }
    }

    private static string EncodeBase64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string RenderRegexPolicy(RegexRouteConstraint constraint, string parameterName)
    {
        if (constraint.Constraint.Options != FrameworkInlineRegexOptions)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                $"Route parameter '{parameterName}' uses a regex policy with unsupported options '{constraint.Constraint.Options}'; AuthSurface only supports the framework inline regex defaults.");
        }

        try
        {
            return $"{RegexToken}({EncodeBase64Url(StrictUtf8.GetBytes(constraint.Constraint.ToString()))}{RegexOptionsSuffix})";
        }
        catch (EncoderFallbackException)
        {
            throw new AuthSurfaceAnalysisException(
                AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                $"Route parameter '{parameterName}' uses a regex pattern that cannot be represented as UTF-8.");
        }
    }

    private static AuthSurfaceAnalysisException InvalidGeneratedPolicy(
        string content,
        string parameterName) =>
        new(
            AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
            $"Route parameter '{parameterName}' contains an invalid generated policy provenance value '{content}'.");

    private static string RenderUnwrapped(
        IParameterPolicy policy,
        string parameterName,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        cancellationToken.ThrowIfCancellationRequested();
        budget.Visit();
        if (depth >= AuthSurfaceCanonicalizer.MaximumRoutePolicyDepth)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooDeep();
        }

        if (policy.GetType() == typeof(CompositeRouteConstraint))
        {
            return RenderCompositePolicy(
                ((CompositeRouteConstraint)policy).Constraints,
                parameterName,
                "composite",
                budget,
                depth,
                cancellationToken);
        }

        if (policy.GetType() == typeof(OptionalRouteConstraint))
        {
            return RenderCompositePolicy(
                [((OptionalRouteConstraint)policy).InnerConstraint],
                parameterName,
                "optional",
                budget,
                depth,
                cancellationToken);
        }

        if (!Specs.TryGetValue(policy.GetType(), out AuthSurfaceParameterPolicySpec? spec))
        {
            throw Unsupported(policy, parameterName);
        }

        string rendered = spec.RuntimeType == typeof(HttpMethodRouteConstraint)
            ? RenderHttpMethodPolicy((HttpMethodRouteConstraint)policy, budget, cancellationToken)
            : spec.Render(policy, parameterName);
        return rendered;
    }

    private static string RenderHttpMethodPolicy(
        HttpMethodRouteConstraint policy,
        AuthSurfaceCanonicalizationBudget? budget,
        CancellationToken cancellationToken = default)
    {
        var methods = new List<string>();
        int characters = 0;
        foreach (string? method in policy.AllowedMethods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget?.Visit();
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            if (method.Length > AuthSurfaceCanonicalizer.MaximumMetadataValueLength)
            {
                throw AuthSurfaceCanonicalizer.ResourceLimit(
                    "A programmatic HTTP-method constraint value exceeds the supported metadata value length.");
            }

            string normalized = method.Trim().ToUpperInvariant();
            characters = checked(characters + normalized.Length);
            if (characters > AuthSurfaceCanonicalizer.MaximumNestedValueCharacters)
            {
                throw AuthSurfaceCanonicalizer.ResourceLimit(
                    $"A programmatic HTTP-method constraint exceeds the supported {AuthSurfaceCanonicalizer.MaximumNestedValueCharacters:N0}-character bound.");
            }

            if (methods.Count >= AuthSurfaceCanonicalizer.MaximumNestedValueCount)
            {
                throw AuthSurfaceCanonicalizer.ResourceLimit(
                    $"A programmatic HTTP-method constraint exceeds the supported {AuthSurfaceCanonicalizer.MaximumNestedValueCount:N0}-value bound.");
            }

            methods.Add(normalized);
        }

        return "httpMethod(" + string.Join(
            ',',
            methods.Distinct(StringComparer.Ordinal).OrderBy(static method => method, StringComparer.Ordinal)) + ")";
    }

    private static string RenderCompositePolicy(
        IEnumerable<IRouteConstraint> constraints,
        string parameterName,
        string name) => RenderCompositePolicy(
            constraints,
            parameterName,
            name,
            new AuthSurfaceCanonicalizationBudget(),
            depth: 0,
            cancellationToken: default);

    private static string RenderCompositePolicy(
        IEnumerable<IRouteConstraint> constraints,
        string parameterName,
        string name,
        AuthSurfaceCanonicalizationBudget budget,
        int depth,
        CancellationToken cancellationToken)
    {
        var rendered = new List<string>();
        foreach (IRouteConstraint constraint in constraints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (constraint is not IParameterPolicy parameterPolicy)
            {
                throw new AuthSurfaceAnalysisException(
                    AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                    $"Route parameter '{parameterName}' uses a composite constraint with an unsupported member type '{AuthSurfaceCanonicalizer.StableTypeIdentity(constraint.GetType())}'; AuthSurface cannot produce a stable route identity.");
            }

            rendered.Add(RenderUnwrapped(parameterPolicy, parameterName, budget, depth + 1, cancellationToken));
        }

        rendered.Sort(StringComparer.Ordinal);
        return name + "(" + string.Join(',', rendered) + ")";
    }

    internal static AuthSurfaceAnalysisException Unsupported(IParameterPolicy policy, string parameterName) =>
        new(
            AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
            $"Route parameter '{parameterName}' uses unsupported parameter policy type '{AuthSurfaceCanonicalizer.StableTypeIdentity(policy.GetType())}'; AuthSurface cannot produce a stable route identity. Use a parsed route constraint or exclude the endpoint explicitly.");
}
