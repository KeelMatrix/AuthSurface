# Privacy

AuthSurface keeps endpoint routes, URLs, policy names, roles, schemes, requirement identities, controller/action names, application/service/repository names, filesystem paths, test names, claims, credentials, baseline contents, and diagnostics local to the process. None of those values is included in telemetry.

The package uses the shared `KeelMatrix.Telemetry` client only after a real scan followed by policy or baseline evaluation. When enabled, the shared client serializes its bounded activation payload with `event`, `tool`, `tool_version`, `telemetry_version`, `schema_version`, `project_hash`, `installation_hash`, `runtime`, `os`, `ci`, and `timestamp`. `project_hash` is an established pseudonymous identifier derived by the shared client from solution/project/repository context; it is not a raw project, application, service, or repository name. `installation_hash` is a pseudonymous installation identifier. AuthSurface does not add product or security data to this payload.

The bounded activation mechanism may be disabled with `KEELMATRIX_NO_TELEMETRY=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, or `DO_NOT_TRACK=1`. No recurring heartbeat is requested by AuthSurface. Telemetry delivery is best-effort and cannot change scan or comparison results. The actual serialized payload and fresh-process opt-out behavior are covered by the telemetry contract tests.

The `authsurface.json` file itself may reveal internal architecture. Store it with the same care as other security-relevant application metadata.
