using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SourceProvenance;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 4)
            {
                throw new InvalidDataException("Usage: SourceProvenance <nupkg> <snupkg> <repository-directory> <expected-commit>");
            }
            byte[] dll = Verifier.ReadEntry(args[0], "lib/net8.0/KeelMatrix.AuthSurface.dll");
            byte[] pdb = Verifier.ReadEntry(args[1], "lib/net8.0/KeelMatrix.AuthSurface.pdb");
            Report result = Verifier.Verify(dll, pdb, args[3], path => Verifier.ReadGitBlob(args[2], args[3], path));
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.Success ? 0 : 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or BadImageFormatException or JsonException)
        {
            Console.Error.WriteLine($"Source provenance failed: {error.Message}");
            return 1;
        }
    }
}

public sealed record DocumentResult(int Row, string Name, string Checksum, string? Url, string? MappingError, string Channel, bool Success, string? Error);
public sealed record Report(string DllSha256, string PdbSha256, string Commit, IReadOnlyList<DocumentResult> Documents)
{
    public bool Success => Documents.Count > 0 && Documents.All(document => document.Success);
}

// Build-only package gate. The developer contract lives in docs/maintaining-release-workflow.md.
public static partial class Verifier
{
    public static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    public static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    public static readonly Guid Sha256Kind = new("8829D00F-11B8-4213-878B-770E8597AC16");
    private const string RepositoryPrefix = "https://raw.githubusercontent.com/KeelMatrix/AuthSurface/";

