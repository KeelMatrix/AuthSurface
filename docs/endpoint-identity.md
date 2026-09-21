# Endpoint identity

AuthSurface freezes multi-method representation as method-specific records. A route endpoint with methods `POST, GET` becomes two records: one for the normalized route plus `GET`, and one for the normalized route plus `POST`. Methods are uppercased with invariant culture and sorted ordinally before records are emitted.

The durable identity is `normalized route pattern + HTTP method`. Display names, controller/action names, source locations, endpoint order, and generated display text are never baseline keys. The route is normalized to an absolute pattern with one leading slash, collapsed duplicate separators, and no trailing slash except for `/`; parameter names and inline constraints are preserved because they affect routing.

Two runtime route endpoints that normalize to the same route and method are ambiguous. Scanning fails with a structured analysis error rather than silently selecting one endpoint. Endpoint exclusions are explicit and happen before identity validation.
