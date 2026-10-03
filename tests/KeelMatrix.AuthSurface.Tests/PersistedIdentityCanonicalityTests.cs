using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Constraints;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class PersistedIdentityCanonicalityTests
{
    [Theory]
    [MemberData(nameof(GeneratedRegexPolicyCases))]
    public async Task GeneratedRegexPoliciesWithDelimiterTextRoundTripThroughBaseline(IParameterPolicy policy)
    {
        RoutePattern pattern = ProgrammaticPatternWithDefault(policy, "x?");
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

        Assert.Contains("regex64(", endpoint.Route, StringComparison.Ordinal);
        Assert.Equal(endpoint.Identity, Assert.Single(roundTrip.Endpoints).Identity);
        Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid, endpoint.Route);
    }

    public static IEnumerable<object[]> GeneratedRegexPolicyCases()
    {
        const System.Text.RegularExpressions.RegexOptions options =
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled;

        yield return new object[]
        {
            new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:;options=521),foo", options)),
        };
        yield return new object[]
        {
            new OptionalRouteConstraint(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:foo;options=521),bar", options))),
        };
        yield return new object[]
        {
            new CompositeRouteConstraint([
                new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("foo(?:bar;options=521)", options)),
                new MinRouteConstraint(2),
            ]),
        };
        yield return new object[]
        {
            new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:(?:foo;options=521))", options)),
        };
        yield return new object[]
        {
            new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:(?:a;options=521)(?:b;options=521))", options)),
        };
        yield return new object[]
        {
            new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:é/\\{foo\\},bar;options=521)", options)),
        };
        yield return new object[]
        {
            new CompositeRouteConstraint([
                new OptionalRouteConstraint(new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:a;options=521),b(?:c;options=521),d", options))),
                new OptionalRouteConstraint(new CompositeRouteConstraint([
                    new RegexRouteConstraint(new System.Text.RegularExpressions.Regex("(?:foo;options=521),bar", options)),
                    new IntRouteConstraint(),
                ])),
            ]),
        };
    }

    [Fact]
    public async Task WriterEmittableTextualAndProgrammaticRoutesRoundTripThroughBaseline()
    {
        RoutePattern[] patterns =
        [
            RoutePatternFactory.Parse("/items/{id:int}"),
            RoutePatternFactory.Parse("/items/{id=default}"),
            RoutePatternFactory.Parse("/items/{id?}"),
            RoutePatternFactory.Parse("/files/{*path}"),
            RoutePatternFactory.Parse("/files/{**path}"),
            RoutePatternFactory.Parse("/files/{**path:int}"),
            RoutePatternFactory.Parse(
                "/files/{*path}",
                defaults: null,
                parameterPolicies: new { path = new IntRouteConstraint() }),
            RoutePatternFactory.Parse("/v1/pre-{id}.json"),
            RoutePatternFactory.Parse("/items/{id:regex(^\\d+$)}"),
            RoutePatternFactory.Parse("/items/{id:composite(int,min(2))}"),
            RoutePatternFactory.Parse("/items/{id:optional(composite(int,min(2)))}"),
            RoutePatternFactory.Parse("/café/😀/{id:int=значение}"),
            ProgrammaticPatternWithDefault(
                new OptionalRouteConstraint(new CompositeRouteConstraint([
                    new IntRouteConstraint(),
                    new MinRouteConstraint(2),
                ])),
                "значение"),
            ProgrammaticAstralPattern(),
        ];

        using var directory = new TemporaryDirectory();
        int index = 0;
        foreach (RoutePattern pattern in patterns)
        {
            AuthSurfaceReport report = await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
                new AllowingPolicyProvider()).ScanAsync();
            AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
            string path = Path.Combine(directory.Path, (++index).ToString(CultureInfo.InvariantCulture), "authsurface.json");

            AuthSurfaceBaseline.Create(report, path, overwrite: false);
            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);

            Assert.Equal(endpoint.Identity, Assert.Single(roundTrip.Endpoints).Identity);
            Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid, endpoint.Route);
        }
    }

    [Theory]
    [InlineData("int")]
    [InlineData("composite")]
    [InlineData("optional")]
    [InlineData("regex")]
    [InlineData("http-method")]
    public async Task BaselineCreationRejectsNonEncodedCatchAllGeneratedPoliciesWithoutWritingFile(string policyKind)
    {
        RoutePattern pattern = NonEncodedCatchAllWithGeneratedPolicy(CreateGeneratedPolicy(policyKind));
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceEndpoint scannedEndpoint = Assert.Single(report.Endpoints);
        Assert.NotNull(scannedEndpoint.PersistedIdentity);
        Assert.Throws<AuthSurfaceBaselineException>(() =>
            AuthSurfaceCanonicalizer.ReadPersistedIdentity(
                scannedEndpoint.PersistedIdentity!,
                scannedEndpoint.Route,
                scannedEndpoint.Method));

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "unsupported", "authsurface.json");

        AuthSurfaceAnalysisException exception = Assert.Throws<AuthSurfaceAnalysisException>(
            () => AuthSurfaceBaseline.Create(report, path, overwrite: false));

        Assert.Equal("unsupported-parameter-policy", exception.Code);
        Assert.Contains("/files/{**path:programmatic:", exception.Message, StringComparison.Ordinal);
        Assert.Contains("[GET]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("non-encoded catch-all", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));

        AuthSurfaceReport validReport = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(RoutePatternFactory.Parse("/valid"))])],
            new AllowingPolicyProvider()).ScanAsync();
        string existingPath = Path.Combine(directory.Path, "existing", "authsurface.json");
        AuthSurfaceBaseline.Create(validReport, existingPath, overwrite: false);
        byte[] before = File.ReadAllBytes(existingPath);

        AuthSurfaceAnalysisException existingException = Assert.Throws<AuthSurfaceAnalysisException>(
            () => AuthSurfaceBaseline.Create(report, existingPath, overwrite: true));

        Assert.Equal("unsupported-parameter-policy", existingException.Code);
        Assert.Equal(before, File.ReadAllBytes(existingPath));
    }

    [Theory]
    [InlineData("empty-parameter-name")]
    [InlineData("slash-in-parameter-name")]
    [InlineData("contentless-policy-without-provenance")]
    [InlineData("catch-all-parameter-is-optional")]
    [InlineData("duplicate-parameter-part")]
    public async Task NonWriterEmittableBindingFieldsAreRejectedWithoutRewriting(string mutationCase)
    {
        RoutePattern pattern = mutationCase == "catch-all-parameter-is-optional"
            ? CatchAllPattern(null!)
            : ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?");
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();
        AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;

        (string mutatedRoute, string mutatedIdentity) = MutateWriterBinding(
            persistedIdentity,
            endpoint.Route,
            mutationCase);
        string mutatedJson = json
            .Replace("\"route\": \"" + endpoint.Route + "\"", "\"route\": \"" + mutatedRoute + "\"", StringComparison.Ordinal)
            .Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal);
        Assert.Contains("\"route\": \"" + mutatedRoute + "\"", mutatedJson, StringComparison.Ordinal);
        Assert.Contains(mutatedIdentity, mutatedJson, StringComparison.Ordinal);
        File.WriteAllText(path, mutatedJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NonCanonicalBindingRepresentationsAreRejectedWithoutRewriting()
    {
        RoutePattern pattern = RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("item")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        "x?",
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(new IntRouteConstraint())]),
                ]),
            ]);
        AuthSurfaceReport report = await new AuthSurfaceScanner(
            [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
            new AllowingPolicyProvider()).ScanAsync();

        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "authsurface.json");
        AuthSurfaceBaseline.Create(report, path, overwrite: false);
        string json = File.ReadAllText(path);
        using JsonDocument document = JsonDocument.Parse(json);
        string persistedIdentity = document.RootElement
            .GetProperty("endpoints")[0]
            .GetProperty("identity")
            .GetString()!;
        string identityPayload = DecodeUtf8(persistedIdentity[3..]);
        Assert.StartsWith("b1:", identityPayload[(identityPayload.IndexOf('\u001e') + 1)..], StringComparison.Ordinal);

        foreach ((string name, string mutatedIdentity) in Mutations(persistedIdentity))
        {
            string mutatedJson = json.Replace(persistedIdentity, mutatedIdentity, StringComparison.Ordinal);
            File.WriteAllText(path, mutatedJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            byte[] before = File.ReadAllBytes(path);

            AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
                () => AuthSurfaceBaseline.Read(path));

            Assert.Equal("baseline-malformed", exception.Code);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(string.Equals(persistedIdentity, mutatedIdentity, StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public async Task CanonicalWriterTokensRoundTripAcrossBoundedMutationFuzz()
    {
        RoutePattern[] patterns =
        [
            ProgrammaticPatternWithDefault(new IntRouteConstraint(), "x?"),
            ProgrammaticPatternWithDefault(
                new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
                    "[)]:payload",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                    System.Text.RegularExpressions.RegexOptions.Compiled)),
                "foo/bar"),
            ProgrammaticPatternWithDefault(
                new OptionalRouteConstraint(new CompositeRouteConstraint([
                    new IntRouteConstraint(),
                    new MinRouteConstraint(2),
                ])),
                "x?"),
            CatchAllPattern("x?/foo"),
        ];

        using var directory = new TemporaryDirectory();
        int index = 0;
        foreach (RoutePattern pattern in patterns)
        {
            AuthSurfaceReport report = await new AuthSurfaceScanner(
                [new DefaultEndpointDataSource([BuildEndpoint(pattern)])],
                new AllowingPolicyProvider()).ScanAsync();
            AuthSurfaceEndpoint endpoint = Assert.Single(report.Endpoints);
            string path = Path.Combine(directory.Path, (++index).ToString(CultureInfo.InvariantCulture), "authsurface.json");
            AuthSurfaceBaseline.Create(report, path, overwrite: false);

            AuthSurfaceBaseline roundTrip = AuthSurfaceBaseline.Read(path);
            Assert.True(AuthSurfaceVerifier.Compare(report, roundTrip).IsValid, endpoint.Route);

            string json = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(json);
            string persistedIdentity = document.RootElement
                .GetProperty("endpoints")[0]
                .GetProperty("identity")
                .GetString()!;
            Assert.StartsWith("b1:", DecodeUtf8(persistedIdentity[3..]).Split('\u001e')[1], StringComparison.Ordinal);

            foreach (string mutatedIdentity in BoundedMutations(persistedIdentity))
            {
                AssertMutationRejected(path, json, persistedIdentity, mutatedIdentity);
            }
        }
    }

    private static IEnumerable<(string Name, string Identity)> Mutations(string persistedIdentity)
    {
        yield return ("outer-unused-pad-bits", MutateOuterUnusedPadBits(persistedIdentity));
        yield return ("inner-unused-pad-bits", MutateInnerUnusedPadBits(persistedIdentity));
        yield return ("overlong-method-length", RewriteBinding(persistedIdentity, MutateOverlongMethodLength));
        yield return ("redundant-empty-literal", RewriteBinding(persistedIdentity, MutateRedundantEmptyLiteral));
    }

    private static string MutateOuterUnusedPadBits(string persistedIdentity)
    {
        string encoded = persistedIdentity[3..];
        return "v1:" + encoded[..^1] + ShiftUnusedBits(encoded[^1]);
    }

    private static string MutateInnerUnusedPadBits(string persistedIdentity)
    {
        string payload = DecodeUtf8(persistedIdentity[3..]);
        int separator = payload.IndexOf('\u001e');
        Assert.True(separator > 0);
        string identityToken = payload[(separator + 1)..];
        Assert.StartsWith(PersistedIdentityBinding.Prefix, identityToken, StringComparison.Ordinal);

        string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..];
        string mutatedToken = PersistedIdentityBinding.Prefix + encodedBinding[..^1] + ShiftUnusedBits(encodedBinding[^1]);
        return "v1:" + EncodeBase64Url(Encoding.UTF8.GetBytes(payload[..(separator + 1)] + mutatedToken));
    }

    private static string RewriteBinding(string persistedIdentity, Func<byte[], byte[]> mutate)
    {
        string payload = DecodeUtf8(persistedIdentity[3..]);
        int separator = payload.IndexOf('\u001e');
        Assert.True(separator > 0);
        string identityToken = payload[(separator + 1)..];
        Assert.StartsWith(PersistedIdentityBinding.Prefix, identityToken, StringComparison.Ordinal);

        string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..];
        byte[] binding = DecodeBase64Url(encodedBinding);
        string mutatedToken = PersistedIdentityBinding.Prefix + EncodeBase64Url(mutate(binding));
        string mutatedPayload = payload[..(separator + 1)] + mutatedToken;
        return "v1:" + EncodeBase64Url(Encoding.UTF8.GetBytes(mutatedPayload));
    }

    private static IEnumerable<string> BoundedMutations(string persistedIdentity)
    {
        yield return persistedIdentity + "=";
        yield return "v1:" + persistedIdentity[3..^1] + "+";
        yield return RewriteInnerToken(persistedIdentity, static encoded => encoded + "=");
        yield return RewriteInnerToken(persistedIdentity, static encoded => encoded[..^1] + "/");
        yield return RewriteBinding(persistedIdentity, static binding => binding[..^1]);
        yield return RewriteBinding(persistedIdentity, static binding =>
        {
            byte[] mutated = binding.ToArray();
            mutated[4] = 2;
            return mutated;
        });
        yield return RewriteBinding(persistedIdentity, MutateUnknownPartKind);

        string payload = DecodeUtf8(persistedIdentity[3..]);
        string identityToken = payload[(payload.IndexOf('\u001e') + 1)..];
        byte[] binding = DecodeBase64Url(identityToken[PersistedIdentityBinding.Prefix.Length..]);
        for (int seed = 0; seed < 32; seed++)
        {
            int offset = 5 + ((seed * 7919) % (binding.Length - 5));
            byte[] mutated = binding.ToArray();
            mutated[offset] ^= (byte)(1 << (seed % 8));
            yield return RewriteBinding(persistedIdentity, _ => mutated);
        }
    }

    private static string RewriteInnerToken(string persistedIdentity, Func<string, string> mutate)
    {
        string payload = DecodeUtf8(persistedIdentity[3..]);
        int separator = payload.IndexOf('\u001e');
        string identityToken = payload[(separator + 1)..];
        string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..];
        string mutatedToken = PersistedIdentityBinding.Prefix + mutate(encodedBinding);
        return "v1:" + EncodeBase64Url(Encoding.UTF8.GetBytes(payload[..(separator + 1)] + mutatedToken));
    }

    private static byte[] MutateUnknownPartKind(byte[] binding)
    {
        int offset = 5;
        SkipString(binding, ref offset);
        SkipString(binding, ref offset);
        int partOffset = checked(offset + sizeof(int) + sizeof(int));
        byte[] mutated = binding.ToArray();
        mutated[partOffset] = 0xff;
        return mutated;
    }

    private static void AssertMutationRejected(
        string path,
        string json,
        string canonicalIdentity,
        string mutatedIdentity)
    {
        Assert.NotEqual(canonicalIdentity, mutatedIdentity);
        File.WriteAllText(
            path,
            json.Replace(canonicalIdentity, mutatedIdentity, StringComparison.Ordinal),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        byte[] before = File.ReadAllBytes(path);

        AuthSurfaceBaselineException exception = Assert.Throws<AuthSurfaceBaselineException>(
            () => AuthSurfaceBaseline.Read(path));

        Assert.Equal("baseline-malformed", exception.Code);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    private static byte[] MutateOverlongMethodLength(byte[] binding)
    {
        int offset = 5;
        int methodLengthStart = offset;
        int methodByteLength = Read7BitEncodedInt(binding, ref offset);
        int methodEnd = checked(offset + methodByteLength);
        Assert.Equal(3, methodByteLength);

        var mutated = new List<byte>(binding.Length + 1);
        mutated.AddRange(binding[..methodLengthStart]);
        mutated.Add(0x83);
        mutated.Add(0x00);
        mutated.AddRange(binding[methodEnd..]);
        return mutated.ToArray();
    }

    private static byte[] MutateRedundantEmptyLiteral(byte[] binding)
    {
        int offset = 5;
        SkipString(binding, ref offset);
        SkipString(binding, ref offset);
        int segmentCount = ReadInt32(binding, offset);
        Assert.True(segmentCount > 0);
        int partCountOffset = checked(offset + sizeof(int));
        int partCount = ReadInt32(binding, partCountOffset);
        Assert.True(partCount > 0);

        int partOffset = checked(partCountOffset + sizeof(int));
        Assert.Equal(1, binding[partOffset]);
        partOffset++;
        SkipString(binding, ref partOffset);

        byte[] mutated = new byte[binding.Length + 2];
        Buffer.BlockCopy(binding, 0, mutated, 0, partCountOffset);
        BinaryPrimitives.WriteInt32LittleEndian(mutated.AsSpan(partCountOffset), checked(partCount + 1));
        int insertOffset = partOffset;
        Buffer.BlockCopy(binding, partCountOffset + sizeof(int), mutated, partCountOffset + sizeof(int), insertOffset - partCountOffset - sizeof(int));
        mutated[insertOffset] = 1;
        mutated[insertOffset + 1] = 0;
        Buffer.BlockCopy(binding, insertOffset, mutated, insertOffset + 2, binding.Length - insertOffset);
        return mutated;
    }

    private static string ShiftUnusedBits(char character)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        int value = alphabet.IndexOf(character);
        Assert.True(value >= 0);
        int shifted = (value & 0b110000) | ((value + 1) & 0b001111);
        if (shifted == value)
        {
            shifted = (value & 0b110000) | ((value - 1) & 0b001111);
        }

        return alphabet[shifted].ToString();
    }

    private static string DecodeUtf8(string encoded) => Encoding.UTF8.GetString(DecodeBase64Url(encoded));

    private static byte[] DecodeBase64Url(string encoded)
    {
        string padded = encoded.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encoded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string EncodeBase64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static void SkipString(byte[] bytes, ref int offset)
    {
        int length = Read7BitEncodedInt(bytes, ref offset);
        offset = checked(offset + length);
    }

    private static int Read7BitEncodedInt(byte[] bytes, ref int offset)
    {
        int result = 0;
        int shift = 0;
        while (shift < 35)
        {
            byte current = bytes[offset++];
            result |= (current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
        }

        throw new FormatException("The test binding length is invalid.");
    }

    private static int ReadInt32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    private static (string Route, string Identity) MutateWriterBinding(
        string persistedIdentity,
        string route,
        string mutationCase)
    {
        string payload = DecodeUtf8(persistedIdentity[3..]);
        int separator = payload.IndexOf('\u001e');
        Assert.True(separator > 0);
        string identityToken = payload[(separator + 1)..];
        Assert.StartsWith(PersistedIdentityBinding.Prefix, identityToken, StringComparison.Ordinal);

        string encodedBinding = identityToken[PersistedIdentityBinding.Prefix.Length..];
        PersistedIdentityBinding binding = PersistedIdentityBinding.Deserialize(DecodeBase64Url(encodedBinding));
        PersistedIdentityBinding.ParameterPart originalParameter = Assert.IsType<PersistedIdentityBinding.ParameterPart>(
            binding.Segments[1].Parts[0]);

        string mutatedRoute;
        string mutatedIdentityRoute;
        PersistedIdentityBinding.ParameterPart mutatedParameter;
        switch (mutationCase)
        {
            case "empty-parameter-name":
                mutatedParameter = new PersistedIdentityBinding.ParameterPart(
                    string.Empty,
                    originalParameter.IsCatchAll,
                    originalParameter.EncodeSlashes,
                    originalParameter.IsOptional,
                    originalParameter.Default,
                    originalParameter.Policies);
                mutatedRoute = route.Replace("{id:", "{:", StringComparison.Ordinal);
                mutatedIdentityRoute = binding.IdentityRoute!.Replace("{ID:", "{:", StringComparison.Ordinal);
                break;
            case "slash-in-parameter-name":
                mutatedParameter = new PersistedIdentityBinding.ParameterPart(
                    "a/b",
                    originalParameter.IsCatchAll,
                    originalParameter.EncodeSlashes,
                    originalParameter.IsOptional,
                    originalParameter.Default,
                    originalParameter.Policies);
                mutatedRoute = route.Replace("{id:", "{a/b:", StringComparison.Ordinal);
                mutatedIdentityRoute = binding.IdentityRoute!.Replace("{ID:", "{A/B:", StringComparison.Ordinal);
                break;
            case "contentless-policy-without-provenance":
                mutatedParameter = new PersistedIdentityBinding.ParameterPart(
                    originalParameter.Name,
                    originalParameter.IsCatchAll,
                    originalParameter.EncodeSlashes,
                    originalParameter.IsOptional,
                    originalParameter.Default,
                    originalParameter.Policies
                        .Select(static policy => new PersistedIdentityBinding.Policy(isContent: false, "int"))
                        .ToArray());
                mutatedRoute = route.Replace(":programmatic:int", ":int", StringComparison.Ordinal);
                mutatedIdentityRoute = binding.IdentityRoute!;
                break;
            case "catch-all-parameter-is-optional":
                mutatedParameter = new PersistedIdentityBinding.ParameterPart(
                    originalParameter.Name,
                    originalParameter.IsCatchAll,
                    originalParameter.EncodeSlashes,
                    true,
                    originalParameter.Default,
                    originalParameter.Policies);
                mutatedRoute = route.Replace("}", "?}", StringComparison.Ordinal);
                mutatedIdentityRoute = binding.IdentityRoute!.Replace("}", "?}", StringComparison.Ordinal);
                break;
            case "duplicate-parameter-part":
                mutatedParameter = originalParameter;
                mutatedRoute = route.Replace("}", "}{" + originalParameter.Name + ":programmatic:int=x?}", StringComparison.Ordinal);
                mutatedIdentityRoute = binding.IdentityRoute!.Replace("}", "}{" + originalParameter.Name.ToUpperInvariant() + ":PROGRAMMATIC:INT=x?}", StringComparison.Ordinal);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutationCase));
        }

        var mutatedSegments = binding.Segments.ToArray();
        mutatedSegments[1] = mutationCase == "duplicate-parameter-part"
            ? new PersistedIdentityBinding.Segment([mutatedParameter, mutatedParameter])
            : new PersistedIdentityBinding.Segment([mutatedParameter]);
        PersistedIdentityBinding mutatedBinding = new(
            binding.Method,
            mutatedSegments,
            mutatedIdentityRoute);
        string mutatedToken = PersistedIdentityBinding.Prefix + EncodeBase64Url(mutatedBinding.Serialize());
        string mutatedPayload = mutatedRoute + '\u001e' + mutatedToken;
        return (mutatedRoute, "v1:" + EncodeBase64Url(Encoding.UTF8.GetBytes(mutatedPayload)));
    }

    private static RouteEndpoint BuildEndpoint(RoutePattern pattern)
    {
        RouteEndpointBuilder builder = new(_ => Task.CompletedTask, pattern, order: 0);
        builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
        builder.Metadata.Add(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());
        return (RouteEndpoint)builder.Build();
    }

    private static RoutePattern ProgrammaticPatternWithDefault(IParameterPolicy policy, object defaultValue) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("item")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        defaultValue,
                        RoutePatternParameterKind.Standard,
                        [RoutePatternFactory.ParameterPolicy(policy)]),
                ]),
            ]);

    private static RoutePattern ProgrammaticAstralPattern() =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([
                    RoutePatternFactory.LiteralPart("😀"),
                    RoutePatternFactory.SeparatorPart("."),
                    RoutePatternFactory.LiteralPart("é"),
                ]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "id",
                        null,
                        RoutePatternParameterKind.Optional,
                        [RoutePatternFactory.ParameterPolicy(new IntRouteConstraint())]),
                ]),
            ]);

    private static RoutePattern CatchAllPattern(object defaultValue) =>
        RoutePatternFactory.Pattern(
            rawText: null!,
            segments:
            [
                RoutePatternFactory.Segment([RoutePatternFactory.LiteralPart("files")]),
                RoutePatternFactory.Segment([
                    RoutePatternFactory.ParameterPart(
                        "path",
                        defaultValue,
                        RoutePatternParameterKind.CatchAll,
                        [RoutePatternFactory.ParameterPolicy(new IntRouteConstraint())]),
                ]),
            ]);

    private static RoutePattern NonEncodedCatchAllWithGeneratedPolicy(IParameterPolicy policy) =>
        RoutePatternFactory.Parse(
            "/files/{**path}",
            defaults: null,
            parameterPolicies: new { path = policy });

    private static IParameterPolicy CreateGeneratedPolicy(string policyKind) => policyKind switch
    {
        "int" => new IntRouteConstraint(),
        "composite" => new CompositeRouteConstraint([new IntRouteConstraint(), new MinRouteConstraint(2)]),
        "optional" => new OptionalRouteConstraint(new IntRouteConstraint()),
        "regex" => new RegexRouteConstraint(new System.Text.RegularExpressions.Regex(
            "^\\d+$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant |
            System.Text.RegularExpressions.RegexOptions.Compiled)),
        "http-method" => new HttpMethodRouteConstraint(["post", "GET"]),
        _ => throw new ArgumentOutOfRangeException(nameof(policyKind), policyKind, null),
    };

    private sealed class AllowingPolicyProvider : IAuthorizationPolicyProvider
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "authsurface-canonicality-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
