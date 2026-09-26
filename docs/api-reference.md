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

## Strict fallback contract

`AuthSurfaceScanOptions.StrictFallbackPolicy` rejects an endpoint when its effective authorization policy includes fallback-policy contribution. An explicitly protected endpoint can therefore produce `fallback-policy-endpoint` when requirement data or other explicit metadata is combined with fallback. The diagnostic remediation is to supply a complete endpoint/default/named/direct policy path that prevents fallback from contributing, or to disable strict fallback enforcement intentionally.

## Endpoint identity contract

Endpoint identity is a deterministic canonical representation of the runtime `RoutePattern` structure plus HTTP method, not a proof of general route-matching semantic equivalence. `RawText` is not used as a lossy shortcut: merged defaults, optional/catch-all shape, literal braces, and parameter policies participate in the representation. Textual and programmatic parameter policies retain separate provenance markers; AuthSurface does not infer that a textual token such as `int` resolves to the built-in constraint when an application may remap it. Built-in textual token casing, equal one-value/equal-range length constraints, HTTP-method constraint casing/order/duplicates, and composite child order are normalized. Inline regex commas and literal `;options=` remain pattern text; route-syntax braces in regex content are escaped for round-trip persistence. Supported programmatic regex requires the framework inline defaults, and other options fail closed with `unsupported-parameter-policy`. Matching bounded identities emit `duplicate-endpoint-identity`; representations outside the bounded rules may remain distinct. A version-1 endpoint may carry an optional lossless `identity` field when readable route text cannot preserve programmatic provenance.

## Requirement identity

`AuthSurfaceEndpoint.Requirements` preserves the order produced by ASP.NET Core while combining `IAuthorizeData`-derived requirements, explicit `AuthorizationPolicy` requirements, and `IAuthorizationRequirementData`. If requirement-data metadata is present, AuthSurface constructs the same requirement-data policy as authorization middleware, including the empty-policy failure. Requirement order is identity-significant: reversing requirements changes the canonical text, requirement fingerprint, and baseline comparison. Framework-preserved duplicate entries are retained; AuthSurface does not sort or silently deduplicate the framework's sequence.

The ordered `Requirements` sequence is the single source of truth for requirement identity. `AuthSurfaceEndpoint.RequirementFingerprint` is the SHA-256 fingerprint of only that sequence. Classification, named policies, roles, authentication schemes, and default/fallback provenance do not contribute to the fingerprint and are compared through their dedicated fields and diagnostic codes. `endpoint-requirement-changed` is emitted only when the ordered canonical requirements change.

Baseline schema version 1 stores the readable route plus an optional lossless identity for structural route representations that cannot be reconstructed from display text. Existing version-1 baselines without that optional field remain readable. The requirement fingerprint remains a 64-character SHA-256 value, and readers preserve the stored order and reject a fingerprint that does not match the sequence.

The supported requirement value boundary is exact runtime type identity for the framework requirement types whose complete values AuthSurface serializes. Derived or custom requirements retain stable type identity with an explicit `opaque` marker; AuthSurface does not inspect arbitrary custom state. Route canonicalization is bounded to a 16,384-character route, 8,192-character policy expression, depth 32, and 100,000 policy-work operations. A scan also bounds endpoints and requirement metadata at 100,000 items. These limits fail closed with structured analysis diagnostics. Application callbacks are invoked synchronously; cancellation is checked between scanner-owned enumeration steps and cannot interrupt arbitrary callback code.

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
| `endpoint-limit` | `AuthSurfaceAnalysisException` |
| `endpoint-policy-changed` | `AuthSurfaceViolation` |
| `endpoint-removed` | `AuthSurfaceViolation` |
| `endpoint-requirement-changed` | `AuthSurfaceViolation` |
| `endpoint-role-changed` | `AuthSurfaceViolation` |
| `endpoint-route-changed` | `AuthSurfaceViolation` |
| `endpoint-scheme-changed` | `AuthSurfaceViolation` |
| `fallback-policy-endpoint` | `AuthSurfaceViolation` |
| `metadata-limit` | `AuthSurfaceAnalysisException` |
| `policy-resolution-failed` | `AuthSurfaceAnalysisException` |
| `route-pattern-too-large` | `AuthSurfaceAnalysisException` |
| `route-policy-too-complex` | `AuthSurfaceAnalysisException` |
| `route-policy-too-deep` | `AuthSurfaceAnalysisException` |
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
