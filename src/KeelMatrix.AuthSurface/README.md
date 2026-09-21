# KeelMatrix.AuthSurface

**AuthSurface fails your tests when the authorization surface of an ASP.NET Core app changes unexpectedly.** It discovers runtime endpoints, resolves their effective authorization metadata, and compares the result with a reviewable baseline.

## Install

```powershell
dotnet add package KeelMatrix.AuthSurface --version 0.1.0
```

## Quick start

Resolve runtime services after the application has been built and endpoint data sources have been constructed:

```csharp
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

var scanner = new AuthSurfaceScanner(
    app.Services.GetServices<EndpointDataSource>(),
    app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
AuthSurfaceReport report = await scanner.ScanAsync();
report.AssertPolicyCompliant();

string baselinePath = Path.Combine("tests", "authsurface.json");
AuthSurfaceBaseline.Create(report, baselinePath, overwrite: false);
AuthSurfaceVerifier.Compare(report, baselinePath).AssertValid();
```

### Baseline creation and updates

Create a baseline explicitly with `AuthSurfaceBaseline.Create(report, path, overwrite: false)`, review it, and commit it. Comparison never creates or rewrites a baseline. Update it only through an intentional command or test change that calls `Write(..., overwrite: true)` after reviewing the current scan.

## What the scan records

AuthSurface emits one deterministic record for each normalized route and HTTP method contract. An endpoint accepting `GET` and `POST` produces one record per method. Each record contains the route pattern, method, one of the four classifications, named policies, roles, authentication schemes, default/fallback contribution flags, canonical supported requirements, and a stable requirement fingerprint.

The four classifications are:

- `ExplicitAnonymous`: runtime metadata contains `IAllowAnonymous`; the intentional public endpoint remains visible.
- `ExplicitProtected`: endpoint authorization metadata or an explicit `AuthorizationPolicy` contributes a policy.
- `FallbackProtected`: no endpoint-specific authorization metadata exists, but the application's fallback policy protects it.
- `Unprotected`: no explicit anonymous metadata and no effective protecting policy exist; this fails the default policy check.

`[Authorize]`, `[AllowAnonymous]`, endpoint and route-group `.RequireAuthorization()`, named policies, roles, schemes, default/fallback policy, and dynamic policy providers are evaluated from runtime endpoint metadata and the application's real `IAuthorizationPolicyProvider`. Unknown requirements are represented only by a stable type identity and an explicit `opaque` marker; arbitrary custom requirement object graphs are never reflected.

## Baselines, identity, and diagnostics

`authsurface.json` is bounded, UTF-8, schema-versioned JSON with deterministic ordering and `\n` newlines. Schema version 1 accepts one leading UTF-8 BOM when the document is otherwise valid; a raw BOM inside the document is invalid, while an escaped `\uFEFF` sequence inside a JSON string is treated as ordinary string content. The writer always emits BOM-free UTF-8. It rejects schema errors with structured `AuthSurfaceBaselineException` diagnostics and never silently rewrites a file. Duplicate and unknown property names are rendered in diagnostics at no more than 128 characters; longer names end with `…(truncated)` to keep diagnostic text bounded.

### Baseline diagnostic troubleshooting

