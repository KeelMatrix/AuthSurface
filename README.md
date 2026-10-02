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

Endpoint identity follows the bounded canonical-representation contract in [`docs/endpoint-identity.md`](docs/endpoint-identity.md). It normalizes only the listed framework equivalences and does not claim general routing semantic equivalence. Textual and programmatic parameter policies retain separate provenance markers, including when content begins with the reserved `programmatic:` marker. When display text cannot preserve that provenance, the persisted `v1:` token contains a bounded length-delimited route-pattern binding. The reader reconstructs that binding through the same `RoutePatternFactory` and supported generated-policy registry used by the writer, then re-extracts and re-encodes it with the exact writer encoder; the complete token must match byte-for-byte, including both base64url layers and minimal length encodings. It also re-renders the reconstructed binding with the writer's canonical renderer and requires exact matches for both the stored display route and structural route plus method. If factory or registry reconstruction throws or the emitted binding differs, the token fails as `baseline-malformed`. Zero-width literal/separator parts, delimiters, regex syntax, escapes, defaults, slashes, and marker-like payload text are never accepted or reparsed by a heuristic. Malformed, stale, unsupported, non-canonical, or ambiguous tokens fail closed, and failed reads leave baseline bytes unchanged. Version-1 records without an identity field remain readable; legacy identity tokens are accepted only when their binding is directly provable. Each scan also has cumulative bounds for route/policy expansion, authorization metadata, and requirement-data expansion; the latter charges each item before materialization and caps the scan at 8,192 requirements.

Baseline creation rejects a non-encoded catch-all combined with any generated/content-less parameter policy before creating a directory or file. It throws `unsupported-parameter-policy` with the route and HTTP method, and directs callers to use an encoded catch-all, a textual/content-bearing policy, or an explicit exclusion. Plain and textual/content-bearing non-encoded catch-alls remain supported; the reader still rejects forged or non-writer-emittable tokens as `baseline-malformed` without changing input bytes.

Schema version 1 endpoint records require explicit `usesDefaultPolicy` and `usesFallbackPolicy` JSON booleans; missing, `null`, or wrong-type required fields fail closed rather than acquiring CLR defaults. The writer and default reader share a 1,048,576-byte UTF-8 document bound, and `Write` validates the serialized document before creating a destination directory or file. Programmatic regex policies use `regex64(<base64url-no-padding(UTF-8 pattern)>;options=521)`, so user-controlled regex text cannot be mistaken for nested-policy delimiters; malformed encodings fail closed without rewriting the baseline.

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
| `endpoint-limit` | Endpoint analysis |
| `endpoint-method-changed` | Baseline comparison |
| `endpoint-policy-changed` | Baseline comparison |
| `endpoint-removed` | Baseline comparison |
| `endpoint-requirement-changed` | Baseline comparison |
| `endpoint-role-changed` | Baseline comparison |
| `endpoint-route-changed` | Baseline comparison |
| `endpoint-scheme-changed` | Baseline comparison |
| `fallback-policy-endpoint` | Policy verification |
| `metadata-limit` | Endpoint analysis |
| `policy-resolution-failed` | Endpoint analysis |
| `route-pattern-too-large` | Endpoint analysis |
| `route-policy-too-complex` | Endpoint analysis |
| `route-policy-too-deep` | Endpoint analysis |
| `resource-limit` | Endpoint analysis |
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
