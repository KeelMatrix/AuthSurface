# Package discovery metadata

The root [`keelmatrix.website.json`](../keelmatrix.website.json) is the canonical
classification contract. Schema version 1 maps each exact packable package ID
to one visibility (`public-product` or `internal-package`) and one artifact role
(`primary` or `component`). The public product family has exactly one primary.
Tests and fixture applications are not packable and have no manifest entries.

Each packable project's evaluated Release `PackageTags` must carry exactly one
matching lowercase visibility sentinel and role sentinel. Descriptive search
tags can coexist with these sentinels. `PackageProjectUrl` and `RepositoryUrl`
must both identify `https://github.com/KeelMatrix/AuthSurface`, with repository
type `git`.

Run the source check and its regression tests from the repository root:

```powershell
pwsh -NoProfile -File ./scripts/check-website-metadata.ps1
pwsh -NoProfile -File ./scripts/test-website-metadata.ps1
```

The checker uses evaluated MSBuild properties, including inherited and
conditional values, without restoring or building. It rejects missing,
malformed, unsupported, duplicate, or unknown manifest fields; missing, stale,
or foreign identities; absent, repeated, conflicting, or mismatched sentinels;
invalid primary counts; and incorrect source repository URLs.

`scripts/validate.ps1` runs the source check and regression tests before restore.
The existing Windows, Linux, and macOS CI jobs run that full gate and propagate
failures. `scripts/inspect-package.ps1` uses the same checker for the actual
`.nupkg` and `.snupkg` nuspecs, including identity, classification, project URL,
and repository metadata. The tag-push publication job also invokes that
inspector before publication.

The inspector also requires the matching symbol archive and an explicit expected
source commit. Its separate [source-provenance contract](maintaining-release-workflow.md#packaged-source-provenance)
checks the packaged DLL/PDB and every source document. Passing metadata alone
does not establish functional debugging sources.

For focused archive inspection, pass the built artifacts to the checker:

```powershell
./scripts/check-website-metadata.ps1 -PackagePath @(
    './artifacts/package/KeelMatrix.AuthSurface.0.1.1.nupkg',
    './artifacts/package/KeelMatrix.AuthSurface.0.1.1.snupkg'
)
```

The regression fixtures exercise valid inherited source metadata and both
archive types, then mutate schema shapes, identities, classifications, tags,
primary counts, and URLs. Child-process controls require exit 1 from the direct
checker, package inspector, and full validation entry point. A successful return
or reaching the defensive restore sentinel fails the test.
The fixtures also place a forwarding executable on `PATH` alongside the installed
`dotnet`. Child wrappers select the first application, preserving ordinary command
resolution when several executable paths match.
