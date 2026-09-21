# Privacy

AuthSurface keeps endpoint routes, policy names, roles, schemes, requirement identities, baseline contents, diagnostics, and application identity local to the process. They are not telemetry fields.

The package uses the shared `KeelMatrix.Telemetry` client only after a real scan followed by policy or baseline evaluation. The bounded activation mechanism may be disabled with `KEELMATRIX_NO_TELEMETRY=1`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`, or `DO_NOT_TRACK=1`. No recurring heartbeat is requested by AuthSurface.

The `authsurface.json` file itself may reveal internal architecture. Store it with the same care as other security-relevant application metadata.
