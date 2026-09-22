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

Baseline schema version 1 remains unchanged because its requirement array already stores an ordered sequence. This is a pre-release contract clarification. A schema-version-1 baseline produced by an earlier build may need regeneration when its requirement array reflects the former sorted order; schema-version-1 readers preserve the order stored in the file.
