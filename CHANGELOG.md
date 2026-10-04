# Changelog

All notable changes to this package will be documented here.

## [Unreleased]

## [0.1.0] - 2026-10-04

### Added

- Provides a test-framework-independent .NET 8 library that discovers completed ASP.NET Core route endpoints, resolves effective authorization through the application's policy provider, and classifies each endpoint as explicitly anonymous, explicitly protected, fallback protected, or unprotected.
- Creates explicit, deterministic `authsurface.json` baselines and compares endpoint and authorization contracts, reporting endpoint additions and removals, unprotected endpoints, and policy changes for review in tests.
- Uses a versioned baseline format with bounded, fail-closed validation; failed reads and comparisons do not rewrite baseline files.
- Requests best-effort shared activation telemetry only after nonempty scan evaluation; route and authorization details remain local, shared-client opt-out applies, and telemetry cannot affect results.
- Defines the package boundary: AuthSurface inspects authorization metadata but does not execute authorization handlers or verify business-level authorization decisions.
