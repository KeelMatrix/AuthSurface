# AuthSurface development guide

## Navigation

- Shipping library: `src/KeelMatrix.AuthSurface`.
- Integration fixtures: `fixtures`.
- Tests: `tests`.
- Validation scripts: `scripts`.
- Package documentation: `docs` and the packable project's `README.md`.

## Commands

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh ./scripts/validate.ps1 -Configuration Release
```

Use focused `dotnet test` or `dotnet build` commands while developing, then run the validation script once after the implementation is final. The script performs controlled restore, static checks, Release build, tests, package inspection, vulnerability checks, and package-consumer smoke.

## Invariants

- The scanner reads completed runtime `EndpointDataSource` metadata and never uses API Explorer or action descriptors as its source of truth.
- Authorization policy resolution uses the application's `IAuthorizationPolicyProvider` and framework `AuthorizationPolicy.CombineAsync` behavior.
- Unknown requirements are represented as opaque type identities without object-graph reflection.
- Baselines are explicit, bounded, deterministic, schema-versioned, and never rewritten by comparison failures.
- No production request middleware, database, network service, or test-framework dependency is required by the shipping library.
