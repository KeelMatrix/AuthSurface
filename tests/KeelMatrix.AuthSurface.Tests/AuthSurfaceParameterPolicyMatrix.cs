using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;

namespace KeelMatrix.AuthSurface.Tests;

internal sealed record AuthSurfaceParameterPolicyContract(
    Type RuntimeType,
    IReadOnlyList<Func<IParameterPolicy>> Variants)
{
    internal Func<IParameterPolicy> PrimaryVariant => Variants[0];
}

internal static class AuthSurfaceParameterPolicyMatrix
{
    private static readonly Dictionary<Type, IReadOnlyList<Func<IParameterPolicy>>> VariantFactories =
        new Dictionary<Type, IReadOnlyList<Func<IParameterPolicy>>>
        {
            [typeof(AlphaRouteConstraint)] = [static () => new AlphaRouteConstraint()],
            [typeof(BoolRouteConstraint)] = [static () => new BoolRouteConstraint()],
            [typeof(CompositeRouteConstraint)] =
            [
                static () => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
                static () => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(3)]),
            ],
            [typeof(DateTimeRouteConstraint)] = [static () => new DateTimeRouteConstraint()],
            [typeof(DecimalRouteConstraint)] = [static () => new DecimalRouteConstraint()],
            [typeof(DoubleRouteConstraint)] = [static () => new DoubleRouteConstraint()],
            [typeof(FileNameRouteConstraint)] = [static () => new FileNameRouteConstraint()],
            [typeof(FloatRouteConstraint)] = [static () => new FloatRouteConstraint()],
            [typeof(GuidRouteConstraint)] = [static () => new GuidRouteConstraint()],
            [typeof(HttpMethodRouteConstraint)] =
            [
                static () => new HttpMethodRouteConstraint(["post", "GET", "POST"]),
                static () => new HttpMethodRouteConstraint(["GET"]),
            ],
            [typeof(IntRouteConstraint)] = [static () => new IntRouteConstraint()],
            [typeof(LengthRouteConstraint)] =
            [
                static () => new LengthRouteConstraint(3, 12),
                static () => new LengthRouteConstraint(3, 3),
                static () => new LengthRouteConstraint(4, 12),
            ],
            [typeof(LongRouteConstraint)] = [static () => new LongRouteConstraint()],
            [typeof(MaxLengthRouteConstraint)] = [static () => new MaxLengthRouteConstraint(12), static () => new MaxLengthRouteConstraint(13)],
            [typeof(MaxRouteConstraint)] = [static () => new MaxRouteConstraint(9), static () => new MaxRouteConstraint(10)],
            [typeof(MinLengthRouteConstraint)] = [static () => new MinLengthRouteConstraint(3), static () => new MinLengthRouteConstraint(4)],
            [typeof(MinRouteConstraint)] = [static () => new MinRouteConstraint(2), static () => new MinRouteConstraint(3)],
            [typeof(NonFileNameRouteConstraint)] = [static () => new NonFileNameRouteConstraint()],
            [typeof(OptionalRouteConstraint)] =
            [
                static () => new OptionalRouteConstraint(new IntRouteConstraint()),
                static () => new OptionalRouteConstraint(new LongRouteConstraint()),
            ],
            [typeof(RangeRouteConstraint)] = [static () => new RangeRouteConstraint(2, 9), static () => new RangeRouteConstraint(3, 9)],
            [typeof(RegexRouteConstraint)] =
            [
                static () => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("^\\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled)),
                static () => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("^\\D+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled)),
            ],
            [typeof(RequiredRouteConstraint)] = [static () => new RequiredRouteConstraint()],
        };

    internal static IReadOnlyList<AuthSurfaceParameterPolicyContract> Contracts { get; } =
        AuthSurfaceParameterPolicyRegistry.Supported
            .Select(spec => new AuthSurfaceParameterPolicyContract(
                spec.RuntimeType,
                VariantFactories.TryGetValue(spec.RuntimeType, out IReadOnlyList<Func<IParameterPolicy>>? variants)
                    ? variants
                    : throw new InvalidOperationException($"No test variants are registered for {spec.RuntimeType.FullName}.")))
            .ToArray();

    internal static IReadOnlySet<Type> VariantTypes => VariantFactories.Keys.ToHashSet();
}
