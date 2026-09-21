using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.AuthSurface;

/// <summary>Represents a validated, deterministic AuthSurface baseline.</summary>
public sealed class AuthSurfaceBaseline
{
    /// <summary>The first supported baseline schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private AuthSurfaceBaseline(IEnumerable<AuthSurfaceEndpoint> endpoints)
    {
        Endpoints = endpoints.OrderBy(static endpoint => endpoint.Route, StringComparer.Ordinal)
            .ThenBy(static endpoint => endpoint.Methods[0], StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Gets the schema version written by this package.</summary>
    public int SchemaVersion { get; } = CurrentSchemaVersion;

    /// <summary>Gets canonical endpoint records in route/method order.</summary>
    public IReadOnlyList<AuthSurfaceEndpoint> Endpoints { get; }

    /// <summary>Creates a baseline from a policy-compliant report without writing it.</summary>
    /// <param name="report">The completed runtime scan report.</param>
    /// <returns>The in-memory baseline.</returns>
    public static AuthSurfaceBaseline Create(AuthSurfaceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report.AssertPolicyCompliant();
        return new AuthSurfaceBaseline(report.Endpoints);
    }

    /// <summary>Reads a bounded, validated baseline without modifying the file.</summary>
    /// <param name="path">The local baseline path.</param>
    /// <param name="maximumBytes">The maximum accepted file size.</param>
    /// <returns>The validated baseline.</returns>
    public static AuthSurfaceBaseline Read(string path, int maximumBytes = 1_048_576)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        byte[] bytes;
        try
        {
            using FileStream stream = new(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
            {
                throw new AuthSurfaceBaselineException(
                    "baseline-too-large",
                    $"The baseline exceeds the maximum size of {maximumBytes:N0} bytes.");
            }

            bytes = new byte[checked((int)stream.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset != bytes.Length || stream.ReadByte() >= 0)
            {
                throw new AuthSurfaceBaselineException(
                    "baseline-too-large",
                    $"The baseline exceeds the maximum size of {maximumBytes:N0} bytes.");
            }
        }
        catch (AuthSurfaceBaselineException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AuthSurfaceBaselineException(
                "baseline-read-failed",
                "The baseline could not be read.",
                exception);
        }

        BaselineDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BaselineDocument>(bytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline is not valid JSON.", exception);
        }

        if (document is null)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline is empty or null.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new AuthSurfaceBaselineException(
                "baseline-schema-unsupported",
                $"The baseline schema version {document.SchemaVersion} is not supported; expected {CurrentSchemaVersion}.");
        }

        if (document.Endpoints is null || document.Endpoints.Count > 100_000)
        {
            throw new AuthSurfaceBaselineException("baseline-endpoint-limit", "The baseline endpoint count is invalid or exceeds the supported bound.");
        }

        try
        {
            var endpoints = document.Endpoints.Select(ToEndpoint).ToArray();
            if (endpoints.Select(static endpoint => endpoint.Identity).Distinct(StringComparer.Ordinal).Count() != endpoints.Length)
            {
                throw new AuthSurfaceBaselineException("baseline-duplicate-identity", "The baseline contains duplicate canonical endpoint identities.");
            }

            return new AuthSurfaceBaseline(endpoints);
        }
        catch (AuthSurfaceBaselineException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline endpoint records are invalid.", exception);
        }
    }

    /// <summary>Writes the baseline explicitly as canonical UTF-8 JSON.</summary>
    /// <param name="path">The local destination path.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    public void Write(string path, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ToDocumentWithEndpoints(), JsonOptions);
        string text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        bytes = System.Text.Encoding.UTF8.GetBytes(text);

        if (!overwrite)
        {
            using FileStream stream = new(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return;
        }

        string tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Creates and explicitly writes a baseline in one call.</summary>
    /// <param name="report">The completed runtime scan report.</param>
    /// <param name="path">The local destination path.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <returns>The baseline that was written.</returns>
    public static AuthSurfaceBaseline Create(AuthSurfaceReport report, string path, bool overwrite)
    {
        AuthSurfaceBaseline baseline = Create(report);
        baseline.Write(path, overwrite);
        return baseline;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32,
    };

    private static BaselineDocument ToDocument() => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Endpoints = [],
    };

    private BaselineDocument ToDocumentWithEndpoints()
    {
        BaselineDocument document = ToDocument();
        document.Endpoints = Endpoints.Select(ToDocument).ToList();
        return document;
    }

    private static EndpointDocument ToDocument(AuthSurfaceEndpoint endpoint) => new()
    {
        Route = endpoint.Route,
        Methods = endpoint.Methods.ToList(),
        Authorization = endpoint.AuthorizationKind.ToString(),
        Policies = endpoint.Policies.ToList(),
        Roles = endpoint.Roles.ToList(),
        Schemes = endpoint.AuthenticationSchemes.ToList(),
        UsesDefaultPolicy = endpoint.UsesDefaultPolicy,
        UsesFallbackPolicy = endpoint.UsesFallbackPolicy,
        Requirements = endpoint.Requirements.ToList(),
        RequirementFingerprint = endpoint.RequirementFingerprint,
    };

    private static AuthSurfaceEndpoint ToEndpoint(EndpointDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Route) || document.Methods is null || document.Methods.Count != 1 ||
            string.IsNullOrWhiteSpace(document.Methods[0]) || document.Requirements is null ||
            string.IsNullOrWhiteSpace(document.RequirementFingerprint) || document.RequirementFingerprint.Length != 64 ||
            document.Policies is null || document.Roles is null || document.Schemes is null)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "A baseline endpoint record is incomplete.");
        }

        if (!Enum.TryParse(document.Authorization, ignoreCase: false, out AuthSurfaceAuthorizationKind kind))
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "A baseline endpoint has an unknown authorization classification.");
        }

        if (document.RequirementFingerprint.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "A baseline endpoint has an invalid requirement fingerprint.");
        }

        return new AuthSurfaceEndpoint(
            AuthSurfaceCanonicalizer.NormalizeRoute(Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(document.Route)),
            document.Methods[0],
            kind,
            AuthSurfaceCanonicalizer.OrderedDistinct(document.Policies),
            AuthSurfaceCanonicalizer.OrderedDistinct(document.Roles),
            AuthSurfaceCanonicalizer.OrderedDistinct(document.Schemes),
            document.UsesDefaultPolicy,
            document.UsesFallbackPolicy,
            document.Requirements.OrderBy(static value => value, StringComparer.Ordinal),
            document.RequirementFingerprint.ToLowerInvariant());
    }

    private sealed class BaselineDocument
    {
        public int SchemaVersion { get; set; }

        public List<EndpointDocument>? Endpoints { get; set; }
    }

    private sealed class EndpointDocument
    {
        public string? Route { get; set; }

        public List<string>? Methods { get; set; }

        public string? Authorization { get; set; }

        public List<string>? Policies { get; set; }

        public List<string>? Roles { get; set; }

        public List<string>? Schemes { get; set; }

        public bool UsesDefaultPolicy { get; set; }

        public bool UsesFallbackPolicy { get; set; }

        public List<string>? Requirements { get; set; }

        public string? RequirementFingerprint { get; set; }
    }
}
