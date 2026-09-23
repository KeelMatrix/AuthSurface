using System.Text.RegularExpressions;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class DocumentationContractTests
{
    private const string DiagnosticCodesBegin = "<!-- BEGIN:DIAGNOSTIC-CODES -->";
    private const string DiagnosticCodesEnd = "<!-- END:DIAGNOSTIC-CODES -->";

    [Fact]
    public void CompleteDiagnosticCodeInventoryMatchesEveryMaintainedListingSurface()
    {
        string root = FindRepositoryRoot();
        string[] expected = AuthSurfaceDiagnosticCodes.All.ToArray();
        Assert.Equal(expected.Length, expected.Distinct(StringComparer.Ordinal).Count());

        string[] listingSurfaces =
        [
            Path.Combine(root, "README.md"),
            Path.Combine(root, "src", "KeelMatrix.AuthSurface", "README.md"),
            Path.Combine(root, "docs", "api-reference.md"),
        ];

        foreach (string path in listingSurfaces)
        {
            string[] documented = ExtractDiagnosticCodeInventory(File.ReadAllText(path));
            Assert.Equal(documented.Length, documented.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(expected, documented);
        }
    }

    [Fact]
    public void EndpointIdentityDocumentationMatchesProgrammaticParameterPolicyBehavior()
    {
        string root = FindRepositoryRoot();
        string documentation = File.ReadAllText(Path.Combine(root, "docs", "endpoint-identity.md"));
        Dictionary<string, string> documented = Regex.Matches(
                documentation,
                "^\\| `(?<type>[A-Za-z][A-Za-z0-9]+RouteConstraint)` \\| `(?<identity>[^`]+)` \\|$",
                RegexOptions.Multiline)
            .ToDictionary(
                static match => match.Groups["type"].Value,
                static match => match.Groups["identity"].Value,
                StringComparer.Ordinal);

        IParameterPolicy[] supportedPolicies =
        [
            new AlphaRouteConstraint(),
            new BoolRouteConstraint(),
            new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
            new DateTimeRouteConstraint(),
            new DecimalRouteConstraint(),
            new DoubleRouteConstraint(),
            new FileNameRouteConstraint(),
            new FloatRouteConstraint(),
            new GuidRouteConstraint(),
            new HttpMethodRouteConstraint(["POST", "GET"]),
            new IntRouteConstraint(),
            new LengthRouteConstraint(3, 12),
            new LongRouteConstraint(),
            new MaxLengthRouteConstraint(12),
            new MaxRouteConstraint(9),
            new MinLengthRouteConstraint(3),
            new MinRouteConstraint(2),
            new NonFileNameRouteConstraint(),
            new OptionalRouteConstraint(new IntRouteConstraint()),
            new RangeRouteConstraint(2, 9),
            new RegexRouteConstraint(new Regex("^\\d+$", RegexOptions.None)),
            new RequiredRouteConstraint(),
        ];
        Dictionary<string, string> shipped = supportedPolicies.ToDictionary(
            static policy => policy.GetType().Name,
            static policy => RenderProgrammaticPolicy(policy),
            StringComparer.Ordinal);

        Assert.Equal(
            shipped.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            documented.OrderBy(static pair => pair.Key, StringComparer.Ordinal));

        AuthSurfaceAnalysisException exception = Assert.Throws<AuthSurfaceAnalysisException>(
            () => AuthSurfaceCanonicalizer.NormalizeRoute(ProgrammaticPattern(new UnsupportedParameterPolicy())));
        string unsupportedCode = AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy.GetValue();
        Assert.Equal(unsupportedCode, exception.Code);
        Assert.Contains($"`{unsupportedCode}`", documentation, StringComparison.Ordinal);
        Assert.Contains("fails closed", documentation, StringComparison.Ordinal);
        Assert.Contains("not omitted", documentation, StringComparison.Ordinal);
    }

    private static string[] ExtractDiagnosticCodeInventory(string documentation)
    {
        int begin = documentation.IndexOf(DiagnosticCodesBegin, StringComparison.Ordinal);
        int end = documentation.IndexOf(DiagnosticCodesEnd, StringComparison.Ordinal);
        Assert.True(begin >= 0, $"Missing marker {DiagnosticCodesBegin}.");
        Assert.True(end > begin, $"Missing marker {DiagnosticCodesEnd}.");
        Assert.Equal(begin, documentation.LastIndexOf(DiagnosticCodesBegin, StringComparison.Ordinal));
        Assert.Equal(end, documentation.LastIndexOf(DiagnosticCodesEnd, StringComparison.Ordinal));

        string inventory = documentation[(begin + DiagnosticCodesBegin.Length)..end];
        return Regex.Matches(inventory, "^\\| `(?<code>[a-z][a-z0-9-]+)` \\|", RegexOptions.Multiline)
            .Select(static match => match.Groups["code"].Value)
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RenderProgrammaticPolicy(IParameterPolicy policy)
    {
        const string prefix = "/items/{id:";
        string route = AuthSurfaceCanonicalizer.NormalizeRoute(ProgrammaticPattern(policy));
        Assert.StartsWith(prefix, route, StringComparison.Ordinal);
        Assert.EndsWith("}", route, StringComparison.Ordinal);
        return route[prefix.Length..^1];
    }

    private static RoutePattern ProgrammaticPattern(IParameterPolicy policy) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("items")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null!,
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(policy)]),
                ]),
            ]);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeelMatrix.AuthSurface.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed class UnsupportedParameterPolicy : IParameterPolicy
    {
    }
}
