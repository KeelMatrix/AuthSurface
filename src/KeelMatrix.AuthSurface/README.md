# KeelMatrix.AuthSurface

**AuthSurface fails your tests when the authorization surface of an ASP.NET Core app changes unexpectedly.** It discovers runtime endpoints, resolves their effective authorization metadata, and compares the result with a reviewable baseline.

## Install

```powershell
dotnet add package KeelMatrix.AuthSurface --version 0.1.0
```

## Quick start

Build and start the host before scanning so endpoint data sources and authorization metadata are complete:

```csharp
using KeelMatrix.AuthSurface;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

await app.StartAsync();
var scanner = new AuthSurfaceScanner(
    app.Services.GetServices<EndpointDataSource>(),
    app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
AuthSurfaceReport report = await scanner.ScanAsync();
report.AssertPolicyCompliant();
```

### One-time baseline creation

After reviewing the first report, create and commit the baseline exactly once:

```csharp
AuthSurfaceBaseline.Create(report, "authsurface.json", overwrite: false);
```

### Recurring comparison test

The recurring test reads the accepted baseline and never creates or rewrites it:

```csharp
AuthSurfaceVerifier.Compare(report, "authsurface.json").AssertValid();
```

The complete public surface is summarized in the repository's [API reference](https://github.com/KeelMatrix/AuthSurface/blob/main/docs/api-reference.md).

## What the scan records

AuthSurface emits one deterministic record for each normalized route and HTTP method contract. An endpoint accepting `GET` and `POST` produces one record per method. Each record contains the route pattern, method, one of the four classifications, named policies, roles, authentication schemes, default/fallback contribution flags, canonical supported requirements, and a stable SHA-256 fingerprint of only the ordered canonical requirements sequence.

The four classifications are:

- `ExplicitAnonymous`: runtime metadata contains `IAllowAnonymous`; the intentional public endpoint remains visible.
- `ExplicitProtected`: endpoint authorization metadata, an explicit `AuthorizationPolicy`, or non-empty requirement data contributes protection. If requirement-data metadata exists but produces no requirements, policy construction fails closed because that is the framework middleware behavior.
- `FallbackProtected`: no endpoint-specific protecting contribution exists, but the application's fallback policy protects it.
- `Unprotected`: no explicit anonymous metadata and no effective protecting policy exist; this fails the default policy check.

`[Authorize]`, `[AllowAnonymous]`, endpoint and route-group `.RequireAuthorization()`, named policies, roles, schemes, default/fallback policy, and dynamic policy providers are evaluated from runtime endpoint metadata and the application's real `IAuthorizationPolicyProvider`. Unknown requirements are represented only by a stable type identity and an explicit `opaque` marker; arbitrary custom requirement object graphs are never reflected. The `requirements` list preserves the order produced by the framework as it combines `IAuthorizeData`-derived requirements, explicit `AuthorizationPolicy` requirements, and `IAuthorizationRequirementData`; it does not sort or silently deduplicate that sequence. Framework-preserved duplicate requirements remain in the list.

## Baselines, identity, and diagnostics

