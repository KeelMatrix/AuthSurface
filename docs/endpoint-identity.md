# Endpoint identity

AuthSurface freezes multi-method representation as method-specific records. A route endpoint with methods `POST, GET` becomes two records: one for the normalized route plus `GET`, and one for the normalized route plus `POST`. Methods are uppercased with invariant culture and sorted ordinally before records are emitted.

The durable identity is `routing-equivalent normalized route pattern + HTTP method`. AuthSurface uppercases only route literal content, parameter names, and the HTTP method for identity purposes. Constraint policy text, constraint arguments, defaults, catch-all encoding, and other semantically significant pattern content remain ordinally distinct. Consequently, `regex(^\\d+$)` and `regex(^\\D+$)` cannot collapse to one identity. The readable route preserves its normalized source casing. Display names, controller/action names, source locations, endpoint order, and generated display text are never baseline keys. The readable route is normalized to an absolute pattern with one leading slash, collapsed duplicate separators, and no trailing slash except for `/`.

The same renderer handles parsed patterns and programmatically constructed `RoutePattern` instances without `RawText`. An encoded-slash catch-all is rendered with one star (`{*path}`); a non-encoded catch-all is rendered with two (`{**path}`).

## Programmatic parameter policies

For a programmatically constructed `RoutePattern` whose parameter-policy reference has no textual `Content`, AuthSurface supports the following `IParameterPolicy` runtime types and canonical identity text. Numeric examples are representative values; composite member order and regex options are identity-significant.

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

Parsed policy text remains part of the route identity as supplied by ASP.NET Core. A content-less reference, unsupported programmatic policy type, or unsupported member inside a composite policy is not omitted: analysis fails closed with `unsupported-parameter-policy`. Exclude the endpoint explicitly or use a parsed constraint or one of the supported programmatic types when a stable identity is required.

Two runtime route endpoints whose routing-equivalent identities match are ambiguous. Scanning fails with the structured `duplicate-endpoint-identity` analysis error rather than silently selecting one endpoint. Endpoint exclusions are explicit and happen before identity validation. Routes that remain different after this rule, such as `/alpha` and `/beta`, retain separate records.
