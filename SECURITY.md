# Security

Please do not report security issues in public issues. Contact the maintainers privately with a concise description, affected version, reproduction details, and a suggested mitigation if available.

AuthSurface inspects in-process ASP.NET Core endpoint metadata. It does not connect to databases, execute authorization handlers, create principals, mint tokens, or send routes and policy data through telemetry. Baseline files can reveal internal application architecture and should be reviewed as source-controlled security-sensitive data.
