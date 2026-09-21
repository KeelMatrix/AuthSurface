using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeelMatrix.AuthSurface;

/// <summary>Represents a validated, deterministic AuthSurface baseline.</summary>
public sealed class AuthSurfaceBaseline
{
    /// <summary>The first supported baseline schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private const int DiagnosticPropertyNameLimit = 128;

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

        byte[] jsonBytes = bytes;
        if (jsonBytes.Length >= 3 && jsonBytes[0] == 0xEF && jsonBytes[1] == 0xBB && jsonBytes[2] == 0xBF)
        {
            jsonBytes = bytes[3..];
        }

        ValidateJsonSyntaxBeforeDepth(jsonBytes);

        JsonDocument jsonDocument;
        try
        {
            jsonDocument = JsonDocument.Parse(jsonBytes, JsonDocumentOptions);
        }
        catch (JsonException exception)
        {
            string code = exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase)
                ? "baseline-too-deep"
                : "baseline-malformed";
            string message = code == "baseline-too-deep"
                ? "The baseline exceeds the supported JSON nesting depth."
                : "The baseline is not valid JSON.";
            throw new AuthSurfaceBaselineException(code, message, exception);
        }

        using (jsonDocument)
        {
            ValidateSchema(jsonDocument.RootElement);
        }

        BaselineDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BaselineDocument>(jsonBytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline is not valid according to schema version 1.", exception);
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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonDocumentOptions JsonDocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = 32,
    };

    private static readonly HashSet<string> TopLevelFields = ["schemaVersion", "endpoints"];

    private static readonly HashSet<string> EndpointFields =
    [
        "route",
        "methods",
        "authorization",
        "policies",
        "roles",
        "schemes",
        "usesDefaultPolicy",
        "usesFallbackPolicy",
        "requirements",
        "requirementFingerprint",
    ];

    private static void ValidateJsonSyntaxBeforeDepth(byte[] bytes)
    {
        bool hasNonWhitespace = false;
        foreach (byte value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                hasNonWhitespace = true;
                break;
            }
        }

        if (!hasNonWhitespace)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline is empty or contains only whitespace.");
        }

        JsonReaderOptions options = new()
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = Math.Max(bytes.Length, 1),
        };

        Utf8JsonReader reader = new(bytes, options);
        try
        {
            bool hasValue = false;
            while (reader.Read())
            {
                hasValue = true;
            }

            if (!hasValue)
            {
                throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline is empty or contains only whitespace.");
            }
        }
        catch (AuthSurfaceBaselineException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            bool truncated = IsAtEndOfPayload(bytes, exception);
            string code = truncated ? "baseline-truncated" : "baseline-malformed";
            string message = truncated
                ? "The baseline ends before its JSON document is complete."
                : "The baseline is not valid JSON.";
            throw new AuthSurfaceBaselineException(code, message, exception);
        }
    }

    private static bool IsAtEndOfPayload(byte[] bytes, JsonException exception)
    {
        if (!exception.LineNumber.HasValue || !exception.BytePositionInLine.HasValue ||
            exception.LineNumber.Value < 0 || exception.BytePositionInLine.Value < 0)
        {
            return false;
        }

        long line = 0;
        long lineStart = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }

            if (line == exception.LineNumber.Value)
            {
                return lineStart + exception.BytePositionInLine.Value >= bytes.Length;
            }

            line++;
            lineStart = index + 1;
        }

        return line == exception.LineNumber.Value &&
            lineStart + exception.BytePositionInLine.Value >= bytes.Length;
    }

    private static void ValidateSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AuthSurfaceBaselineException("baseline-malformed", "The baseline root must be a JSON object.");
        }

        ValidateKnownFields(root, TopLevelFields, "baseline-unknown-field", "top-level");

        if (root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) &&
            (schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out _)))
        {
            throw new AuthSurfaceBaselineException(
                "baseline-field-type",
                "The baseline field '$.schemaVersion' must be a JSON integer.");
        }

        if (!root.TryGetProperty("endpoints", out JsonElement endpoints))
        {
            return;
        }

        if (endpoints.ValueKind != JsonValueKind.Array)
        {
            throw new AuthSurfaceBaselineException(
                "baseline-field-type",
                "The baseline field '$.endpoints' must be a JSON array.");
        }

        int index = 0;
        foreach (JsonElement endpoint in endpoints.EnumerateArray())
        {
            string path = "$.endpoints[" + index.ToString(CultureInfo.InvariantCulture) + "]";
            if (endpoint.ValueKind != JsonValueKind.Object)
            {
                throw new AuthSurfaceBaselineException(
                    "baseline-field-type",
                    $"The baseline value '{path}' must be a JSON object.");
            }

            ValidateKnownFields(endpoint, EndpointFields, "baseline-unknown-endpoint-field", path);
            ValidateEndpointFieldTypes(endpoint, path);
            index++;
        }
    }

    private static void ValidateKnownFields(
        JsonElement value,
        HashSet<string> knownFields,
        string errorCode,
        string scope)
    {
        HashSet<string> seenFields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!seenFields.Add(property.Name))
            {
                throw new AuthSurfaceBaselineException(
                    "baseline-duplicate-field",
                    $"The baseline field '{RenderDiagnosticPropertyName(property.Name)}' is duplicated at {scope}.");
            }
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!knownFields.Contains(property.Name))
            {
                throw new AuthSurfaceBaselineException(
                    errorCode,
                    $"The baseline field '{scope}.{RenderDiagnosticPropertyName(property.Name)}' is not declared in schema version 1.");
            }
        }
    }

    private static string RenderDiagnosticPropertyName(string propertyName)
    {
        const string truncationMarker = "…(truncated)";
        if (propertyName.Length <= DiagnosticPropertyNameLimit)
        {
            return propertyName;
        }

        return propertyName[..(DiagnosticPropertyNameLimit - truncationMarker.Length)] + truncationMarker;
    }

    private static void ValidateEndpointFieldTypes(JsonElement endpoint, string path)
    {
        ValidateString(endpoint, "route", path);
        ValidateString(endpoint, "authorization", path);
        ValidateString(endpoint, "requirementFingerprint", path);
        ValidateStringArray(endpoint, "methods", path);
        ValidateStringArray(endpoint, "policies", path);
        ValidateStringArray(endpoint, "roles", path);
        ValidateStringArray(endpoint, "schemes", path);
        ValidateStringArray(endpoint, "requirements", path);
        ValidateBoolean(endpoint, "usesDefaultPolicy", path);
        ValidateBoolean(endpoint, "usesFallbackPolicy", path);
    }

    private static void ValidateString(JsonElement value, string name, string path)
    {
        if (value.TryGetProperty(name, out JsonElement property) && property.ValueKind != JsonValueKind.String)
        {
            throw new AuthSurfaceBaselineException(
                "baseline-field-type",
                $"The baseline field '{path}.{name}' must be a JSON string.");
        }
    }

    private static void ValidateStringArray(JsonElement value, string name, string path)
    {
        if (!value.TryGetProperty(name, out JsonElement property))
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Array || property.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
        {
            throw new AuthSurfaceBaselineException(
                "baseline-field-type",
                $"The baseline field '{path}.{name}' must be a JSON array of strings.");
        }
    }

    private static void ValidateBoolean(JsonElement value, string name, string path)
    {
        if (value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new AuthSurfaceBaselineException(
                "baseline-field-type",
                $"The baseline field '{path}.{name}' must be a JSON boolean.");
        }
    }

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
