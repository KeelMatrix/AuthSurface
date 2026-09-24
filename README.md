# KeelMatrix.AuthSurface

AuthSurface tests the runtime ASP.NET Core authorization surface. It discovers endpoints, resolves effective authorization metadata, and compares the result with a reviewable local baseline.

## Install

```powershell
dotnet add package KeelMatrix.AuthSurface --version 0.1.0
```

## Quick Start

Build the host and its endpoint metadata before scanning. Create the baseline once, review and commit it, then use only the comparison path in recurring tests:

```csharp
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthorization();
WebApplication app = builder.Build();
app.MapGet("/health", () => Results.Ok()).AllowAnonymous();
app.MapGet("/orders", () => Results.Ok()).RequireAuthorization();
await app.StartAsync();

AuthSurfaceScanner scanner = new(
    app.Services.GetServices<EndpointDataSource>(),
    app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
AuthSurfaceReport report = await scanner.ScanAsync();
report.AssertPolicyCompliant();

// One-time, reviewed setup:
AuthSurfaceBaseline.Create(report, "authsurface.json", overwrite: false);

// Recurring comparison test (after authsurface.json is committed):
AuthSurfaceVerifier.Compare(report, "authsurface.json").AssertValid();
```

The package README is the canonical consumer guide for updates, classifications, limitations, and privacy: [`src/KeelMatrix.AuthSurface/README.md`](src/KeelMatrix.AuthSurface/README.md).

## Requirement fingerprint

`AuthSurfaceEndpoint.RequirementFingerprint` is the SHA-256 fingerprint of only the ordered canonical `Requirements` sequence. Requirement order and framework-preserved duplicates are identity-significant. Classification, named policies, roles, authentication schemes, and default/fallback provenance have their own fields and comparison codes; changing only one of those values does not emit `endpoint-requirement-changed`.

## Strict fallback enforcement

`AuthSurfaceScanOptions.StrictFallbackPolicy` rejects endpoints whose effective authorization policy includes fallback-policy contribution. This includes an explicitly protected endpoint when its requirement data or other explicit metadata is combined with fallback. The `fallback-policy-endpoint` diagnostic means fallback contributed to the effective policy; it does not mean the endpoint lacked endpoint-level metadata. Supply a complete endpoint/default/named/direct policy path that prevents fallback from contributing, or disable strict fallback enforcement intentionally.

Endpoint identity follows the bounded canonical-representation contract in [`docs/endpoint-identity.md`](docs/endpoint-identity.md). It normalizes only the listed framework equivalences and does not claim general routing semantic equivalence.

## Diagnostic codes

The following table is the complete stable code set emitted by the shipping assembly through structured violations and analysis or baseline exceptions. Message text may add context; automation should use the code.

<!-- BEGIN:DIAGNOSTIC-CODES -->
| Code | Scope |
| --- | --- |
| `baseline-duplicate-field` | Baseline validation |
| `baseline-duplicate-identity` | Baseline validation |
| `baseline-endpoint-limit` | Baseline validation |
| `baseline-field-type` | Baseline validation |
| `baseline-malformed` | Baseline validation |
| `baseline-read-failed` | Baseline I/O |
| `baseline-schema-unsupported` | Baseline compatibility |
| `baseline-too-deep` | Baseline bounds |
| `baseline-too-large` | Baseline bounds |
| `baseline-truncated` | Baseline validation |
| `baseline-unknown-endpoint-field` | Baseline validation |
| `baseline-unknown-field` | Baseline validation |
| `duplicate-endpoint-identity` | Endpoint analysis |
| `endpoint-added` | Baseline comparison |
| `endpoint-classification-changed` | Baseline comparison |
| `endpoint-default-policy-changed` | Baseline comparison |
| `endpoint-fallback-policy-changed` | Baseline comparison |
| `endpoint-policy-changed` | Baseline comparison |
| `endpoint-removed` | Baseline comparison |
| `endpoint-requirement-changed` | Baseline comparison |
| `endpoint-role-changed` | Baseline comparison |
| `endpoint-route-changed` | Baseline comparison |
| `endpoint-scheme-changed` | Baseline comparison |
| `fallback-policy-endpoint` | Policy verification |
| `policy-resolution-failed` | Endpoint analysis |
| `unprotected-endpoint` | Policy verification |
| `unsupported-parameter-policy` | Endpoint analysis |
<!-- END:DIAGNOSTIC-CODES -->

See the [API reference](docs/api-reference.md) for the emission boundary and the package README for troubleshooting guidance.

## Limitations

AuthSurface targets `net8.0` only. It records runtime authorization metadata and policy requirements; it does not execute handlers or prove business-level authorization semantics. The local `authsurface.json` baseline is reviewable source-controlled state and may reveal internal application architecture, so protect and review it accordingly.

## Documentation

- [API reference](docs/api-reference.md)
- [Endpoint identity](docs/endpoint-identity.md)
- [Privacy](PRIVACY.md)
- [Developer guide](AGENTS.md)

## Troubleshooting

For empty endpoint sets, policy-resolution errors, duplicate endpoint identity, and unsupported baseline versions, see the package README's [diagnostic troubleshooting](src/KeelMatrix.AuthSurface/README.md#troubleshooting) section.

## License

AuthSurface is licensed under the [MIT License](LICENSE).
