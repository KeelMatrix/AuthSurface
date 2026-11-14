using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SourceProvenance;
using Xunit;

namespace KeelMatrix.AuthSurface.Tests;

public sealed class SourceProvenanceTests
{
    private const string Commit = "0123456789012345678901234567890123456789";
    private const string UrlRoot = "https://raw.githubusercontent.com/KeelMatrix/AuthSurface/" + Commit + "/";
    private static readonly byte[] Source = Encoding.UTF8.GetBytes("// exact source\r\n");

    [Theory]
    [InlineData("/_/Root.cs", "Root.cs")]
    [InlineData("/_/src/Library/File.cs", "src/Library/File.cs")]
    [InlineData("/_/src/Library/nested/File.cs", "src/Library/nested/File.cs")]
    [InlineData("/_/shared/Linked.cs", "shared/Linked.cs")]
    [InlineData("/_/space ü/#percent%.cs", "space ü/#percent%.cs")]
    [InlineData("C:\\repo\\src\\Library\\File.cs", "src/Library/File.cs")]
    public void ExactMappedPathsUseRepositorySuffixAndRawBytes(string name, string path)
    {
        string prefix = name.StartsWith("C:", StringComparison.Ordinal) ? "C:\\repo\\*" : "/_/*";
        Pair pair = Create([new(name, Source)], Map(prefix, UrlRoot + "*"));
        Report report = Check(pair, blob => { Assert.Equal(path, blob); return Source; });
        Assert.True(report.Success);
        Assert.Single(report.Documents);
    }

    [Fact]
    public void OriginalProjectRootFailsAtTheSameCorrectCommitDespiteSameBasename()
    {
        Pair pair = Create([new("/_/File.cs", Source)], Map("/_/*", UrlRoot + "*"));
        Report broken = Check(pair, path => path == "src/Library/File.cs" ? Source : Encoding.UTF8.GetBytes("// misleading root file"));
        Assert.False(broken.Success);
        Assert.Contains("checksum", broken.Documents[0].Error);
        Report absent = Check(pair, _ => throw new FileNotFoundException("Exact mapped path absent."));
        Assert.False(absent.Success);
        Pair corrected = Create([new("/_/src/Library/File.cs", Source)], Map("/_/*", UrlRoot + "*"));
        Assert.True(Check(corrected, path => path == "src/Library/File.cs" ? Source : throw new FileNotFoundException()).Success);
    }