| Code | Meaning | What to do |
| --- | --- | --- |
| `baseline-duplicate-field` | A property name appears more than once in one JSON object. The message names the property and identifies the top level or the endpoint entry (`$.endpoints[index]`). | Remove the duplicate property and keep the value that matches the intended schema. |
| `baseline-duplicate-identity` | Two endpoint records normalize to the same route and HTTP method identity. | Remove the duplicate record or correct its route/method before regenerating the baseline. |
| `baseline-endpoint-limit` | The baseline endpoint list is missing or exceeds the supported 100,000-record bound. | Reduce the baseline to the intended endpoint set and regenerate it from a bounded scan. |
| `baseline-truncated` | The document ends in the middle of a JSON structure, such as an object, array, string, or escape sequence. | Restore the missing bytes or regenerate the baseline from a trusted scan. |
| `baseline-malformed` | The file is empty, whitespace-only, has a syntax error, contains an invalid BOM placement, or is otherwise not valid JSON for schema version 1. | Fix the JSON syntax or regenerate the baseline; a malformed file is never rewritten. |
| `baseline-too-deep` | A complete JSON document exceeds the supported nesting depth. This is distinct from `baseline-truncated`, which means the document ends before its structure is complete. | Remove unexpected nesting and regenerate the baseline if the content is intentional. |
| `baseline-unknown-field` | An unrecognized property appears at the top level. | Remove the property or migrate the file to the supported schema. |
| `baseline-unknown-endpoint-field` | An unrecognized property appears in an endpoint entry. | Remove the property or migrate the file to the supported schema. |
| `baseline-field-type` | A known property has the wrong JSON type, such as a string where an array or boolean is required. | Change the value to the type required by schema version 1. |
| `baseline-schema-unsupported` | The file declares a schema version other than the supported version 1. | Migrate the file explicitly to schema version 1; AuthSurface does not downgrade it automatically. |
| `baseline-too-large` | The file exceeds the default 1 MiB input limit (or the limit supplied to `Read`). | Reduce the baseline size or provide an intentional, bounded maximum appropriate for the application. |
| `baseline-read-failed` | The baseline could not be opened or read because of an I/O or access failure. | Check that the path exists and that the process has permission to read the file, then retry. |

For all diagnostics, inspect the `Code` and message on `AuthSurfaceBaselineException`. The original file remains byte-for-byte unchanged on failure.

Endpoint identity is the case-folded normalized route pattern plus HTTP method; the readable record may retain original casing. Equivalent route literals or parameter names therefore fail with `duplicate-endpoint-identity`, while genuinely different routes remain distinct.

Baseline comparison reports structured additions, removals, changes, and policy violations. Routes and methods appear only in local diagnostics; source locations, claims, tokens, request bodies, and user data are never collected. Use `AuthSurfaceScanOptions.ExcludedRoutePatterns` for intentional infrastructure exclusions. Strict fallback mode is available through `StrictFallbackPolicy` when every protected endpoint must carry endpoint-level authorization metadata.

## What AuthSurface proves

AuthSurface proves that completed runtime endpoint data sources contain the discovered route/method contracts and that the framework's effective policy metadata resolves to the recorded classification and supported requirement fingerprint. It can detect endpoint additions, removals, and authorization metadata changes against a reviewed local baseline.

## What AuthSurface does not prove

AuthSurface does not execute authorization handlers, create users or claims, mint tokens, contact identity providers, test request behavior, validate OpenAPI completeness, or prove route business behavior. A green result does **not** prove custom authorization handlers implement correct business rules.

## Limitations, privacy, and compatibility

The package targets `net8.0` and uses the `Microsoft.AspNetCore.App` framework reference. It does not require `WebApplicationFactory`, a test framework, a hosted service, middleware, a database, or a network service. Scan after the host has built its endpoint data sources. Dynamic policy resolution errors mean the application's provider could not resolve a named policy. Unsupported baseline versions require explicit migration; AuthSurface will not downgrade or regenerate them.

AuthSurface sends only the shared telemetry contract's bounded activation fields when telemetry is enabled, including pseudonymous `project_hash` and `installation_hash`; routes, URLs, policy names, roles, schemes, requirement types, controller/action names, app/service/repository names, filesystem paths, test names, claims, credentials, baseline contents, and diagnostics stay local. Disable telemetry with `KEELMATRIX_NO_TELEMETRY=1`. The baseline itself may reveal internal architecture and should be handled accordingly. See [`PRIVACY.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/PRIVACY.md) and [`docs/endpoint-identity.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/docs/endpoint-identity.md) for the maintained details.
