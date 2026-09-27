# Changelog

All notable changes to this package will be documented here.

## [Unreleased]

- Hardened baseline identity validation and route persistence for method binding, malformed/stale records, programmatic defaults, identity-significant policy/default/regex mutations, and duplicate route/method entries. For unparseable or provenance-ambiguous displays, persisted identities now compare exact route payloads and recognize the `text:` provenance marker only at policy boundaries; marker-like text in defaults or policy payloads is not stripped.
- Matched anonymous policy construction to ASP.NET Core fallback-policy behavior, enforced cumulative scanner-owned expansion/resource budgets across route methods and authorization values, and rejected derived route-policy wrappers that are not exact supported runtime types.
- Preserved distinct textual and programmatic route-policy provenance, including for content-bearing values that begin with the reserved `programmatic:` marker.
- Applied cumulative scan-wide bounds to route rendering/policy expansion, authorization metadata, and requirement-data expansion; requirement-data items are charged before materialization, with exact-boundary, aggregate, cancellation, and per-endpoint-limit coverage.
- Corrected repository-hygiene coverage for Dependabot web-flow commits and isolated shallow-history negative coverage.

## 0.1.0 - Pre-release

### Added

- Runtime ASP.NET Core endpoint discovery from completed `EndpointDataSource` metadata.
- Four-state authorization classification for explicit anonymous, explicit protected, fallback protected, and unprotected endpoints.
- Real authorization-policy-provider resolution including framework requirement-data policy construction, SHA-256 fingerprints derived only from ordered canonical requirements, exact-type framework requirement canonicalization with opaque custom identities, lossless deterministic route/method identity, and explicit baseline creation/comparison.
- Bounded route-policy parsing, metadata enumeration, and baseline validation with structured fail-closed diagnostics, plus complete-history hygiene and semantic release-contract checks.
- Bounded schema-versioned `authsurface.json` read/write validation with actionable structured diagnostics and one synchronized stable code inventory covering baseline, scan-policy, analysis, and comparison results.
- Bounded activation telemetry after real scan plus policy/baseline evaluation, with opt-out through `KEELMATRIX_NO_TELEMETRY=1`; telemetry is best-effort and cannot affect results.
- `net8.0` NuGet packaging, package-content inspection, cross-platform validation, and an isolated `PackageReference` consumer smoke path.

Important limitations: AuthSurface does not execute authorization handlers, authenticate users, validate identity-provider configuration, prove business authorization semantics, or perform generic vulnerability/route-behavior scanning. This entry is intentionally pre-release and is not finalized for publication.