`authsurface.json` is bounded, UTF-8, schema-versioned JSON with deterministic endpoint ordering and `\n` newlines. Requirement arrays retain framework combination order, including framework-preserved duplicates, and requirement order is identity-significant: reversing requirements changes the canonical text, requirement-only fingerprint, and baseline comparison result. Classification, named policies, roles, authentication schemes, and default/fallback provenance are excluded from `requirementFingerprint` and use their dedicated comparison codes. Schema version 1 accepts one leading UTF-8 BOM when the document is otherwise valid; a raw BOM inside the document is invalid, while an escaped `\uFEFF` sequence inside a JSON string is treated as ordinary string content. The writer always emits BOM-free UTF-8. Endpoint records may include a bounded `v1:` identity token for structural route representations; its length-delimited binding records route parts, parameter flags/defaults, policy provenance/content, and method. The reader reconstructs each binding through the same `RoutePatternFactory` and supported generated-policy registry used by the writer, then re-extracts and re-encodes it with the exact writer encoder. The complete token must match byte-for-byte across both base64url layers, minimal length encodings, and structural entries, and the writer's canonical renderer must reproduce the exact display and structural routes. A factory or registry rejection, non-writer-emittable field combination, or byte mismatch is `baseline-malformed`. Zero-width literal/separator entries and opaque delimiters or regex syntax are never accepted or classified by a heuristic. Older version-1 records without the optional identity field remain readable; ambiguous legacy identity tokens fail closed and require explicit regeneration. The reader validates identity syntax and HTTP-method binding, rejects canonical duplicate route/method records even when their supplied keys differ, preserves the stored requirement order, and rejects a fingerprint that does not match that sequence. It rejects schema errors with structured `AuthSurfaceBaselineException` diagnostics and never silently rewrites a file. Duplicate and unknown property names are rendered in diagnostics at no more than 128 characters; longer names end with `…(truncated)` to keep diagnostic text bounded.

Framework requirement canonicalization uses an exact supported-runtime-type policy. Derived or custom requirements retain stable type identity with an explicit `opaque` marker, and arbitrary custom state is never reflected. Route processing is bounded per route to a 16,384-character route, 8,192-character policy expression, depth 32, and 100,000 policy-work operations. Each scan also applies cumulative bounds to input endpoints, emitted route/method records, route-rendering/policy-expansion work, effective requirements, nested authorization values (including roles, claims, schemes, and policy method lists), authorization metadata items, requirement-data requirements, metadata strings, and canonical requirement text. The cumulative route-work and authorization-metadata limits are 100,000 units each; requirement-data items are charged before materialization and expansion is bounded to 8,192 requirements per scan. Countable sequences over the per-endpoint requirement-data ceiling fail before enumeration. These bounds fail closed with structured diagnostics. Scanner-owned cancellation checks occur between callback yields but cannot interrupt arbitrary application callback code.

## Troubleshooting

This table is the complete stable code set emitted by the shipping assembly. `AuthSurfaceViolation.Code`, `AuthSurfaceAnalysisException.Code`, and `AuthSurfaceBaselineException.Code` all use this inventory.