    [Fact]
    public void ExactAndMostSpecificMapsWinWithoutChangingRepositoryPathCase()
    {
        string map = JsonSerializer.Serialize(new
        {
            documents = new Dictionary<string, string>
            {
                ["/_/*"] = UrlRoot + "wrong/*",
                ["/_/src/*"] = UrlRoot + "src/*",
                ["/_/src/Exact.cs"] = UrlRoot + "Case/Exact.cs"
            }
        });
        Pair pair = Create([new("/_/src/Exact.cs", Source), new("/_/src/Nested.cs", Source)], map);
        List<string> paths = [];
        Assert.True(Check(pair, path => { paths.Add(path); return Source; }).Success);
        Assert.Equal("Case/Exact.cs", paths[0]);
        Assert.Equal("src/Nested.cs", paths[1]);
        Assert.Equal(UrlRoot + "a.cs", Verifier.Resolve("/_/a.cs", [new("/_/A.cs", UrlRoot + "a.cs")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"documents\":[]}")]
    [InlineData("{\"documents\":{},\"documents\":{}}")]
    [InlineData("{\"documents\":{}}")]
    [InlineData("{\"documents\":{\"/_/*\":null}}")]
    [InlineData("{\"documents\":{\"/_/*/a\":\"https://x/*\"}}")]
    [InlineData("{\"documents\":{\"/_/**\":\"https://x/*\"}}")]
    [InlineData("{\"documents\":{\"/_/*\":\"https://x/no-wildcard\"}}")]
    [InlineData("{\"documents\":{\"/_/a.cs\":\"https://x/*\"}}")]
    [InlineData("{\"documents\":{\"/_/*\":\"https://x/*\",\"/_/*\":\"https://y/*\"}}")]
    [InlineData("{\"documents\":{\"/_/A.cs\":\"https://x/a\",\"/_/a.cs\":\"https://y/a\"}}")]
    public void MissingMalformedAndAmbiguousMappingsFail(string? map)
    {
        Report report = Check(Create([new("/_/a.cs", Source)], map));
        Assert.False(report.Success);
        Assert.Single(report.Documents);
    }

    [Fact]
    public void DuplicateSourceLinkRecordsFail()
    {
        Assert.False(Check(Create([new("/_/a.cs", Source)], Map("/_/*", UrlRoot + "*"), duplicateMapping: true)).Success);
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/KeelMatrix/Foreign/" + Commit + "/a.cs")]
    [InlineData("https://raw.githubusercontent.com/KeelMatrix/AuthSurface/main/a.cs")]
    [InlineData("https://raw.githubusercontent.com/KeelMatrix/AuthSurface/1123456789012345678901234567890123456789/a.cs")]
    [InlineData(UrlRoot + "a.cs?x=1")]
    [InlineData(UrlRoot + "a.cs#x")]
    [InlineData(UrlRoot + "../a.cs")]
    [InlineData(UrlRoot + "%2e%2e/a.cs")]
    [InlineData(UrlRoot + "a%5cb.cs")]
    public void WrongRepositoryRevisionOrInvalidPathFails(string url)
    {
        Assert.False(Check(Create([new("/_/a.cs", Source)], Map("/_/a.cs", url))).Success);
    }

    [Theory]
    [InlineData("lf")]
    [InlineData("bom")]
    [InlineData("one-byte")]
    [InlineData("case")]
    public void SourceByteAndCaseDifferencesCannotBeNormalizedAway(string mutation)
    {
        Pair pair = Create([new("/_/Case.cs", Source)], Map("/_/*", UrlRoot + "*"));
        byte[] altered = mutation switch
        {
            "lf" => Encoding.UTF8.GetBytes("// exact source\n"),
            "bom" => new byte[] { 239, 187, 191 }.Concat(Source).ToArray(),
            _ => Encoding.UTF8.GetBytes("// altered source\r\n")
        };
        Report report = Check(pair, path => mutation == "case" && path == "Case.cs" ? throw new FileNotFoundException() : altered);
        Assert.False(report.Success);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RawCompressedAndEmptyEmbeddedSourcesPass(bool compressed, bool empty)
    {
        byte[] bytes = empty ? [] : Source;
        Pair pair = Create([new("/_/obj/generated.cs", bytes, Embed(bytes, compressed))], null);
        Report report = Check(pair, _ => throw new InvalidOperationException("Embedded source must not read Git."));
        Assert.True(report.Success);
        Assert.Equal("embedded", report.Documents[0].Channel);
        Assert.Null(report.Documents[0].Url);
    }

    [Theory]
    [InlineData("/_/src/Library/nested ü/File.cs", "src/Library/nested%20%C3%BC/File.cs")]
    [InlineData("/_/src/Library/nested u\u0308/File.cs", "src/Library/nested%20u%CC%88/File.cs")]
    public void AuthoredEmbeddingPreservesTheActualUnicodeMapping(string name, string escapedPath)
    {
        Report report = Check(Create([new(name, Source, Embed(Source, true))], Map("/_/*", UrlRoot + "*")),
            _ => throw new InvalidOperationException("Embedded source must not claim Git retrieval."));
        Assert.True(report.Success);
        DocumentResult row = Assert.Single(report.Documents);
        Assert.Equal("embedded", row.Channel);
        Assert.Equal(UrlRoot + escapedPath, row.Url);
        Assert.Null(row.MappingError);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("format")]
    [InlineData("length-long")]
    [InlineData("length-short")]
    [InlineData("compressed-truncated")]
    [InlineData("compressed-final-truncated")]
    [InlineData("compressed-trailing")]
    [InlineData("compressed-invalid")]
    [InlineData("hash")]
    [InlineData("duplicate")]
    public void MalformedEmbeddedSourcesFail(string mutation)
    {
        byte[] blob = Embed(Source, true);
        if (mutation == "header") { blob = [0, 0, 0]; }
        if (mutation == "format") { blob = [255, 255, 255, 255]; }
        if (mutation == "length-long") { blob[0]++; }
        if (mutation == "length-short") { blob[0]--; }
        if (mutation == "compressed-truncated") { blob = blob[..^5]; }
        if (mutation == "compressed-final-truncated") { blob = blob[..^1]; }
        if (mutation == "compressed-trailing") { blob = [.. blob, 0]; }
        if (mutation == "compressed-invalid") { blob = [4, 0, 0, 0, 255, 255, 255]; }
        if (mutation == "hash") { blob = Embed(Encoding.UTF8.GetBytes("changed"), false); }
        Pair pair = Create([new("/_/obj/a.cs", Source, blob)], null, duplicateEmbedding: mutation == "duplicate");
        Assert.False(Check(pair).Success);
    }

    [Theory]
    [InlineData("embedded-parent")]
    [InlineData("map-parent")]
    public void WrongParentSourceRecordsFail(string mutation)
    {
        Pair pair = Create([new("/_/obj/a.cs", Source, Embed(Source, false))], Map("/_/*", UrlRoot + "*"), wrongParent: mutation);
        Assert.Throws<InvalidDataException>(() => Check(pair));
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("unsupported-hash")]
    [InlineData("one-unresolved")]
    [InlineData("one-generated-unresolved")]
    public void EveryRowRequiresValidSourceAndChecksum(string mutation)
    {
        DocumentSpec[] docs = [new("/_/a.cs", Source), new("/_/b.cs", Source, Embed(Source, false))];
        if (mutation == "one-generated-unresolved") { docs[1] = new("/_/obj/missing.cs", Source); }
        Report report = Check(Create(docs, Map("/_/*", UrlRoot + "*"), checksumMutation: mutation), path =>
            path == "a.cs" && mutation != "one-unresolved" ? Source : throw new FileNotFoundException());
        Assert.False(report.Success);
        Assert.Equal(2, report.Documents.Count);
        if (mutation.StartsWith("one-", StringComparison.Ordinal)) { Assert.Single(report.Documents.Where(row => !row.Success)); }
    }

    [Fact]
    public void MissingWrongAndModifiedPdbFailEvenWhenIdentityIsPreserved()
    {
        Pair pair = Create([new("/_/a.cs", Source)], Map("/_/*", UrlRoot + "*"));
        Assert.Throws<BadImageFormatException>(() => Verifier.Verify(pair.Dll, [], Commit, _ => Source));
        Pair other = Create([new("/_/b.cs", Source)], Map("/_/*", UrlRoot + "*"));
        Assert.Throws<InvalidDataException>(() => Verifier.Verify(pair.Dll, other.Pdb, Commit, _ => Source));
        byte[] changed = (byte[])pair.Pdb.Clone();
        changed[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Verifier.Verify(pair.Dll, changed, Commit, _ => Source));
        Assert.False(Check(Create([], null)).Success);
    }

    [Fact]
    public void MissingAndDuplicatePdbArchiveEntriesFail()
    {
        using Fixture fixture = new();
        string path = Path.Combine(fixture.Root, "symbols.snupkg");
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create)) { archive.CreateEntry("unrelated"); }
        Assert.Throws<InvalidDataException>(() => Verifier.ReadEntry(path, "lib/net8.0/KeelMatrix.AuthSurface.pdb"));
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            archive.CreateEntry("lib/net8.0/KeelMatrix.AuthSurface.pdb");
            archive.CreateEntry("lib/net8.0/KeelMatrix.AuthSurface.pdb");
        }
        Assert.Throws<InvalidDataException>(() => Verifier.ReadEntry(path, "lib/net8.0/KeelMatrix.AuthSurface.pdb"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompilerPreservesRootNestedLinkedAndGeneratedSources(bool projectDirectory)
    {
        using Fixture fixture = new();
        string root = fixture.Root;
        string project = Path.Combine(root, "src", "Library");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(Path.Combine(project, "nested space ü"));
        Directory.CreateDirectory(Path.Combine(root, "shared"));
        File.WriteAllBytes(Path.Combine(root, "Root.cs"), Encoding.UTF8.GetBytes("public class RootType {}\n"));
        File.WriteAllBytes(Path.Combine(project, "Project.cs"), Encoding.UTF8.GetBytes("public class ProjectType {}\r\n"));
        File.WriteAllBytes(Path.Combine(project, "nested space ü", "Nested.cs"), new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes("public class NestedType {}\n")).ToArray());
        File.WriteAllText(Path.Combine(root, "shared", "Linked.cs"), "public class LinkedType {}\n", new UTF8Encoding(false));
        string escapedRoot = System.Security.SecurityElement.Escape(Path.Combine(root, "Root.cs"));
        File.WriteAllText(Path.Combine(project, "Library.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
                <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild><EmbedUntrackedSources>true</EmbedUntrackedSources>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.SourceLink.GitHub" Version="10.0.401" PrivateAssets="all" />
                <Compile Include="../../shared/Linked.cs" Link="Linked.cs" />
                <Compile Include="{{escapedRoot}}" Link="Root.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(root, ".gitignore"), "**/bin/\n**/obj/\n");
        DirectoryInfo? repo = new(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "global.json"))) { repo = repo.Parent; }
        File.WriteAllBytes(Path.Combine(root, "global.json"), File.ReadAllBytes(Path.Combine(repo!.FullName, "global.json")));
        await Run(root, "git", "init", "-q");
        await Run(root, "git", "config", "core.autocrlf", "false");
        await Run(root, "git", "remote", "add", "origin", "https://github.com/KeelMatrix/AuthSurface.git");
        await Run(root, "git", "add", ".");
        await Run(root, "git", "-c", "user.name=KeelMatrix", "-c", "user.email=keelmatrix@gmail.com", "commit", "-qm", "Source fixture");
        string commit = (await Run(root, "git", "rev-parse", "HEAD")).Trim();
        Assert.Throws<InvalidDataException>(() => Verifier.ReadGitBlob(root, commit, "src/Library/project.cs"));
        Assert.Throws<InvalidDataException>(() => Verifier.ReadGitBlob(root, commit, "src/Library"));
        await Run(projectDirectory ? project : root, "dotnet", "build", projectDirectory ? "Library.csproj" : "src/Library/Library.csproj", "-c", "Release", "--nologo");
        Report report = Verifier.Verify(File.ReadAllBytes(Path.Combine(project, "bin/Release/net8.0/Library.dll")),
            File.ReadAllBytes(Path.Combine(project, "bin/Release/net8.0/Library.pdb")), commit, path => Verifier.ReadGitBlob(root, commit, path));
        Assert.True(report.Success, JsonSerializer.Serialize(report));
        foreach (string path in new[] { "Root.cs", "src/Library/Project.cs", "shared/Linked.cs" })
        {
            DocumentResult authored = Assert.Single(report.Documents.Where(row => row.Name == "/_/" + path));
            Assert.True(authored.Success);
            Assert.StartsWith("git-blob", authored.Channel, StringComparison.Ordinal);
        }
        // A tracked Unicode path can be classified as untracked by source-control
        // integration on a filesystem with a different Unicode normalization form.
        // Either exact source channel is valid; normalization here only identifies
        // the fixture row, never changes the verifier's Git path or source bytes.
        DocumentResult nested = Assert.Single(report.Documents.Where(row => row.Name.Normalize(NormalizationForm.FormC) == "/_/src/Library/nested space ü/Nested.cs"));
        Assert.True(nested.Success);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(project, "nested space ü", "Nested.cs")))).ToLowerInvariant(), nested.Checksum);
        Assert.True(nested.Channel == "embedded" || nested.Channel.StartsWith("git-blob", StringComparison.Ordinal));
        foreach (string generated in new[] { "GlobalUsings.g.cs", "AssemblyAttributes.cs", "AssemblyInfo.cs" })
        {
            Assert.Contains(report.Documents, row => row.Name.EndsWith(generated, StringComparison.Ordinal) && row.Channel == "embedded" && row.Success);
        }
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.Combine(project, "bin/Release/net8.0/Library.pdb")));
        MetadataReader reader = provider.GetMetadataReader();
        string sourceLink = Encoding.UTF8.GetString(reader.GetBlobBytes(reader.GetCustomDebugInformation(reader.GetCustomDebugInformation(MetadataTokens.EntityHandle(1))
            .Single(handle => reader.GetGuid(reader.GetCustomDebugInformation(handle).Kind) == Verifier.SourceLinkKind)).Value));
        List<DocumentSpec> specs = [];
        foreach (DocumentHandle handle in reader.Documents)
        {
            Document document = reader.GetDocument(handle);
            byte[]? embedded = reader.GetCustomDebugInformation(handle).Select(reader.GetCustomDebugInformation)
                .Where(record => reader.GetGuid(record.Kind) == Verifier.EmbeddedSourceKind).Select(record => reader.GetBlobBytes(record.Value)).SingleOrDefault();
            string name = reader.GetString(document.Name);
            DocumentResult row = report.Documents.Single(row => row.Name == name);
            byte[] bytes = embedded is not null ? Verifier.DecodeEmbedded(embedded) : Verifier.ReadGitBlob(root, commit, Verifier.RepositoryPath(row.Url!, commit));
            specs.Add(new(name, bytes, embedded));
        }
        foreach (DocumentSpec generated in specs.Where(spec => spec.Name.Contains("/obj/", StringComparison.Ordinal)))
        {
            Pair removed = Create(specs.Select(spec => spec == generated ? spec with { Embedded = null } : spec).ToArray(), sourceLink);
            Report failure = Verifier.Verify(removed.Dll, removed.Pdb, commit, path => Verifier.ReadGitBlob(root, commit, path));
            Assert.False(failure.Success);
            Assert.Equal(generated.Name, Assert.Single(failure.Documents.Where(row => !row.Success)).Name);
        }
        // A changed checkout cannot affect immutable Git-blob verification.
        File.WriteAllText(Path.Combine(project, "Project.cs"), "changed checkout");
        Assert.True(Verifier.ReadGitBlob(root, commit, "src/Library/Project.cs").AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("public class ProjectType {}\r\n")));
    }

    private static Report Check(Pair pair, Func<string, byte[]>? source = null) => Verifier.Verify(pair.Dll, pair.Pdb, Commit, source ?? (_ => Source));
    private static string Map(string path, string url) => JsonSerializer.Serialize(new { documents = new Dictionary<string, string> { [path] = url } });

    private static byte[] Embed(byte[] source, bool compress)
    {
        using MemoryStream output = new();
        using BinaryWriter writer = new(output, Encoding.UTF8, true);
        writer.Write(compress ? source.Length : 0);
        if (compress) { using DeflateStream deflate = new(output, CompressionLevel.Optimal, true); deflate.Write(source); }
        else { writer.Write(source); }
        return output.ToArray();
    }

    private static Pair Create(DocumentSpec[] documents, string? map, string? wrongParent = null, bool duplicateEmbedding = false, string? checksumMutation = null, bool duplicateMapping = false)
    {
        MetadataBuilder metadata = new();
        foreach (DocumentSpec document in documents)
        {
            DocumentHandle handle = metadata.AddDocument(metadata.GetOrAddDocumentName(document.Name),
                metadata.GetOrAddGuid(checksumMutation == "unsupported-hash" ? Guid.Empty : Verifier.Sha256Kind),
                metadata.GetOrAddBlob(checksumMutation == "missing-hash" ? [] : SHA256.HashData(document.Source)), default);
            if (document.Embedded is not null)
            {
                metadata.AddCustomDebugInformation(wrongParent == "embedded-parent" ? MetadataTokens.EntityHandle(1) : handle,
                    metadata.GetOrAddGuid(Verifier.EmbeddedSourceKind), metadata.GetOrAddBlob(document.Embedded));
                if (duplicateEmbedding) { metadata.AddCustomDebugInformation(handle, metadata.GetOrAddGuid(Verifier.EmbeddedSourceKind), metadata.GetOrAddBlob(document.Embedded)); }
            }
        }
        if (map is not null)
        {
            metadata.AddCustomDebugInformation(wrongParent == "map-parent" ? MetadataTokens.DocumentHandle(1) : MetadataTokens.EntityHandle(1),
                metadata.GetOrAddGuid(Verifier.SourceLinkKind), metadata.GetOrAddBlob(Encoding.UTF8.GetBytes(map)));
            if (duplicateMapping) { metadata.AddCustomDebugInformation(MetadataTokens.EntityHandle(1), metadata.GetOrAddGuid(Verifier.SourceLinkKind), metadata.GetOrAddBlob(Encoding.UTF8.GetBytes(map))); }
        }
        BlobBuilder pdb = new();
        PortablePdbBuilder pdbBuilder = new(metadata, ImmutableArray.Create(new int[64]), default,
            blobs => BlobContentId.FromHash(SHA256.HashData(blobs.SelectMany(blob => blob.GetBytes()).ToArray())));
        BlobContentId id = pdbBuilder.Serialize(pdb);
        byte[] pdbBytes = pdb.ToArray();
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdbBytes));
        byte[] normalized = (byte[])pdbBytes.Clone();
        normalized.AsSpan(provider.GetMetadataReader().DebugMetadataHeader!.IdStartOffset, 20).Clear();
        DebugDirectoryBuilder debug = new();
        debug.AddCodeViewEntry("Library.pdb", id, 0x0100);
        debug.AddPdbChecksumEntry("SHA256", ImmutableArray.Create(SHA256.HashData(normalized)));
        debug.AddReproducibleEntry();
        MetadataBuilder assembly = new();
        assembly.AddModule(0, assembly.GetOrAddString("Library.dll"), assembly.GetOrAddGuid(Guid.NewGuid()), default, default);
        assembly.AddTypeDefinition(0, default, assembly.GetOrAddString("<Module>"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        ManagedPEBuilder pe = new(new PEHeaderBuilder(imageCharacteristics: Characteristics.Dll), new MetadataRootBuilder(assembly), new BlobBuilder(), debugDirectoryBuilder: debug);
        BlobBuilder dll = new();
        pe.Serialize(dll);
        return new(dll.ToArray(), pdbBytes);
    }

    private sealed record Pair(byte[] Dll, byte[] Pdb);
    private sealed record DocumentSpec(string Name, byte[] Source, byte[]? Embedded = null);
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("AUTHSURFACE_TEST_SCRATCH") ?? Path.GetTempPath(), "source fixture ü " + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(Root, true);
        }
    }

    private static async Task<string> Run(string directory, string executable, params string[] arguments)
    {
        ProcessStartInfo start = new(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        string output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, output);
        return output;
    }
}
