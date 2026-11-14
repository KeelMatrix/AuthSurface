# Changelog

All notable changes to this package will be documented here.

## [Unreleased]

### Fixed

- Unpublished source builds preserve repository-relative SourceLink paths and embed generated source. This does not change the broken automatic source retrieval in public 0.1.0.
- Release automation requires the matching symbol archive, uses v3 symbol-service discovery, and retains inspected archives with SHA-256 hashes before publication.

### Added

- Packaged DLL/PDB identity and complete source-document verification before validation, rehearsal, and release preparation can succeed.
- A separate manual symbols recovery route verifies the preserved 0.1.0 archive and shipping identity, retains evidence before authentication, and records HTTP acceptance or uncertain effects without an application retry. Preparing this route does not publish or index symbols.

## [0.1.0] - 2026-11-11

### Added

- Provides a test-framework-independent .NET 8 library that discovers completed ASP.NET Core route endpoints, resolves effective authorization through the application's policy provider, and classifies each endpoint as explicitly anonymous, explicitly protected, fallback protected, or unprotected.
- Creates explicit, deterministic `authsurface.json` baselines and compares endpoint and authorization contracts, reporting endpoint additions and removals, unprotected endpoints, and policy changes for review in tests.
- Uses a versioned baseline format with bounded, fail-closed validation; failed reads and comparisons do not rewrite baseline files.
- Requests best-effort shared activation telemetry only after nonempty scan evaluation; route and authorization details remain local, shared-client opt-out applies, and telemetry cannot affect results.
- Defines the package boundary: AuthSurface inspects authorization metadata but does not execute authorization handlers or verify business-level authorization decisions.
