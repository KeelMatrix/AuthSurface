# Changelog

All notable changes to this package will be documented here.

## [Unreleased]

## 0.1.0 - Pre-release

### Added

- Runtime ASP.NET Core endpoint discovery from completed `EndpointDataSource` metadata.
- Four-state authorization classification for explicit anonymous, explicit protected, fallback protected, and unprotected endpoints.
- Real authorization-policy-provider resolution, supported framework requirement fingerprints, opaque custom requirement identities, deterministic route/method identity, and explicit baseline creation/comparison.
- Bounded schema-versioned `authsurface.json` read/write validation with actionable structured diagnostics.
- `net8.0` NuGet packaging, package-content inspection, cross-platform validation, and an isolated `PackageReference` consumer smoke path.

Important limitations: AuthSurface does not execute authorization handlers, authenticate users, validate identity-provider configuration, prove business authorization semantics, or perform generic vulnerability/route-behavior scanning. This entry is intentionally pre-release and is not finalized for publication.