    public static byte[] ReadEntry(string archivePath, string name)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry[] entries = archive.Entries.Where(entry => entry.FullName == name).ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException($"Archive must contain exactly one {name}.");
        }
        using Stream stream = entries[0].Open();
        using MemoryStream output = new();
        stream.CopyTo(output);
        return output.ToArray();
    }

    public static Report Verify(byte[] dll, byte[] pdb, string commit, Func<string, byte[]> readBlob)
    {
        ArgumentNullException.ThrowIfNull(readBlob);
        if (!CommitPattern().IsMatch(commit))
        {
            throw new InvalidDataException("Expected source commit must be a full lowercase Git object ID.");
        }
        using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb));
        MetadataReader reader = provider.GetMetadataReader();
        VerifyPair(dll, pdb, reader);
        List<KeyValuePair<string, string>> mappings = [];
        string? mappingError = null;
        try { mappings = ReadMappings(reader); }
        catch (Exception error) when (error is InvalidDataException or JsonException) { mappingError = error.Message; }

        // A record of these kinds with a different parent is malformed, not an alternative source channel.
        foreach (CustomDebugInformationHandle handle in reader.CustomDebugInformation)
        {
            CustomDebugInformation record = reader.GetCustomDebugInformation(handle);
            Guid kind = reader.GetGuid(record.Kind);
            if ((kind == EmbeddedSourceKind && record.Parent.Kind != HandleKind.Document) ||
                (kind == SourceLinkKind && record.Parent != MetadataTokens.EntityHandle(1)))
            {
                throw new InvalidDataException("Source debug record has the wrong parent.");
            }
        }

        List<DocumentResult> documents = [];
        foreach (DocumentHandle handle in reader.Documents)
        {
            Document document = reader.GetDocument(handle);
            string name = reader.GetString(document.Name);
            byte[] expected = reader.GetBlobBytes(document.Hash);
            string? url = null;
            string? documentMappingError = mappingError;
            if (documentMappingError is null)
            {
                try { url = Resolve(name, mappings); RepositoryPath(url, commit); }
                catch (InvalidDataException error) { documentMappingError = error.Message; }
            }
            string channel = "unresolved";
            string? errorMessage = null;
            try
            {
                Guid algorithm = reader.GetGuid(document.HashAlgorithm);
                if (algorithm != Sha256Kind || expected.Length != 32)
                {
                    throw new InvalidDataException("Missing or unsupported document checksum.");
                }
                CustomDebugInformation[] embedded = reader.GetCustomDebugInformation(handle)
                    .Select(reader.GetCustomDebugInformation).Where(record => reader.GetGuid(record.Kind) == EmbeddedSourceKind).ToArray();
                byte[] source;
                if (embedded.Length > 0)
                {
                    if (embedded.Length != 1) { throw new InvalidDataException("Duplicate embedded source."); }
                    source = DecodeEmbedded(reader.GetBlobBytes(embedded[0].Value));
                    channel = "embedded";
                    // An embedded row does not claim its URL is retrievable (obj is normally untracked).
                }
                else
                {
                    if (documentMappingError is not null) { throw new InvalidDataException(documentMappingError); }
                    string path = RepositoryPath(url!, commit);
                    source = readBlob(path);
                    channel = "git-blob (HTTP availability untested)";
                }
                byte[] actual = SHA256.HashData(source);
                if (!actual.AsSpan().SequenceEqual(expected)) { throw new InvalidDataException("Source bytes do not match the document checksum."); }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or InvalidOperationException or JsonException)
            {
                errorMessage = error.Message;
            }
            documents.Add(new(MetadataTokens.GetRowNumber(handle), name, Convert.ToHexString(expected).ToLowerInvariant(), url, documentMappingError, channel, errorMessage is null, errorMessage));
        }
        return new(Convert.ToHexString(SHA256.HashData(dll)).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(pdb)).ToLowerInvariant(), commit, documents);
    }

    private static void VerifyPair(byte[] dll, byte[] pdb, MetadataReader reader)
    {
        using PEReader pe = new(new MemoryStream(dll));
        DebugDirectoryEntry[] entries = pe.ReadDebugDirectory().ToArray();
        DebugDirectoryEntry[] codeViews = entries.Where(entry => entry.Type == DebugDirectoryEntryType.CodeView).ToArray();
        DebugDirectoryEntry[] checksums = entries.Where(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum).ToArray();
        if (codeViews.Length != 1 || checksums.Length != 1 || reader.DebugMetadataHeader is null)
        {
            throw new InvalidDataException("DLL must identify one portable PDB and its checksum.");
        }
        CodeViewDebugDirectoryData codeView = pe.ReadCodeViewDebugDirectoryData(codeViews[0]);
        BlobContentId id = new(reader.DebugMetadataHeader.Id);
        if (!codeViews[0].IsPortableCodeView || codeView.Age != 1 || codeView.Guid != id.Guid || codeViews[0].Stamp != id.Stamp)
        {
            throw new InvalidDataException("DLL/PDB identity mismatch.");
        }
        PdbChecksumDebugDirectoryData checksum = pe.ReadPdbChecksumDebugDirectoryData(checksums[0]);
        byte[] normalized = (byte[])pdb.Clone();
        normalized.AsSpan(reader.DebugMetadataHeader.IdStartOffset, 20).Clear();
        byte[] actual = checksum.AlgorithmName switch
        {
            "SHA256" => SHA256.HashData(normalized),
            "SHA384" => SHA384.HashData(normalized),
            "SHA512" => SHA512.HashData(normalized),
            _ => throw new InvalidDataException("Unsupported PDB checksum algorithm.")
        };
        if (!actual.AsSpan().SequenceEqual(checksum.Checksum.AsSpan())) { throw new InvalidDataException("DLL/PDB checksum mismatch."); }
    }

    private static List<KeyValuePair<string, string>> ReadMappings(MetadataReader reader)
    {
        CustomDebugInformation[] records = reader.GetCustomDebugInformation(MetadataTokens.EntityHandle(1))
            .Select(reader.GetCustomDebugInformation).Where(record => reader.GetGuid(record.Kind) == SourceLinkKind).ToArray();
        if (records.Length != 1) { throw new InvalidDataException("Missing or duplicate SourceLink record."); }
        using JsonDocument json = JsonDocument.Parse(reader.GetBlobBytes(records[0].Value));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            json.RootElement.EnumerateObject().Count(property => property.NameEquals("documents")) != 1 ||
            !json.RootElement.TryGetProperty("documents", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Malformed SourceLink documents map.");
        }
        List<KeyValuePair<string, string>> mappings = [];
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) { throw new InvalidDataException("Malformed SourceLink URL."); }
            string url = property.Value.GetString()!;
            int stars = property.Name.Count(character => character == '*');
            if (property.Name.Length == 0 || stars > 1 || (stars == 1 && !property.Name.EndsWith('*')) ||
                url.Count(character => character == '*') != stars || mappings.Any(pair => pair.Key.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Malformed or ambiguous SourceLink mapping.");
            }
            mappings.Add(new(property.Name, url));
        }
        return mappings;
    }

    public static string Resolve(string name, IReadOnlyList<KeyValuePair<string, string>> mappings)
    {
        foreach (KeyValuePair<string, string> mapping in mappings.OrderByDescending(pair => pair.Key.Length).ThenBy(pair => pair.Key.EndsWith('*')))
        {
            if (!mapping.Key.EndsWith('*'))
            {
                if (name.Equals(mapping.Key, StringComparison.OrdinalIgnoreCase)) { return mapping.Value; }
                continue;
            }
            string prefix = mapping.Key[..^1];
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = name[prefix.Length..].Replace('\\', '/');
                string escaped = string.Join('/', suffix.Split('/').Select(Uri.EscapeDataString));
                return mapping.Value.Replace("*", escaped, StringComparison.Ordinal);
            }
        }
        throw new InvalidDataException("No SourceLink mapping for document.");
    }

    public static string RepositoryPath(string url, string commit)
    {
        string prefix = RepositoryPrefix + commit + "/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new InvalidDataException("SourceLink URL does not identify the expected repository and immutable commit.");
        }
        string path = Uri.UnescapeDataString(url[prefix.Length..]);
        if (path.Length == 0 || path.Contains('\\') || path.Contains('\0') || path.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException("Invalid repository-relative SourceLink path.");
        }
        return path;
    }

    public static byte[] DecodeEmbedded(byte[] blob)
    {
        if (blob.Length < 4) { throw new InvalidDataException("Truncated embedded-source header."); }
        int length = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (length < 0) { throw new InvalidDataException("Unsupported embedded-source format."); }
        if (length == 0) { return blob[4..]; }
        using CompleteDeflateInput input = new(blob);
        using DeflateStream inflater = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = inflater.Read(buffer)) > 0)
        {
            if (output.Length + count > length) { throw new InvalidDataException("Embedded source exceeds declared length."); }
            output.Write(buffer, 0, count);
        }
        if (output.Length != length) { throw new InvalidDataException("Embedded-source length mismatch."); }
        if (input.Position != input.Length) { throw new InvalidDataException("Trailing embedded-source compressed bytes."); }
        return output.ToArray();
    }

    // DeflateStream on .NET 8 treats premature EOF as a successful short read.
    // Throwing at EOF forces the framework inflater to reach its actual end marker.
    // Single-byte input also makes trailing data observable without parsing deflate.
    private sealed class CompleteDeflateInput(byte[] blob) : MemoryStream(blob, 4, blob.Length - 4)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) { return 0; }
            int read = base.Read(buffer, offset, Math.Min(count, 1));
            return read == 0 ? throw new InvalidDataException("Truncated embedded-source deflate stream.") : read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) { return 0; }
            int read = base.Read(buffer[..1]);
            return read == 0 ? throw new InvalidDataException("Truncated embedded-source deflate stream.") : read;
        }
    }

    public static byte[] ReadGitBlob(string repository, string commit, string path)
    {
        // cat-file blob requires a blob and returns raw bytes, independent of checkout conversion.
        ProcessStartInfo start = new("git") { WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "cat-file", "blob", $"{commit}:{path}" }) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start git.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using MemoryStream bytes = new();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        if (process.ExitCode != 0) { throw new InvalidDataException($"No exact Git blob at {commit}:{path} (exit {process.ExitCode})."); }
        errors.GetAwaiter().GetResult();
        return bytes.ToArray();
    }

    [GeneratedRegex("\\A[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
}
