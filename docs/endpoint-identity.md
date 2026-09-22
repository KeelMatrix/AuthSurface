# Endpoint identity

AuthSurface freezes multi-method representation as method-specific records. A route endpoint with methods `POST, GET` becomes two records: one for the normalized route plus `GET`, and one for the normalized route plus `POST`. Methods are uppercased with invariant culture and sorted ordinally before records are emitted.

The durable identity is `routing-equivalent normalized route pattern + HTTP method`. AuthSurface uppercases only route literal content, parameter names, and the HTTP method for identity purposes. Constraint policy text, constraint arguments, defaults, catch-all encoding, and other semantically significant pattern content remain ordinally distinct. Consequently, `regex(^\\d+$)` and `regex(^\\D+$)` cannot collapse to one identity. The readable route preserves its normalized source casing. Display names, controller/action names, source locations, endpoint order, and generated display text are never baseline keys. The readable route is normalized to an absolute pattern with one leading slash, collapsed duplicate separators, and no trailing slash except for `/`.

The same renderer handles parsed patterns and programmatically constructed `RoutePattern` instances without `RawText`. An encoded-slash catch-all is rendered with one star (`{*path}`); a non-encoded catch-all is rendered with two (`{**path}`).

Two runtime route endpoints whose routing-equivalent identities match are ambiguous. Scanning fails with the structured `duplicate-endpoint-identity` analysis error rather than silently selecting one endpoint. Endpoint exclusions are explicit and happen before identity validation. Routes that remain different after this rule, such as `/alpha` and `/beta`, retain separate records.
