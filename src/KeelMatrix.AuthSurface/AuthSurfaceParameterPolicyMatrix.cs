using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;

namespace KeelMatrix.AuthSurface;

internal sealed record AuthSurfaceParameterPolicyContract(
    Type RuntimeType,
    IReadOnlyList<Func<IParameterPolicy>> Variants)
{
    internal Func<IParameterPolicy> PrimaryVariant => Variants[0];
}

internal static class AuthSurfaceParameterPolicyMatrix
{
    internal static IReadOnlyList<AuthSurfaceParameterPolicyContract> Contracts { get; } =
    [
        new(typeof(AlphaRouteConstraint), [static () => new AlphaRouteConstraint()]),
        new(typeof(BoolRouteConstraint), [static () => new BoolRouteConstraint()]),
        new(
            typeof(CompositeRouteConstraint),
            [
                static () => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
                static () => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(3)]),
            ]),
        new(typeof(DateTimeRouteConstraint), [static () => new DateTimeRouteConstraint()]),
        new(typeof(DecimalRouteConstraint), [static () => new DecimalRouteConstraint()]),
        new(typeof(DoubleRouteConstraint), [static () => new DoubleRouteConstraint()]),
        new(typeof(FileNameRouteConstraint), [static () => new FileNameRouteConstraint()]),
        new(typeof(FloatRouteConstraint), [static () => new FloatRouteConstraint()]),
        new(typeof(GuidRouteConstraint), [static () => new GuidRouteConstraint()]),
        new(
            typeof(HttpMethodRouteConstraint),
            [
                static () => new HttpMethodRouteConstraint(["POST", "GET"]),
                static () => new HttpMethodRouteConstraint(["GET"]),
            ]),
        new(typeof(IntRouteConstraint), [static () => new IntRouteConstraint()]),
        new(typeof(LengthRouteConstraint), [static () => new LengthRouteConstraint(3, 12), static () => new LengthRouteConstraint(4, 12)]),
        new(typeof(LongRouteConstraint), [static () => new LongRouteConstraint()]),
        new(typeof(MaxLengthRouteConstraint), [static () => new MaxLengthRouteConstraint(12), static () => new MaxLengthRouteConstraint(13)]),
        new(typeof(MaxRouteConstraint), [static () => new MaxRouteConstraint(9), static () => new MaxRouteConstraint(10)]),
        new(typeof(MinLengthRouteConstraint), [static () => new MinLengthRouteConstraint(3), static () => new MinLengthRouteConstraint(4)]),
        new(typeof(MinRouteConstraint), [static () => new MinRouteConstraint(2), static () => new MinRouteConstraint(3)]),
        new(typeof(NonFileNameRouteConstraint), [static () => new NonFileNameRouteConstraint()]),
        new(
            typeof(OptionalRouteConstraint),
            [
                static () => new OptionalRouteConstraint(new IntRouteConstraint()),
                static () => new OptionalRouteConstraint(new LongRouteConstraint()),
            ]),
        new(typeof(RangeRouteConstraint), [static () => new RangeRouteConstraint(2, 9), static () => new RangeRouteConstraint(3, 9)]),
        new(
            typeof(RegexRouteConstraint),
            [
                static () => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("^\\d+$", System.Text.RegularExpressions.RegexOptions.None)),
                static () => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("^\\D+$", System.Text.RegularExpressions.RegexOptions.None)),
            ]),
        new(typeof(RequiredRouteConstraint), [static () => new RequiredRouteConstraint()]),
    ];
}
