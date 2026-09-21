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