<!-- BEGIN:DIAGNOSTIC-CODES -->
| Code | Meaning | What to do |
| --- | --- | --- |
| `baseline-duplicate-field` | A property name appears more than once in one JSON object. The message names the property and identifies the top level or the endpoint entry (`$.endpoints[index]`). | Remove the duplicate property and keep the value that matches the intended schema. |
| `baseline-duplicate-identity` | Two endpoint records normalize to the same route and HTTP method identity. | Remove the duplicate record or correct its route/method before regenerating the baseline. |
| `baseline-endpoint-limit` | The baseline endpoint list is missing or exceeds the supported 100,000-record bound. | Reduce the baseline to the intended endpoint set and regenerate it from a bounded scan. |
| `baseline-field-type` | A known property has the wrong JSON type, such as a string where an array or boolean is required. | Change the value to the type required by schema version 1. |
| `baseline-malformed` | The file is empty, whitespace-only, has a syntax error, contains an invalid BOM placement or mismatched requirement fingerprint, or is otherwise not valid JSON for schema version 1. | Fix the JSON syntax or regenerate the baseline; a malformed file is never rewritten. |
| `baseline-read-failed` | The baseline could not be opened or read because of an I/O or access failure. | Check that the path exists and that the process has permission to read the file, then retry. |
| `baseline-schema-unsupported` | The file declares a schema version other than the supported version 1. | Migrate the file explicitly to schema version 1; AuthSurface does not downgrade it automatically. |
| `baseline-too-deep` | A complete JSON document exceeds the supported nesting depth. This is distinct from `baseline-truncated`, which means the document ends before its structure is complete. | Remove unexpected nesting and regenerate the baseline if the content is intentional. |
| `baseline-too-large` | The file exceeds the default 1 MiB input limit (or the limit supplied to `Read`). | Reduce the baseline size or provide an intentional, bounded maximum appropriate for the application. |
| `baseline-truncated` | The document ends in the middle of a JSON structure, such as an object, array, string, or escape sequence. | Restore the missing bytes or regenerate the baseline from a trusted scan. |
| `baseline-unknown-endpoint-field` | An unrecognized property appears in an endpoint entry. | Remove the property or migrate the file to the supported schema. |
| `baseline-unknown-field` | An unrecognized property appears at the top level. | Remove the property or migrate the file to the supported schema. |
| `duplicate-endpoint-identity` | Two runtime endpoints normalize to the same route and HTTP method identity. | Disambiguate the routes or explicitly exclude the intended infrastructure endpoint. |
| `endpoint-added` | A current endpoint has no matching baseline entry. | Review the endpoint, then deliberately update the accepted baseline if the addition is intended. |
| `endpoint-classification-changed` | An endpoint changed among the four authorization classifications. | Review its runtime authorization metadata and accept only an intentional posture change. |
| `endpoint-default-policy-changed` | The default-policy contribution flag changed. | Review the endpoint metadata and application default policy. |
| `endpoint-fallback-policy-changed` | The fallback-policy contribution flag changed. | Review the application fallback policy and the endpoint's explicit metadata. |
| `endpoint-limit` | The scan exceeds the supported endpoint count. | Reduce the endpoint set or explicitly filter infrastructure endpoints. |
| `endpoint-method-changed` | The complete HTTP method contract changed. | Review the route method metadata and update the baseline only when intentional. |
| `endpoint-policy-changed` | The exact named-policy sequence changed. | Review the named policies and dynamic policy-provider result. |
| `endpoint-removed` | A baseline endpoint has no matching current endpoint. | Confirm the route was intentionally removed, then update the baseline. |
| `endpoint-requirement-changed` | The ordered canonical effective requirements changed. | Review the effective authorization-policy requirements before accepting the new baseline. |
| `endpoint-role-changed` | The canonical role set changed. | Review role metadata and policy requirements before accepting the change. |
| `endpoint-route-changed` | The readable normalized route changed for the same canonical identity. | Review the route spelling/casing and update the baseline only when intentional. |
| `endpoint-scheme-changed` | The canonical authentication-scheme set changed. | Review endpoint and policy scheme metadata before accepting the change. |
| `fallback-policy-endpoint` | Fallback contributed to the effective authorization policy while strict mode was enabled. | Supply a complete endpoint/default/named/direct policy path that prevents fallback from contributing, or disable strict fallback enforcement intentionally. |
| `metadata-limit` | An endpoint exceeds the supported authorization metadata or requirement-data count. | Reduce generated metadata or split the endpoint configuration. |
| `policy-resolution-failed` | The application's policy provider could not resolve the endpoint's authorization metadata. | Register a resolvable policy provider and scan the completed host again. |
| `route-pattern-too-large` | A route pattern exceeds the supported character bound. | Shorten or explicitly exclude the route. |
| `route-policy-too-complex` | A route policy exceeds its per-route length/work bound, or the scan exceeds its cumulative route-rendering/policy-expansion work bound. | Simplify or explicitly exclude the route, reduce method/policy expansion, or reduce the scanned surface. |
| `route-policy-too-deep` | A route policy exceeds the supported nesting-depth bound. | Simplify or explicitly exclude the route. |
| `resource-limit` | Scanner-owned endpoint, method, effective-policy, nested-value, authorization-metadata, requirement-data, or canonicalization work exceeds a bounded limit. | Reduce the analyzed surface, method/policy expansion, or explicitly filter infrastructure endpoints. |
| `unprotected-endpoint` | An endpoint is neither explicitly anonymous nor protected by an effective policy. | Add authorization, mark it explicitly anonymous, or explicitly exclude it. |
| `unsupported-parameter-policy` | A programmatic route parameter policy has no stable representation, or a generated/content-less policy is attached to a non-encoded catch-all during baseline creation. | Use a parsed route constraint, an encoded catch-all, a textual/content-bearing policy, or explicitly exclude the endpoint. |
<!-- END:DIAGNOSTIC-CODES -->

