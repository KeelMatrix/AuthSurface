# API reference

The shipping API is XML-documented in the `KeelMatrix.AuthSurface` assembly. The supported concepts are:

- `AuthSurfaceScanner` for completed runtime endpoint data sources and real policy-provider resolution;
- `AuthSurfaceScanOptions` for explicit exclusions and optional strict fallback enforcement;
- `AuthSurfaceReport` and `AuthSurfaceEndpoint` for immutable scan output;
- `AuthSurfaceAuthorizationKind` for the exact four classifications;
- `AuthSurfaceBaseline` for bounded schema-versioned read/write operations;
- `AuthSurfaceVerifier` and `AuthSurfaceVerificationResult` for policy and baseline comparison;
- `AuthSurfaceViolation` and structured exception types for actionable failures.

The package intentionally exposes no test-framework adapters, handler execution engine, identity-provider client, CLI, or generic security scanner.

## Requirement identity

`AuthSurfaceEndpoint.Requirements` preserves the order produced by ASP.NET Core while combining `IAuthorizeData`-derived requirements, explicit `AuthorizationPolicy` requirements, and `IAuthorizationRequirementData`. Requirement order is identity-significant: reversing requirements changes the canonical text, requirement fingerprint, and baseline comparison. Framework-preserved duplicate entries are retained; AuthSurface does not sort or silently deduplicate the framework's sequence.

The ordered `Requirements` sequence is the single source of truth for requirement identity. `AuthSurfaceEndpoint.RequirementFingerprint` is the SHA-256 fingerprint of only that sequence. Classification, named policies, roles, authentication schemes, and default/fallback provenance do not contribute to the fingerprint and are compared through their dedicated fields and diagnostic codes. `endpoint-requirement-changed` is emitted only when the ordered canonical requirements change.

Baseline schema version 1 remains unchanged because its requirement array already stores an ordered sequence and its fingerprint field remains a 64-character SHA-256 value. This is a pre-release contract clarification. A schema-version-1 baseline produced by an earlier build may need regeneration when its requirement array reflects the former sorted order or its fingerprint includes non-requirement authorization metadata. Schema-version-1 readers preserve the stored order and reject a fingerprint that does not match the sequence.

## Diagnostic code contract

The following table is the complete stable code set that the shipping assembly can emit through `AuthSurfaceViolation.Code`, `AuthSurfaceAnalysisException.Code`, or `AuthSurfaceBaselineException.Code`. It does not include human-readable message text or the `Expected`/`Actual` values attached to violations.

<!-- BEGIN:DIAGNOSTIC-CODES -->
| Code | Emission surface |
| --- | --- |
| `baseline-duplicate-field` | `AuthSurfaceBaselineException` |
| `baseline-duplicate-identity` | `AuthSurfaceBaselineException` |
| `baseline-endpoint-limit` | `AuthSurfaceBaselineException` |
| `baseline-field-type` | `AuthSurfaceBaselineException` |
| `baseline-malformed` | `AuthSurfaceBaselineException` |
| `baseline-read-failed` | `AuthSurfaceBaselineException` |
| `baseline-schema-unsupported` | `AuthSurfaceBaselineException` |
| `baseline-too-deep` | `AuthSurfaceBaselineException` |
| `baseline-too-large` | `AuthSurfaceBaselineException` |
| `baseline-truncated` | `AuthSurfaceBaselineException` |
| `baseline-unknown-endpoint-field` | `AuthSurfaceBaselineException` |
| `baseline-unknown-field` | `AuthSurfaceBaselineException` |
| `duplicate-endpoint-identity` | `AuthSurfaceAnalysisException` |
| `endpoint-added` | `AuthSurfaceViolation` |
| `endpoint-classification-changed` | `AuthSurfaceViolation` |
| `endpoint-default-policy-changed` | `AuthSurfaceViolation` |
| `endpoint-fallback-policy-changed` | `AuthSurfaceViolation` |
| `endpoint-policy-changed` | `AuthSurfaceViolation` |
| `endpoint-removed` | `AuthSurfaceViolation` |
| `endpoint-requirement-changed` | `AuthSurfaceViolation` |
| `endpoint-role-changed` | `AuthSurfaceViolation` |
| `endpoint-route-changed` | `AuthSurfaceViolation` |
| `endpoint-scheme-changed` | `AuthSurfaceViolation` |
| `fallback-policy-endpoint` | `AuthSurfaceViolation` |
| `policy-resolution-failed` | `AuthSurfaceAnalysisException` |
| `unprotected-endpoint` | `AuthSurfaceViolation` |
| `unsupported-parameter-policy` | `AuthSurfaceAnalysisException` |
<!-- END:DIAGNOSTIC-CODES -->

## Human-readable diff examples

The examples below show five common `AuthSurfaceVerifier` messages; the table above, not this example subset, defines the complete code set.

```text
endpoint-added
Endpoint '/internal/export' [GET] was added to the authorization surface.

endpoint-removed
Endpoint '/internal/export' [GET] was removed from the authorization surface.

endpoint-classification-changed
Endpoint '/secure' [GET] authorization classification changed from ExplicitProtected to ExplicitAnonymous.

endpoint-policy-changed
Endpoint '/secure' [GET] named policies changed from ["Read"] to ["Write"].

endpoint-requirement-changed
Endpoint '/secure' [GET] effective requirements changed from requirements=["requirement"]; fingerprint=79a5e127308a5a9cf7b4b805b8c675f2b6a8de8f55652be79846d74d5b6a892a to requirements=["changed"]; fingerprint=14ad2100f312362285a276d6d800d93c25a379f6e1fc6da5b34484a965c07f53.
```
