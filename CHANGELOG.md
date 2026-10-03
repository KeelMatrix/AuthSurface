# Changelog

All notable changes to this package will be documented here.

## [Unreleased]

- Kept collection-level endpoint identity consistent across scan, baseline creation, persistence, reading, and comparison so distinct proven structural identities with colliding readable route display remain readable and comparable.

## 0.1.0 - Pre-release

### Added

- Runtime ASP.NET Core endpoint discovery with effective authorization resolved through the application's `IAuthorizationPolicyProvider`, four-state classification (`ExplicitAnonymous`, `ExplicitProtected`, `FallbackProtected`, and `Unprotected`), and default policy checks for unprotected endpoints.
- Deterministic normalized route/method identities and explicit baseline creation/comparison for schema-versioned `authsurface.json`, with required endpoint fields, explicit policy-contribution booleans, a shared 1 MiB writer/reader bound, and bounded validation that fails closed on malformed, unsupported, duplicate, or ambiguous records. Programmatic regex policies use delimiter-safe `regex64` UTF-8/base64url encoding. Baseline creation rejects a non-encoded catch-all combined with a generated or content-less parameter policy with `unsupported-parameter-policy`.
- Bounded, best-effort telemetry after real scan and policy or baseline evaluation, excluding endpoint and authorization details and supporting opt-out through `KEELMATRIX_NO_TELEMETRY=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, or `DO_NOT_TRACK=1`; targets `net8.0`.
- Defines the package boundary: it does not authenticate users, execute authorization handlers, validate identity-provider configuration, prove business-level authorization semantics, or perform generic vulnerability or route-behavior scanning; the local baseline may reveal application architecture and should be protected.
