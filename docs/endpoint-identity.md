# Endpoint identity

AuthSurface freezes multi-method representation as method-specific records. A route endpoint with methods `POST, GET` becomes two records: one for the normalized route plus `GET`, and one for the normalized route plus `POST`. Methods are uppercased with invariant culture and sorted ordinally before records are emitted.

The durable v1 identity is a deterministic canonical representation of the runtime `RoutePattern` structure plus HTTP method. It is not a proof of general route-matching semantic equivalence. AuthSurface uppercases route literal content, parameter names, and HTTP methods for identity purposes. The readable route preserves its normalized source casing and includes merged defaults and parameter policies. Display names, controller/action names, source locations, endpoint order, and generated display text are never baseline keys. Literal braces are escaped when a readable route is persisted (`{{` and `}}`); this keeps a literal-brace segment distinct from a parameter segment. A structural identity is persisted when display text alone cannot reconstruct programmatic provenance. The readable route is normalized to an absolute pattern with one leading slash, collapsed duplicate separators, and no trailing slash except for `/`.

The canonicalizer deliberately keeps policy provenance at the identity boundary. Textual policy content is represented with a `text:` prefix and a programmatic policy without textual content is represented with a `programmatic:` prefix. AuthSurface therefore does not claim that parsed `int` and `IntRouteConstraint` are equivalent, and it cannot infer the meaning of a textual token when an application remaps it with `RouteOptions.SetParameterPolicy`. Built-in textual tokens are case-insensitive, so `iNt` and `int` have the same bounded textual identity. One-value and equal-range length constraints (`length(3)` equals `length(3,3)`), HTTP-method constraint casing/order/duplicates, and composite-constraint child order are also normalized. Constraint arguments, defaults, catch-all encoding, optionality, regex text/options, and other representation details remain identity-significant unless one of those explicit rules applies.

Regex text is one inline framework argument: commas in a pattern, quantifier/range syntax, and literal `;options=` text remain pattern content. Inline regex content is never rewritten as `RegexOptions.None`. Supported programmatic regex policies must use the framework's inline defaults (`IgnoreCase | CultureInvariant | Compiled`, numeric value `521`); other option combinations fail closed with `unsupported-parameter-policy`. This keeps inline `regex(foo)` distinct from a programmatic regex and keeps a literal pattern containing `;options=` distinct from the explicit programmatic representation.

The same renderer handles parsed patterns and programmatically constructed `RoutePattern` instances without `RawText`; `RawText` is never used as a lossy shortcut. An encoded-slash catch-all is rendered with one star (`{*path}`); a non-encoded catch-all is rendered with two (`{**path}`). A `programmatic:` route-policy marker is an AuthSurface persistence encoding, not application route text; structural identity is retained when a route parser would split that marker, and it is never reparsed as a claim of framework token-map equivalence. Route policy parsing is bounded to a 16,384-character route, 8,192-character policy expression, depth 32, and 100,000 work operations; above-limit input fails closed with a structured diagnostic.

## Programmatic parameter policies

For a programmatically constructed `RoutePattern` whose parameter-policy reference has no textual `Content`, AuthSurface supports the following `IParameterPolicy` runtime types and explicit programmatic identity text. Numeric examples are representative values. Composite child order is normalized; distinct child arguments remain distinct.

| Runtime type | Canonical identity example |
| --- | --- |
| `AlphaRouteConstraint` | `programmatic:alpha` |
| `BoolRouteConstraint` | `programmatic:bool` |
| `CompositeRouteConstraint` | `programmatic:composite(int,min(2))` |
| `DateTimeRouteConstraint` | `programmatic:datetime` |
| `DecimalRouteConstraint` | `programmatic:decimal` |
| `DoubleRouteConstraint` | `programmatic:double` |
| `FileNameRouteConstraint` | `programmatic:file` |
| `FloatRouteConstraint` | `programmatic:float` |
| `GuidRouteConstraint` | `programmatic:guid` |
| `HttpMethodRouteConstraint` | `programmatic:httpMethod(GET,POST)` |
| `IntRouteConstraint` | `programmatic:int` |
| `LengthRouteConstraint` | `programmatic:length(3,12)` |
| `LongRouteConstraint` | `programmatic:long` |
| `MaxLengthRouteConstraint` | `programmatic:maxlength(12)` |
| `MaxRouteConstraint` | `programmatic:max(9)` |
| `MinLengthRouteConstraint` | `programmatic:minlength(3)` |
| `MinRouteConstraint` | `programmatic:min(2)` |
| `NonFileNameRouteConstraint` | `programmatic:nonfile` |
| `OptionalRouteConstraint` | `programmatic:optional(int)` |
| `RangeRouteConstraint` | `programmatic:range(2,9)` |
| `RegexRouteConstraint` | `programmatic:regex(^\d+$;options=521)` |
| `RequiredRouteConstraint` | `programmatic:required` |

Parsed policy text is canonicalized only by the explicit rules above. A content-less reference, unsupported programmatic policy type, or unsupported member inside a composite policy is not omitted: analysis fails closed with `unsupported-parameter-policy`. Exclude the endpoint explicitly or use a parsed constraint or one of the supported programmatic types when a stable identity is required.

Two runtime route endpoints whose bounded canonical identities match are duplicates. Scanning fails with the structured `duplicate-endpoint-identity` analysis error rather than silently selecting one endpoint. Representation differences outside the explicit normalization rules can keep two routing-equivalent endpoints distinct. Endpoint exclusions are explicit and happen before identity validation. Routes that remain different after this rule, such as `/alpha` and `/beta`, retain separate records.

## Requirement identity

Requirement identity is separate from route/method identity. The ordered canonical `Requirements` sequence is its single source of truth: order and framework-preserved duplicates are significant. `RequirementFingerprint` is the SHA-256 fingerprint of only that sequence. Classification, named policies, roles, authentication schemes, and default/fallback provenance are excluded from the fingerprint and produce only their dedicated comparison diagnostics when requirements remain identical.
