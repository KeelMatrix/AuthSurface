# Security Policy

## Reporting a Vulnerability

Please do not report security issues in public issues. Contact the maintainers privately with the affected version, reproduction details, impact, and a suggested mitigation if available.

AuthSurface inspects in-process ASP.NET Core endpoint metadata. It does not connect to databases, execute authorization handlers, create principals, mint tokens, or send routes and policy data through telemetry. Baseline files can reveal internal application architecture and should be reviewed as source-controlled security-sensitive data.

## Supported Versions

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
