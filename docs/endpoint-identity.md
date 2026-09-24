# Endpoint identity

AuthSurface freezes multi-method representation as method-specific records. A route endpoint with methods `POST, GET` becomes two records: one for the normalized route plus `GET`, and one for the normalized route plus `POST`. Methods are uppercased with invariant culture and sorted ordinally before records are emitted.

The durable v1 identity is a deterministic canonical representation of the runtime route pattern plus HTTP method. It is not a proof of general route-matching semantic equivalence. AuthSurface uppercases route literal content, parameter names, and HTTP methods for identity purposes. The readable route preserves its normalized source casing. Display names, controller/action names, source locations, endpoint order, and generated display text are never baseline keys. The readable route is normalized to an absolute pattern with one leading slash, collapsed duplicate separators, and no trailing slash except for `/`.

The canonicalizer deliberately normalizes only bounded framework equivalences: case-insensitive built-in constraint tokens, one-value and equal-range length constraints (`length(3)` equals `length(3,3)`), parsed regex constraints and programmatic regex constraints with equivalent options, HTTP-method constraint casing/order/duplicates, and composite-constraint child order. Constraint arguments, defaults, catch-all encoding, optionality, regex text/options, and other representation details remain identity-significant unless one of those explicit rules applies. Thus `regex(^\\d+$)` and `regex(^\\D+$)` remain different identities, while an inline `int` token and an equivalent `IntRouteConstraint` collapse to the same canonical representation.

The same renderer handles parsed patterns and programmatically constructed `RoutePattern` instances without `RawText`. An encoded-slash catch-all is rendered with one star (`{*path}`); a non-encoded catch-all is rendered with two (`{**path}`).

## Programmatic parameter policies

For a programmatically constructed `RoutePattern` whose parameter-policy reference has no textual `Content`, AuthSurface supports the following `IParameterPolicy` runtime types and canonical identity text. Numeric examples are representative values. Composite child order is normalized; distinct child arguments remain distinct.

| Runtime type | Canonical identity example |
| --- | --- |
| `AlphaRouteConstraint` | `alpha` |
| `BoolRouteConstraint` | `bool` |
| `CompositeRouteConstraint` | `composite(int,min(2))` |
| `DateTimeRouteConstraint` | `datetime` |
| `DecimalRouteConstraint` | `decimal` |
| `DoubleRouteConstraint` | `double` |
| `FileNameRouteConstraint` | `file` |
| `FloatRouteConstraint` | `float` |
| `GuidRouteConstraint` | `guid` |
| `HttpMethodRouteConstraint` | `httpMethod(GET,POST)` |
| `IntRouteConstraint` | `int` |
| `LengthRouteConstraint` | `length(3,12)` |
| `LongRouteConstraint` | `long` |
| `MaxLengthRouteConstraint` | `maxlength(12)` |
| `MaxRouteConstraint` | `max(9)` |
| `MinLengthRouteConstraint` | `minlength(3)` |
| `MinRouteConstraint` | `min(2)` |
| `NonFileNameRouteConstraint` | `nonfile` |
| `OptionalRouteConstraint` | `optional(int)` |
| `RangeRouteConstraint` | `range(2,9)` |
| `RegexRouteConstraint` | `regex(^\d+$;options=0)` |
| `RequiredRouteConstraint` | `required` |

Parsed policy text is canonicalized only by the explicit rules above. A content-less reference, unsupported programmatic policy type, or unsupported member inside a composite policy is not omitted: analysis fails closed with `unsupported-parameter-policy`. Exclude the endpoint explicitly or use a parsed constraint or one of the supported programmatic types when a stable identity is required.

Two runtime route endpoints whose bounded canonical identities match are duplicates. Scanning fails with the structured `duplicate-endpoint-identity` analysis error rather than silently selecting one endpoint. Representation differences outside the explicit normalization rules can keep two routing-equivalent endpoints distinct. Endpoint exclusions are explicit and happen before identity validation. Routes that remain different after this rule, such as `/alpha` and `/beta`, retain separate records.

## Requirement identity

Requirement identity is separate from route/method identity. The ordered canonical `Requirements` sequence is its single source of truth: order and framework-preserved duplicates are significant. `RequirementFingerprint` is the SHA-256 fingerprint of only that sequence. Classification, named policies, roles, authentication schemes, and default/fallback provenance are excluded from the fingerprint and produce only their dedicated comparison diagnostics when requirements remain identical.