For baseline diagnostics, inspect the `Code` and message on `AuthSurfaceBaselineException`; the original file remains byte-for-byte unchanged on failure. Analysis failures use `AuthSurfaceAnalysisException`, while policy and comparison findings use `AuthSurfaceViolation`.

An endpoint entry requires `route`, exactly one `methods` value, one of the four exact authorization names, `policies`, `roles`, `schemes`, explicit JSON booleans `usesDefaultPolicy` and `usesFallbackPolicy`, `requirements`, and a 64-character hexadecimal `requirementFingerprint` equal to the SHA-256 fingerprint of only that ordered requirements sequence. Missing, `null`, or wrong-type required fields fail as `baseline-field-type`; absent booleans are never synthesized as `false`. Schema version 1 is strict about unknown fields, classification names, and fingerprint consistency; future schema versions require an explicit migration rather than silent reinterpretation. The writer and default reader share a 1,048,576-byte UTF-8 document bound. `Write` validates the complete serialized document before creating a destination directory or file, so rejected writes have no destination side effect; `Read(path, maximumBytes)` may impose a smaller explicit limit. Under SemVer, a future incompatible baseline representation requires a new schema version and documented migration behavior.

If the scan finds no endpoints, verify that the host was started and that endpoint data sources were resolved after route mapping; do not create an empty baseline until that is intentional. A `policy-resolution-failed` result means the application's real policy provider could not resolve a named policy, so register the provider and scan the completed host again. A `duplicate-endpoint-identity` result means two runtime endpoints normalize to one route/method identity; disambiguate or explicitly exclude the infrastructure endpoint before creating a baseline.

Endpoint identity is a deterministic canonical representation of the runtime route pattern plus HTTP method. It is not a proof of general route-matching semantic equivalence. Every content-bearing parameter policy is textual, including values that begin with the exact-lowercase `programmatic:` marker; only supported content-less runtime policies use the `programmatic:` provenance marker. Thus textual and programmatic policies retain distinct identity provenance even when their readable route display is the same. Built-in textual constraint-token casing, `length(3)` versus `length(3,3)`, HTTP-method constraint casing/order/duplicates, and composite child order are normalized. Inline regex commas and literal `;options=` remain pattern text. Supported programmatic regex uses `regex64(<base64url-no-padding(UTF-8 pattern)>;options=521)` with the framework inline defaults; malformed base64url, UTF-8, or option suffixes fail closed with `unsupported-parameter-policy`. The encoded payload cannot contain nested-policy delimiters, so terminator-like text, groups, character classes, escapes, braces, slashes, commas, and Unicode remain opaque through nested policies. Other representation details, including constraint arguments, defaults, catch-all encoding, optionality, regex text/options, and unsupported policy forms, remain identity-significant or fail closed. The readable record may retain original casing. For an unparseable or provenance-ambiguous display, the persisted `v1:` token contains a length-delimited route-pattern binding. On read, the binding is reconstructed through the same `RoutePatternFactory` and supported generated-policy registry used by the writer, then re-extracted and re-encoded with the exact writer encoder; the bytes must match across both base64url layers, minimal length encodings, and structural entries. The writer's canonical renderer must also reproduce both the display route and canonical structural route, requiring exact matches for route and method. Factory/registry rejection, non-writer-emittable flags/defaults/parts, malformed, stale, unsupported, or ambiguous tokens fail as `baseline-malformed` without rewriting the file. Zero-width literal/separator entries and marker-like payloads are opaque and are never accepted or reclassified by a boundary heuristic. Equivalent bounded identities fail with `duplicate-endpoint-identity`, while representations outside the explicit normalization rules may remain distinct. Programmatically constructed patterns without `RawText` use explicit provenance markers for catch-alls and supported parameter-policy objects; an unrepresentable parameter policy fails with `unsupported-parameter-policy` instead of being omitted. Encoded-slash catch-alls render as `{*name}` and non-encoded catch-alls as `{**name}`.

