using System.Text;
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
        AssertRejected(new string('[', 40) + "0" + new string(']', 40), "baseline-too-deep");
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
