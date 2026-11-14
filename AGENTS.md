# AuthSurface development guide

## Navigation

- Shipping library: `src/KeelMatrix.AuthSurface`.
- Integration fixtures: `fixtures`.
- Tests: `tests`.
- Validation scripts: `scripts`.
- Non-shipping packaged source verifier: `build/SourceProvenance`.
- Package documentation: `docs` and the packable project's `README.md`.

## Commands

```powershell
$env:KEELMATRIX_NO_TELEMETRY = '1'
pwsh ./scripts/validate.ps1 -Configuration Release
```

Use focused `dotnet test` or `dotnet build` commands while developing, then run the validation script once after the implementation is final. The script performs controlled restore, static checks, Release build, tests, package inspection, vulnerability checks, and package-consumer smoke.

Accepted archives come from a private public-only locked dependency session kept
alive through compilation, tests, pack and inspection. Raw inner-loop commands
do not establish this build-input guarantee. Start preparation changes with
`pwsh ./scripts/test-build-inputs.ps1`; start install changes with
`pwsh ./scripts/test-consumer-smoke.ps1 -PackagePath <actual-nupkg>`.
The build-input/session contract is owned by `docs/maintaining-release-workflow.md`;
the packed project's README owns the literal consumer installation sequence.
Keep standard project lock files current with intentional fresh public restores.
Do not refresh locks during accepted validation or modify shared package caches.

For source-provenance changes, start with `dotnet test tests/KeelMatrix.AuthSurface.Tests -c Release --filter FullyQualifiedName~SourceProvenanceTests`.
Package inspection checks the exact DLL/PDB pair and every document against the
explicit checkout commit or valid embedded source; it also runs fatal caller
regressions. See the [source-provenance contract](docs/maintaining-release-workflow.md#packaged-source-provenance)
for focused archive commands and the distinction between local Git and HTTP proof.

For package classification changes, run `pwsh ./scripts/check-website-metadata.ps1` and `pwsh ./scripts/test-website-metadata.ps1` first. The root `keelmatrix.website.json` owns classification; see [package discovery metadata](docs/package-metadata.md) for source and archive validation.

## Invariants

- The scanner reads completed runtime `EndpointDataSource` metadata and never uses API Explorer or action descriptors as its source of truth.
- Authorization policy resolution uses the application's `IAuthorizationPolicyProvider` and framework `AuthorizationPolicy.CombineAsync` behavior.
- Unknown requirements are represented as opaque type identities without object-graph reflection.
- Baselines are explicit, bounded, deterministic, schema-versioned, and never rewritten by comparison failures.
- No production request middleware, database, network service, or test-framework dependency is required by the shipping library.
