namespace KeelMatrix.AuthSurface;

internal enum AuthSurfaceDiagnosticCode
{
    BaselineDuplicateField,
    BaselineDuplicateIdentity,
    BaselineEndpointLimit,
    BaselineFieldType,
    BaselineMalformed,
    BaselineReadFailed,
    BaselineSchemaUnsupported,
    BaselineTooDeep,
    BaselineTooLarge,
    BaselineTruncated,
    BaselineUnknownEndpointField,
    BaselineUnknownField,
    DuplicateEndpointIdentity,
    EndpointAdded,
    EndpointClassificationChanged,
    EndpointDefaultPolicyChanged,
    EndpointFallbackPolicyChanged,
    EndpointPolicyChanged,
    EndpointRemoved,
    EndpointRequirementChanged,
    EndpointRoleChanged,
    EndpointRouteChanged,
    EndpointSchemeChanged,
    FallbackPolicyEndpoint,
    PolicyResolutionFailed,
    UnprotectedEndpoint,
    UnsupportedParameterPolicy,
}

internal static class AuthSurfaceDiagnosticCodes
{
    internal static IReadOnlyList<string> All { get; } = Array.AsReadOnly(
        Enum.GetValues<AuthSurfaceDiagnosticCode>()
            .Select(static code => code.GetValue())
            .OrderBy(static code => code, StringComparer.Ordinal)
            .ToArray());

    internal static string GetValue(this AuthSurfaceDiagnosticCode code) =>
        code switch
        {
            AuthSurfaceDiagnosticCode.BaselineDuplicateField => "baseline-duplicate-field",
            AuthSurfaceDiagnosticCode.BaselineDuplicateIdentity => "baseline-duplicate-identity",
            AuthSurfaceDiagnosticCode.BaselineEndpointLimit => "baseline-endpoint-limit",
            AuthSurfaceDiagnosticCode.BaselineFieldType => "baseline-field-type",
            AuthSurfaceDiagnosticCode.BaselineMalformed => "baseline-malformed",
            AuthSurfaceDiagnosticCode.BaselineReadFailed => "baseline-read-failed",
            AuthSurfaceDiagnosticCode.BaselineSchemaUnsupported => "baseline-schema-unsupported",
            AuthSurfaceDiagnosticCode.BaselineTooDeep => "baseline-too-deep",
            AuthSurfaceDiagnosticCode.BaselineTooLarge => "baseline-too-large",
            AuthSurfaceDiagnosticCode.BaselineTruncated => "baseline-truncated",
            AuthSurfaceDiagnosticCode.BaselineUnknownEndpointField => "baseline-unknown-endpoint-field",
            AuthSurfaceDiagnosticCode.BaselineUnknownField => "baseline-unknown-field",
            AuthSurfaceDiagnosticCode.DuplicateEndpointIdentity => "duplicate-endpoint-identity",
            AuthSurfaceDiagnosticCode.EndpointAdded => "endpoint-added",
            AuthSurfaceDiagnosticCode.EndpointClassificationChanged => "endpoint-classification-changed",
            AuthSurfaceDiagnosticCode.EndpointDefaultPolicyChanged => "endpoint-default-policy-changed",
            AuthSurfaceDiagnosticCode.EndpointFallbackPolicyChanged => "endpoint-fallback-policy-changed",
            AuthSurfaceDiagnosticCode.EndpointPolicyChanged => "endpoint-policy-changed",
            AuthSurfaceDiagnosticCode.EndpointRemoved => "endpoint-removed",
            AuthSurfaceDiagnosticCode.EndpointRequirementChanged => "endpoint-requirement-changed",
            AuthSurfaceDiagnosticCode.EndpointRoleChanged => "endpoint-role-changed",
            AuthSurfaceDiagnosticCode.EndpointRouteChanged => "endpoint-route-changed",
            AuthSurfaceDiagnosticCode.EndpointSchemeChanged => "endpoint-scheme-changed",
            AuthSurfaceDiagnosticCode.FallbackPolicyEndpoint => "fallback-policy-endpoint",
            AuthSurfaceDiagnosticCode.PolicyResolutionFailed => "policy-resolution-failed",
            AuthSurfaceDiagnosticCode.UnprotectedEndpoint => "unprotected-endpoint",
            AuthSurfaceDiagnosticCode.UnsupportedParameterPolicy => "unsupported-parameter-policy",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown diagnostic code."),
        };
}
