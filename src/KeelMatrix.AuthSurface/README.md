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

`authsurface.json` is bounded, UTF-8, schema-versioned JSON with deterministic ordering and `\n` newlines. Schema version 1 rejects unknown fields, wrong JSON types, malformed input, and unsupported future versions with structured `AuthSurfaceBaselineException` diagnostics. It never silently rewrites a file. Endpoint identity is the case-folded normalized route pattern plus HTTP method; the readable record may retain original casing. Equivalent route literals or parameter names therefore fail with `duplicate-endpoint-identity`, while genuinely different routes remain distinct.

Baseline comparison reports structured additions, removals, changes, and policy violations. Routes and methods appear only in local diagnostics; source locations, claims, tokens, request bodies, and user data are never collected. Use `AuthSurfaceScanOptions.ExcludedRoutePatterns` for intentional infrastructure exclusions. Strict fallback mode is available through `StrictFallbackPolicy` when every protected endpoint must carry endpoint-level authorization metadata.

## What AuthSurface proves

AuthSurface proves that completed runtime endpoint data sources contain the discovered route/method contracts and that the framework's effective policy metadata resolves to the recorded classification and supported requirement fingerprint. It can detect endpoint additions, removals, and authorization metadata changes against a reviewed local baseline.

## What AuthSurface does not prove

AuthSurface does not execute authorization handlers, create users or claims, mint tokens, contact identity providers, test request behavior, validate OpenAPI completeness, or prove route business behavior. A green result does **not** prove custom authorization handlers implement correct business rules.

## Limitations, privacy, and compatibility

The package targets `net8.0` and uses the `Microsoft.AspNetCore.App` framework reference. It does not require `WebApplicationFactory`, a test framework, a hosted service, middleware, a database, or a network service. Scan after the host has built its endpoint data sources. Dynamic policy resolution errors mean the application's provider could not resolve a named policy. Unsupported baseline versions require explicit migration; AuthSurface will not downgrade or regenerate them.

AuthSurface sends only the shared telemetry contract's bounded activation fields when telemetry is enabled, including pseudonymous `project_hash` and `installation_hash`; routes, URLs, policy names, roles, schemes, requirement types, controller/action names, app/service/repository names, filesystem paths, test names, claims, credentials, baseline contents, and diagnostics stay local. Disable telemetry with `KEELMATRIX_NO_TELEMETRY=1`. The baseline itself may reveal internal architecture and should be handled accordingly. See [`PRIVACY.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/PRIVACY.md) and [`docs/endpoint-identity.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/docs/endpoint-identity.md) for the maintained details.
