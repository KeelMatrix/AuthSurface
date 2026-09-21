# KeelMatrix.AuthSurface

AuthSurface tests the runtime ASP.NET Core authorization surface. It discovers endpoints, resolves effective authorization metadata, and compares the result with a reviewable local baseline.

## Install

```powershell
dotnet add package KeelMatrix.AuthSurface --version 0.1.0
```

## Quick Start

The package README is the canonical consumer guide, including the minimal scan, the explicit baseline creation/update workflow, classifications, limitations, and privacy contract: [`src/KeelMatrix.AuthSurface/README.md`](src/KeelMatrix.AuthSurface/README.md).

## Documentation

- [Endpoint identity](docs/endpoint-identity.md)
- [Privacy](PRIVACY.md)
- [Developer guide](AGENTS.md)

## Troubleshooting

For empty endpoint sets, policy-resolution errors, duplicate endpoint identity, and unsupported baseline versions, see the package README's [diagnostic troubleshooting](src/KeelMatrix.AuthSurface/README.md#troubleshooting) section.

## License

AuthSurface is licensed under the [MIT License](LICENSE).
