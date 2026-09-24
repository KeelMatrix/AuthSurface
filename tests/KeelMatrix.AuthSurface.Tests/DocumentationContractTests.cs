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

        Dictionary<string, string> shipped = AuthSurfaceParameterPolicyMatrix.Contracts.ToDictionary(
            static contract => contract.RuntimeType.Name,
            contract => RenderProgrammaticPolicy(contract.PrimaryVariant()),
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

    [Fact]
    public void DocumentedDiagnosticExamplesMatchVerifierOutput()
    {
        string root = FindRepositoryRoot();
        string[] documentation =
        [
            File.ReadAllText(Path.Combine(root, "docs", "api-reference.md")),
            File.ReadAllText(Path.Combine(root, "src", "KeelMatrix.AuthSurface", "README.md")),
        ];

        AuthSurfaceEndpoint expected = Endpoint(
            "/secure",
            AuthSurfaceAuthorizationKind.ExplicitProtected,
            ["Read"],
            ["requirement"]);
        AuthSurfaceEndpoint actual = Endpoint(
            "/secure",
            AuthSurfaceAuthorizationKind.ExplicitAnonymous,
            ["Write"],
            ["changed"],
            usesDefaultPolicy: false);

        AuthSurfaceVerificationResult changes = AuthSurfaceVerifier.Compare(
            new AuthSurfaceReport([actual], []),
            AuthSurfaceBaseline.Create(new AuthSurfaceReport([expected], [])));
        string added = Assert.Single(
            AuthSurfaceVerifier.Compare(
                new AuthSurfaceReport([Endpoint("/internal/export", AuthSurfaceAuthorizationKind.ExplicitProtected)], []),
                AuthSurfaceBaseline.Create(new AuthSurfaceReport([], []))).Violations
                .Where(static violation => violation.Code == "endpoint-added")).Message;
        string removed = Assert.Single(
            AuthSurfaceVerifier.Compare(
                new AuthSurfaceReport([], []),
                AuthSurfaceBaseline.Create(new AuthSurfaceReport([Endpoint("/internal/export", AuthSurfaceAuthorizationKind.ExplicitProtected)], []))).Violations
                .Where(static violation => violation.Code == "endpoint-removed")).Message;

        string[] messages =
        [
            added,
            removed,
            Assert.Single(changes.Violations.Where(static violation => violation.Code == "endpoint-classification-changed")).Message,
            Assert.Single(changes.Violations.Where(static violation => violation.Code == "endpoint-policy-changed")).Message,
            Assert.Single(changes.Violations.Where(static violation => violation.Code == "endpoint-requirement-changed")).Message,
        ];

        foreach (string message in messages)
        {
            Assert.All(documentation, document => Assert.Contains(message, document, StringComparison.Ordinal));
        }
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

    private static AuthSurfaceEndpoint Endpoint(
        string route,
        AuthSurfaceAuthorizationKind kind,
        string[]? policies = null,
        string[]? requirements = null,
        bool usesDefaultPolicy = true)
    {
        policies ??= ["Read"];
        requirements ??= ["permission|read"];
        return new AuthSurfaceEndpoint(
            route,
            "GET",
            kind,
            policies,
            [],
            ["Bearer"],
            usesDefaultPolicy,
            usesFallbackPolicy: false,
            requirements,
            AuthSurfaceCanonicalizer.Fingerprint(requirements));
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
