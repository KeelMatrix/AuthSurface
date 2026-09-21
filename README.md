# KeelMatrix.AuthSurface

**AuthSurface fails your tests when the authorization surface of an ASP.NET Core app changes unexpectedly.** It discovers runtime endpoints, resolves their effective authorization metadata, and compares the result with a reviewable baseline.

## Install

```powershell
dotnet add package KeelMatrix.AuthSurface --version 0.1.0
```

## Five-minute quick start

Resolve runtime services after the application has been built and its endpoint data sources have been constructed:

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
AuthSurfaceBaseline baseline = AuthSurfaceBaseline.Create(report);
baseline.Write(baselinePath, overwrite: false);

AuthSurfaceVerificationResult result = AuthSurfaceVerifier.Compare(
    report,
    AuthSurfaceBaseline.Read(baselinePath));
result.AssertValid();
```

Create the baseline explicitly, review it, and commit it. Comparison never creates or rewrites a baseline. Use `overwrite: true` only in an intentional baseline update command.

## What the scan records

AuthSurface emits one deterministic record for each normalized route and HTTP method contract. If one endpoint accepts `GET` and `POST`, it produces two records with the same route and one method in each record. This makes identity and policy differences explicit and freezes method ordering without depending on display names or controller names.

Each record contains the route pattern, method, one of the four authorization classifications, named policies, roles, authentication schemes, default/fallback contribution flags, canonical supported requirements, and a stable requirement fingerprint.

The classifications are:

- `ExplicitAnonymous`: runtime metadata contains `IAllowAnonymous`; this deliberate public surface remains visible.
- `ExplicitProtected`: endpoint authorization metadata or an explicit `AuthorizationPolicy` contributes a policy.
- `FallbackProtected`: no endpoint-specific authorization metadata exists, but the application's fallback policy protects it.
- `Unprotected`: no explicit anonymous metadata and no effective protecting policy exist. This fails the default policy check.

`[Authorize]`, `[AllowAnonymous]`, endpoint `.RequireAuthorization()`, route-group `.RequireAuthorization()`, named policies, roles, schemes, default policy, fallback policy, and dynamic policy providers are evaluated from runtime endpoint metadata and the application's real `IAuthorizationPolicyProvider`.

Unknown requirements are recorded only as a stable assembly/type identity plus an explicit `opaque` marker. AuthSurface never reflects arbitrary custom requirement object graphs.

## Baselines and diagnostics

`authsurface.json` is schema-versioned, bounded, UTF-8 JSON with deterministic ordering and `\n` newlines. Unsupported future schemas and malformed files fail closed with structured `AuthSurfaceBaselineException` errors. Duplicate normalized route/method identities fail analysis rather than dropping an endpoint.

Baseline comparison reports structured additions, removals, changes, and policy violations. Routes and methods appear in local diagnostics so a developer can fix the right endpoint; source locations, claims, tokens, request bodies, and user data are never collected.

Use `AuthSurfaceScanOptions.ExcludedRoutePatterns` for infrastructure endpoints that are intentionally outside the application's authorization surface. Strict fallback mode is available through `StrictFallbackPolicy` when every protected endpoint must carry endpoint-level authorization metadata.

## What AuthSurface proves

AuthSurface proves that the completed runtime endpoint data sources contain the discovered route/method contracts and that the framework's effective policy metadata resolves to the recorded classification and supported requirement fingerprint. It can detect endpoint additions, removals, and authorization metadata changes against a reviewed local baseline.

## What AuthSurface does not prove

AuthSurface does not execute authorization handlers, create users or claims, mint tokens, contact identity providers, test request behavior, validate OpenAPI completeness, or prove route business behavior. A green result does **not** prove custom authorization handlers implement correct business rules.

Routes, policy names, roles, schemes, requirement identities, baseline contents, and diagnostics stay local and are prohibited from telemetry. Baseline files may reveal internal architecture and should be handled accordingly.

## Compatibility and troubleshooting

The package targets `net8.0` and uses the `Microsoft.AspNetCore.App` framework reference. It is designed for .NET 8 ASP.NET Core applications on Windows, Linux, and macOS; each claimed platform requires runtime verification. No `WebApplicationFactory`, test framework, hosted service, middleware, database, or network service is required.

An empty endpoint set usually means scanning occurred before endpoint construction completed or the application has no `RouteEndpoint`. Resolve `EndpointDataSource` services after building the host. Dynamic policy resolution errors mean the application's provider could not resolve a named policy; inspect provider registration without exposing its internal exception text. Duplicate identity means two runtime route endpoints normalize to the same route and method and must be excluded or intentionally disambiguated. Unsupported baseline version requires an explicit migration; AuthSurface will not downgrade or regenerate it.

See the XML documentation on the public API and `docs/endpoint-identity.md` for the canonicalization decision.
