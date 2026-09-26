using System.Globalization;
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
        new(typeof(HttpMethodRouteConstraint), static (policy, _) => RenderHttpMethodPolicy((HttpMethodRouteConstraint)policy)),
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
            if (constraint.Constraint.Options != FrameworkInlineRegexOptions)
            {
                throw new AuthSurfaceAnalysisException(
                    AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                    $"Route parameter '{parameterName}' uses a regex policy with unsupported options '{constraint.Constraint.Options}'; AuthSurface only supports the framework inline regex defaults.");
            }

            return $"regex({constraint.Constraint};options={((int)constraint.Constraint.Options).ToString(CultureInfo.InvariantCulture)})";
        }),
        new(typeof(RequiredRouteConstraint), static (_, _) => "required"),
    ];

    private static readonly Dictionary<Type, AuthSurfaceParameterPolicySpec> Specs =
        Supported.ToDictionary(static spec => spec.RuntimeType);

    internal static string Render(
        IParameterPolicy policy,
        string parameterName,
        AuthSurfaceCanonicalizationBudget budget)
        => "programmatic:" + RenderUnwrapped(policy, parameterName, budget, depth: 0);

    private static string RenderUnwrapped(
        IParameterPolicy policy,
        string parameterName,
        AuthSurfaceCanonicalizationBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        budget.Visit();
        if (depth >= AuthSurfaceCanonicalizer.MaximumRoutePolicyDepth)
        {
            throw AuthSurfaceCanonicalizer.RoutePolicyTooDeep();
        }

        if (policy is CompositeRouteConstraint composite)
        {
            return RenderCompositePolicy(composite.Constraints, parameterName, "composite", budget, depth);
        }

        if (policy is OptionalRouteConstraint optional)
        {
            return RenderCompositePolicy([optional.InnerConstraint], parameterName, "optional", budget, depth);
        }

        if (!Specs.TryGetValue(policy.GetType(), out AuthSurfaceParameterPolicySpec? spec))
        {
            throw Unsupported(policy, parameterName);
        }

        return spec.Render(policy, parameterName);
    }

    private static string RenderHttpMethodPolicy(HttpMethodRouteConstraint policy) =>
        "httpMethod(" + string.Join(
            ',',
            policy.AllowedMethods
                .Where(static method => !string.IsNullOrWhiteSpace(method))
                .Select(static method => method.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static method => method, StringComparer.Ordinal)) + ")";

    private static string RenderCompositePolicy(
        IEnumerable<IRouteConstraint> constraints,
        string parameterName,
        string name) => RenderCompositePolicy(
            constraints,
            parameterName,
            name,
            new AuthSurfaceCanonicalizationBudget(),
            depth: 0);

    private static string RenderCompositePolicy(
        IEnumerable<IRouteConstraint> constraints,
        string parameterName,
        string name,
        AuthSurfaceCanonicalizationBudget budget,
        int depth)
    {
        var rendered = new List<string>();
        foreach (IRouteConstraint constraint in constraints)
        {
            if (constraint is not IParameterPolicy parameterPolicy)
            {
                throw new AuthSurfaceAnalysisException(
                    AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
                    $"Route parameter '{parameterName}' uses a composite constraint with an unsupported member type '{AuthSurfaceCanonicalizer.StableTypeIdentity(constraint.GetType())}'; AuthSurface cannot produce a stable route identity.");
            }

            rendered.Add(RenderUnwrapped(parameterPolicy, parameterName, budget, depth + 1));
        }

        rendered.Sort(StringComparer.Ordinal);
        return name + "(" + string.Join(',', rendered) + ")";
    }

    internal static AuthSurfaceAnalysisException Unsupported(IParameterPolicy policy, string parameterName) =>
        new(
            AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy,
            $"Route parameter '{parameterName}' uses unsupported parameter policy type '{AuthSurfaceCanonicalizer.StableTypeIdentity(policy.GetType())}'; AuthSurface cannot produce a stable route identity. Use a parsed route constraint or exclude the endpoint explicitly.");
}
