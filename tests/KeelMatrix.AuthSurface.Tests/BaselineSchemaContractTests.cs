using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AuthSurface.FixtureApp;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class BaselineSchemaContractTests
{
    [Fact]
    public void UnknownTopLevelFieldFailsClosedAndDoesNotRewriteTheFile()
    {
        AssertRejected(
            "{\"schemaVersion\":1,\"endpoints\":[],\"futureField\":true}",
            "baseline-unknown-field");
    }

    [Fact]
    public void DuplicateTopLevelFieldFailsClosedWithActionableDiagnostic()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        const string json = "{\"schemaVersion\":1,\"schemaVersion\":1,\"endpoints\":[]}";
        File.WriteAllText(path, json);
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-duplicate-field", exception.Code);
        Assert.Contains("schemaVersion", exception.Message, StringComparison.Ordinal);
        Assert.Contains("top-level", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void DuplicateTopLevelEndpointsFieldFailsClosed()
    {
        AssertDuplicateFieldRejected(
            "{\"schemaVersion\":1,\"endpoints\":[],\"endpoints\":[]}",
            "endpoints",
            "top-level");
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[{\"route\":\"/first\",\"route\":\"/second\"}]}", "route")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[{\"route\":42,\"route\":\"/second\"}]}", "route")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[{\"route\":\"/first\",\"route\":42}]}", "route")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[{\"methods\":[],\"methods\":[]}]}", "methods")]
    public void DuplicateEndpointFieldFailsClosedRegardlessOfValueTypes(string json, string field)
    {
        AssertDuplicateFieldRejected(json, field, "$.endpoints[0]");
    }

    [Fact]
    public void UnknownEndpointFieldFailsClosedAndDoesNotRewriteTheFile()
    {
        AssertRejected(
            "{\"schemaVersion\":1,\"endpoints\":[{\"futureField\":true}]}",
            "baseline-unknown-endpoint-field");
    }

    [Fact]
    public void KnownFieldWithWrongJsonTypeFailsWithTypeDiagnostic()
    {
        AssertRejected(
            "{\"schemaVersion\":1,\"endpoints\":[{\"route\":42}]}",
            "baseline-field-type");
    }

    [Fact]
    public void NewerSchemaVersionFailsWithForwardCompatibilityDiagnostic()
    {
        AssertRejected(
            "{\"schemaVersion\":2,\"endpoints\":[]}",
            "baseline-schema-unsupported");
    }

    [Fact]
    public void CrLfBaselineInputRemainsReadable()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, "{\r\n  \"schemaVersion\": 1,\r\n  \"endpoints\": []\r\n}\r\n", new UTF8Encoding(false));

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Read(path);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Empty(baseline.Endpoints);
    }

    [Fact]
    public void ValidUtf8BomPrefixedBaselineRemainsReadableWithoutRewriting()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        byte[] bytes = WithUtf8Bom("{\"schemaVersion\":1,\"endpoints\":[]}");
        File.WriteAllBytes(path, bytes);

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Read(path);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Empty(baseline.Endpoints);
        Assert.Equal(bytes, File.ReadAllBytes(path));

        string outputPath = Path.Combine(directory.Path, "written.json");
        baseline.Write(outputPath, overwrite: false);
        byte[] outputBytes = File.ReadAllBytes(outputPath);
        Assert.Equal((byte)'{', outputBytes[0]);
        Assert.True(outputBytes.AsSpan().IndexOf(Encoding.UTF8.GetPreamble()) < 0);
    }

    [Fact]
    public void MalformedUtf8BomPrefixedBaselineFailsClosedWithoutRewriting()
    {
        AssertRejected(WithUtf8Bom("{\"schemaVersion\":1,\"endpoints\":[],}"), "baseline-malformed");
    }

    [Fact]
    public void RawUtf8BomBetweenTokensFailsClosedWithoutRewriting()
    {
        AssertRejected(
            WithUtf8Bom("{\"schemaVersion\":1," + "\uFEFF" + "\"endpoints\":[]}"),
            "baseline-malformed");
    }

    [Fact]
    public void RawUtf8BomInsideRouteStringFailsClosedWithoutRewriting()
    {
        AssertRejected(
            Encoding.UTF8.GetBytes(CreateBaselineJson(route: "/orders" + "\uFEFF")),
            "baseline-malformed");
    }

    [Fact]
    public void RawUtf8BomInsidePropertyNameFailsClosedWithoutRewriting()
    {
        AssertRejected(
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"endpoints\":[],\"future" + "\uFEFF" + "Field\":true}"),
            "baseline-malformed");
    }

    [Fact]
    public void MultipleRawUtf8BomsInDifferentPlacesFailClosedWithoutRewriting()
    {
        string json = "{\"schemaVersion\":1,\"endpoints\":[{" +
            "\"route\":\"/orders" + "\uFEFF" + "\",\"methods\":[\"GET\"]," +
            "\"authorization\":\"ExplicitProtected\",\"policies\":[],\"roles\":[]," +
            "\"schemes\":[],\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false," +
            "\"requirements\":[\"requirement\"],\"requirementFingerprint\":\"" +
            new string('0', 64) + "\"}],\"future" + "\uFEFF" + "Field\":true}";

        AssertRejected(Encoding.UTF8.GetBytes(json), "baseline-malformed");
    }

    [Fact]
    public void Utf8BomOnlyFileFailsClosedWithoutRewriting()
    {
        AssertRejected(Encoding.UTF8.GetPreamble(), "baseline-malformed");
    }

    [Fact]
    public void Utf16LeBomPrefixedBaselineFailsClosedWithoutRewriting()
    {
        byte[] bytes = Encoding.Unicode.GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("{\"schemaVersion\":1,\"endpoints\":[]}"))
            .ToArray();

        AssertRejected(bytes, "baseline-malformed");
    }

    [Fact]
    public void EscapedUtf8BomInsideStringIsAcceptedAsOrdinaryContent()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        string json = CreateBaselineJson(requirement: @"intentional\uFEFF-content");
        File.WriteAllText(path, json, new UTF8Encoding(false));

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Read(path);

        Assert.Equal("intentional\uFEFF-content", Assert.Single(baseline.Endpoints).Requirements.Single());
    }

    [Fact]
    public void ValidBomFreeBaselineRemainsReadableAndUnchanged()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        byte[] bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"endpoints\":[]}");
        File.WriteAllBytes(path, bytes);

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Read(path);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Empty(baseline.Endpoints);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void BaselineDiagnosticCodesMatchTheShippedDocumentation()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "KeelMatrix.AuthSurface", "AuthSurfaceBaseline.cs"));
        string readme = File.ReadAllText(Path.Combine(root, "src", "KeelMatrix.AuthSurface", "README.md"));

        AssertDiagnosticCodeContract(source, readme);
    }

    [Fact]
    public void ValidV1BaselineWithDistinctEndpointsRemainsReadable()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        const string fingerprint = "0000000000000000000000000000000000000000000000000000000000000000";
        string json = "{\"schemaVersion\":1,\"endpoints\":[" +
            "{\"route\":\"/first\",\"methods\":[\"GET\"],\"authorization\":\"ExplicitProtected\",\"policies\":[],\"roles\":[],\"schemes\":[],\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false,\"requirements\":[\"requirement\"],\"requirementFingerprint\":\"" + fingerprint + "\"}," +
            "{\"route\":\"/second\",\"methods\":[\"GET\"],\"authorization\":\"ExplicitProtected\",\"policies\":[],\"roles\":[],\"schemes\":[],\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false,\"requirements\":[\"requirement\"],\"requirementFingerprint\":\"" + fingerprint + "\"}]}";
        File.WriteAllText(path, json);

        AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Read(path);

        Assert.Equal(1, baseline.SchemaVersion);
        Assert.Equal(["/first", "/second"], baseline.Endpoints.Select(static endpoint => endpoint.Route));
    }

    [Fact]
    public void OversizedBaselineFailsWithBoundedDiagnostic()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"endpoints\":[]}");

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path, maximumBytes: 8));

        Assert.Equal("baseline-too-large", exception.Code);
    }

    [Fact]
    public void LargeDuplicatePropertyNameHasBoundedDiagnosticAndPreservesFile()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        string propertyName = new('x', 500_009);
        byte[] bytes = Encoding.UTF8.GetBytes("{\"" + propertyName + "\":1,\"" + propertyName + "\":2}");
        Assert.InRange(bytes.Length, 500_000, 1_048_576);
        File.WriteAllBytes(path, bytes);
        byte[] before = File.ReadAllBytes(path);

        Stopwatch stopwatch = Stopwatch.StartNew();
        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));
        stopwatch.Stop();

        Assert.Equal("baseline-duplicate-field", exception.Code);
        Assert.Contains("top-level", exception.Message, StringComparison.Ordinal);
        Assert.Contains("x", exception.Message, StringComparison.Ordinal);
        Assert.Contains("…(truncated)", exception.Message, StringComparison.Ordinal);
        Assert.InRange(exception.Message.Length, 1, 256);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void DeeplyNestedBaselineFailsWithBoundedDiagnostic()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        string nested = new string('[', 40) + "0" + new string(']', 40);
        File.WriteAllText(path, nested);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-too-deep", exception.Code);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1", "baseline-truncated")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[", "baseline-truncated")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[{\"route\":\"/x", "baseline-truncated")]
    public void TruncatedBaselineFormsHaveTruncatedDiagnostic(string json, string code)
    {
        AssertRejected(json, code);
    }

    [Fact]
    public void TruncatedEscapeSequenceHasTruncatedDiagnostic()
    {
        AssertRejected("{\"schemaVersion\":1,\"endpoints\":[{\"route\":\"" + "\\", "baseline-truncated");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("{\"schemaVersion\":1,\"endpoints\":[],}")]
    public void EmptyWhitespaceAndCompleteMalformedBaselineHaveMalformedDiagnostic(string json)
    {
        AssertRejected(json, "baseline-malformed");
    }

    [Fact]
    public void CompleteBaselineBeyondDepthBoundHasTooDeepDiagnostic()
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        string nested = new string('[', 40) + "0" + new string(']', 40);
        File.WriteAllText(path, nested);
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-too-deep", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task OpaqueRequirementWithThrowingGetterIsNeverReflected()
    {
        var policy = new AuthorizationPolicyBuilder()
            .AddRequirements(new ThrowingGetterRequirement())
            .Build();
        var builder = new RouteEndpointBuilder(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/opaque"),
            order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(policy);
        RouteEndpoint endpoint = (RouteEndpoint)builder.Build();

        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([endpoint])],
            new TestPolicyProvider()).ScanAsync();

        AuthSurfaceEndpoint record = Assert.Single(report.Endpoints);
        Assert.Contains(record.Requirements, value => value.EndsWith("|opaque", StringComparison.Ordinal));
    }

    private static void AssertRejected(string json, string code)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, json);
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal(code, exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static void AssertRejected(byte[] bytes, string code)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllBytes(path, bytes);
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal(code, exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static byte[] WithUtf8Bom(string text)
    {
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static string CreateBaselineJson(string route = "/orders", string requirement = "requirement") =>
        "{\"schemaVersion\":1,\"endpoints\":[{" +
        "\"route\":\"" + route + "\",\"methods\":[\"GET\"]," +
        "\"authorization\":\"ExplicitProtected\",\"policies\":[],\"roles\":[]," +
        "\"schemes\":[],\"usesDefaultPolicy\":false,\"usesFallbackPolicy\":false," +
        "\"requirements\":[\"" + requirement + "\"],\"requirementFingerprint\":\"" +
        new string('0', 64) + "\"}]}";

    private static void AssertDiagnosticCodeContract(string source, string readme)
    {
        HashSet<string> sourceCodes = Regex.Matches(source, "\\\"(?<code>baseline-[a-z0-9-]+)\\\"")
            .Select(static match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> documentedCodes = Regex.Matches(
                readme,
                "^\\| `(?<code>baseline-[a-z0-9-]+)` \\|",
                RegexOptions.Multiline)
            .Select(static match => match.Groups["code"].Value)
            .ToHashSet(StringComparer.Ordinal);

        string missing = string.Join(",", sourceCodes.Except(documentedCodes).OrderBy(static code => code, StringComparer.Ordinal));
        string undocumented = string.Join(",", documentedCodes.Except(sourceCodes).OrderBy(static code => code, StringComparer.Ordinal));
        Assert.True(
            sourceCodes.SetEquals(documentedCodes),
            $"SOURCE_CODES={string.Join(',', sourceCodes.OrderBy(static code => code, StringComparer.Ordinal))} " +
            $"DOC_CODES={string.Join(',', documentedCodes.OrderBy(static code => code, StringComparer.Ordinal))} " +
            $"SOURCE_NOT_DOC={missing} DOC_NOT_SOURCE={undocumented}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KeelMatrix.AuthSurface.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static void AssertDuplicateFieldRejected(string json, string field, string location)
    {
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Path, "authsurface.json");
        File.WriteAllText(path, json);
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-duplicate-field", exception.Code);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
        Assert.Contains(location, exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

#pragma warning disable CA1822
    private sealed class ThrowingGetterRequirement : IAuthorizationRequirement
    {
        public string Value => throw new InvalidOperationException("requirement property must not be read");
    }
#pragma warning restore CA1822

    private sealed class TestPolicyProvider : IAuthorizationPolicyProvider
    {
        public bool AllowsCachingPolicies => true;

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => Task.FromResult(new AuthorizationPolicyBuilder().Build());

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => Task.FromResult<AuthorizationPolicy?>(null);

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName) => Task.FromResult<AuthorizationPolicy?>(null);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "authsurface-schema-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