Baseline creation rejects a non-encoded catch-all combined with any generated/content-less parameter policy before creating a directory or file. It throws `unsupported-parameter-policy` with the route and HTTP method, and directs callers to use an encoded catch-all, a textual/content-bearing policy, or an explicit exclusion. Plain and textual/content-bearing non-encoded catch-alls remain supported; the reader still rejects forged or non-writer-emittable tokens as `baseline-malformed` without changing input bytes.

Baseline comparison reports structured additions, removals, changes, and policy violations. Routes and methods appear only in local diagnostics; source locations, claims, tokens, request bodies, and user data are never collected. Use `AuthSurfaceScanOptions.ExcludedRoutePatterns` for intentional infrastructure exclusions. Strict fallback mode is available through `StrictFallbackPolicy` to reject any endpoint whose effective authorization policy includes fallback-policy contribution, including an explicitly protected endpoint when fallback also contributes. Remediate by supplying a complete endpoint/default/named/direct policy path that prevents fallback from contributing, or disable strict fallback enforcement intentionally.

## What AuthSurface proves

AuthSurface proves that completed runtime endpoint data sources contain the discovered route/method contracts and that the framework's effective policy metadata resolves to the recorded classification and ordered canonical requirements. The requirement fingerprint is derived only from that sequence. It can detect endpoint additions, removals, and authorization metadata changes against a reviewed local baseline.

## Reading structured diffs

The verifier uses stable codes with human-readable messages:

```text
endpoint-added
Endpoint '/internal/export' [GET] was added to the authorization surface.

endpoint-removed
Endpoint '/internal/export' [GET] was removed from the authorization surface.

endpoint-classification-changed
Endpoint '/secure' [GET] authorization classification changed from ExplicitProtected to ExplicitAnonymous.

endpoint-policy-changed
Endpoint '/secure' [GET] named policies changed from ["Read"] to ["Write"].

endpoint-requirement-changed
Endpoint '/secure' [GET] effective requirements changed from requirements=["requirement"]; fingerprint=79a5e127308a5a9cf7b4b805b8c675f2b6a8de8f55652be79846d74d5b6a892a to requirements=["changed"]; fingerprint=14ad2100f312362285a276d6d800d93c25a379f6e1fc6da5b34484a965c07f53.
```

## What AuthSurface does not prove

AuthSurface does not execute authorization handlers, create users or claims, mint tokens, contact identity providers, test request behavior, validate OpenAPI completeness, or prove route business behavior. A green result does **not** prove custom authorization handlers implement correct business rules.

## Limitations, privacy, and compatibility

The package targets `net8.0` and uses the `Microsoft.AspNetCore.App` framework reference. It does not require `WebApplicationFactory`, a test framework, a hosted service, middleware, a database, or a network service. Scan after the host has built its endpoint data sources. Dynamic policy resolution errors mean the application's provider could not resolve a named policy. Unsupported baseline versions require explicit migration; AuthSurface will not downgrade or regenerate them.

AuthSurface sends only the shared telemetry contract's bounded activation fields when telemetry is enabled, including pseudonymous `project_hash` and `installation_hash`; routes, URLs, policy names, roles, schemes, requirement types, controller/action names, app/service/repository names, filesystem paths, test names, claims, credentials, baseline contents, and diagnostics stay local. Disable telemetry with `KEELMATRIX_NO_TELEMETRY=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, or `DO_NOT_TRACK=1`. The baseline itself may reveal internal architecture and should be handled accordingly. See [`PRIVACY.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/PRIVACY.md) and [`docs/endpoint-identity.md`](https://github.com/KeelMatrix/AuthSurface/blob/main/docs/endpoint-identity.md) for the maintained details.
