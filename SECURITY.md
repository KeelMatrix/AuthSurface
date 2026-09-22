# Security Policy

## Reporting a Vulnerability

Report suspected vulnerabilities privately before public disclosure. Do not open a public issue or disclose vulnerability details in another public channel:

1. Open this repository's [Security tab](https://github.com/KeelMatrix/AuthSurface/security/advisories/new) and select **Report a vulnerability** to create a private GitHub Security Advisory.
2. Email **keelmatrix@gmail.com** with the affected AuthSurface version, security impact, and sanitized reproduction steps.

Do not include production credentials or other live sensitive data.

AuthSurface inspects in-process ASP.NET Core endpoint metadata. It does not connect to databases, execute authorization handlers, create principals, mint tokens, or send routes and policy data through telemetry. Baseline files can reveal internal application architecture and should be reviewed as source-controlled security-sensitive data.

## Supported Versions

| Version | Supported |
| --- | --- |
| 0.1.x | Yes |
