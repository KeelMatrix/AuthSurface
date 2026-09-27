using System.Text;

namespace KeelMatrix.AuthSurface;

/// <summary>
/// Bounded, lossless route-pattern data used only for persisted identity binding.
/// The display route and canonical identity are re-rendered from this data by the
/// canonicalizer; neither is trusted as an assertion supplied by the baseline.
/// </summary>
internal sealed class PersistedIdentityBinding
{
    internal const string Prefix = "b1:";

    private static readonly byte[] Magic = [(byte)'A', (byte)'S', (byte)'I', (byte)'B'];
    private static readonly Encoding BindingEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private const byte Version = 1;

    internal PersistedIdentityBinding(
        string method,
        IReadOnlyList<Segment> segments,
        string? identityRoute = null)
    {
        Method = method;
        Segments = segments;
        IdentityRoute = identityRoute;
    }

    internal string Method { get; }

    internal IReadOnlyList<Segment> Segments { get; }

    internal string? IdentityRoute { get; }

    internal PersistedIdentityBinding WithIdentityRoute(string identityRoute) =>
        new(Method, Segments, identityRoute);

    internal PersistedIdentityBinding WithMethod(string method) =>
        new(method, Segments, IdentityRoute);

    internal byte[] Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, BindingEncoding, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteString(writer, Method);
            if (IdentityRoute is null)
            {
                throw new InvalidOperationException("The persisted identity binding has no canonical identity route.");
            }

            WriteString(writer, IdentityRoute);
            writer.Write(Segments.Count);
            foreach (Segment segment in Segments)
            {
                writer.Write(segment.Parts.Count);
                foreach (Part part in segment.Parts)
                {
                    switch (part)
                    {
                        case LiteralPart literal:
                            writer.Write((byte)1);
                            WriteString(writer, literal.Content);
                            break;
                        case SeparatorPart separator:
                            writer.Write((byte)2);
                            WriteString(writer, separator.Content);
                            break;
                        case ParameterPart parameter:
                            writer.Write((byte)3);
                            writer.Write(parameter.IsCatchAll);
                            writer.Write(parameter.EncodeSlashes);
                            writer.Write(parameter.IsOptional);
                            WriteString(writer, parameter.Name);
                            writer.Write(parameter.Default is not null);
                            if (parameter.Default is not null)
                            {
                                WriteString(writer, parameter.Default);
                            }

                            writer.Write(parameter.Policies.Count);
                            foreach (Policy policy in parameter.Policies)
                            {
                                writer.Write(policy.IsContent);
                                WriteString(writer, policy.Content);
                            }

                            break;
                        default:
                            throw new InvalidOperationException("The persisted identity binding contains an unknown route part.");
                    }
                }
            }

            writer.Flush();
        }

        return stream.ToArray();
    }

    internal static PersistedIdentityBinding Deserialize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, BindingEncoding, leaveOpen: true);

        if (reader.ReadByte() != Magic[0] ||
            reader.ReadByte() != Magic[1] ||
            reader.ReadByte() != Magic[2] ||
            reader.ReadByte() != Magic[3] ||
            reader.ReadByte() != Version)
        {
            throw new FormatException("The persisted identity binding version is unsupported.");
        }

        string method = ReadString(reader, AuthSurfaceCanonicalizer.MaximumMetadataValueLength);
        string identityRoute = ReadString(reader, AuthSurfaceCanonicalizer.MaximumPersistedIdentityLength);
        int segmentCount = ReadCount(reader, AuthSurfaceCanonicalizer.MaximumEndpointCount);
        var segments = new List<Segment>(segmentCount);
        for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            int partCount = ReadCount(reader, AuthSurfaceCanonicalizer.MaximumRoutePolicyWork);
            var parts = new List<Part>(partCount);
            for (int partIndex = 0; partIndex < partCount; partIndex++)
            {
                switch (reader.ReadByte())
                {
                    case 1:
                        parts.Add(new LiteralPart(ReadString(reader, AuthSurfaceCanonicalizer.MaximumRoutePatternLength)));
                        break;
                    case 2:
                        parts.Add(new SeparatorPart(ReadString(reader, AuthSurfaceCanonicalizer.MaximumRoutePatternLength)));
                        break;
                    case 3:
                        {
                            bool isCatchAll = reader.ReadBoolean();
                            bool encodeSlashes = reader.ReadBoolean();
                            bool isOptional = reader.ReadBoolean();
                            string name = ReadString(reader, AuthSurfaceCanonicalizer.MaximumMetadataValueLength);
                            string? defaultValue = reader.ReadBoolean()
                                ? ReadString(reader, AuthSurfaceCanonicalizer.MaximumRoutePatternLength)
                                : null;
                            int policyCount = ReadCount(reader, AuthSurfaceCanonicalizer.MaximumNestedValueCount);
                            var policies = new List<Policy>(policyCount);
                            for (int policyIndex = 0; policyIndex < policyCount; policyIndex++)
                            {
                                bool isContent = reader.ReadBoolean();
                                policies.Add(new Policy(
                                    isContent,
                                    ReadString(reader, AuthSurfaceCanonicalizer.MaximumRoutePolicyLength)));
                            }

                            parts.Add(new ParameterPart(
                                name,
                                isCatchAll,
                                encodeSlashes,
                                isOptional,
                                defaultValue,
                                policies));
                            break;
                        }
                    default:
                        throw new FormatException("The persisted identity binding contains an unknown route part.");
                }
            }

            segments.Add(new Segment(parts));
        }

        if (stream.Position != stream.Length)
        {
            throw new FormatException("The persisted identity binding contains trailing data.");
        }

        return new PersistedIdentityBinding(method, segments, identityRoute);
    }

    internal abstract class Part
    {
    }

    internal sealed class Segment
    {
        internal Segment(IReadOnlyList<Part> parts) => Parts = parts;

        internal IReadOnlyList<Part> Parts { get; }
    }

    internal sealed class LiteralPart : Part
    {
        internal LiteralPart(string content) => Content = content;

        internal string Content { get; }
    }

    internal sealed class SeparatorPart : Part
    {
        internal SeparatorPart(string content) => Content = content;

        internal string Content { get; }
    }

    internal sealed class ParameterPart : Part
    {
        internal ParameterPart(
            string name,
            bool isCatchAll,
            bool encodeSlashes,
            bool isOptional,
            string? @default,
            IReadOnlyList<Policy> policies)
        {
            Name = name;
            IsCatchAll = isCatchAll;
            EncodeSlashes = encodeSlashes;
            IsOptional = isOptional;
            Default = @default;
            Policies = policies;
        }

        internal string Name { get; }

        internal bool IsCatchAll { get; }

        internal bool EncodeSlashes { get; }

        internal bool IsOptional { get; }

        internal string? Default { get; }

        internal IReadOnlyList<Policy> Policies { get; }
    }

    internal sealed class Policy
    {
        internal Policy(bool isContent, string content)
        {
            IsContent = isContent;
            Content = content;
        }

        internal bool IsContent { get; }

        internal string Content { get; }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        if (value.Length > AuthSurfaceCanonicalizer.MaximumPersistedIdentityLength)
        {
            throw AuthSurfaceCanonicalizer.RoutePatternTooLarge();
        }

        writer.Write(value);
    }

    private static string ReadString(BinaryReader reader, int maximumCharacters)
    {
        string value = reader.ReadString();
        if (value.Length > maximumCharacters)
        {
            throw new FormatException("The persisted identity binding contains an oversized string.");
        }

        return value;
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
        {
            throw new FormatException("The persisted identity binding contains an invalid collection count.");
        }

        return count;
    }
}
