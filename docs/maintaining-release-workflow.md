# Maintaining the release workflow

## Publish packages and symbols

`.github/workflows/release.yml` owns the release steps. The publication command
lives in `scripts/push-release-package.ps1`, which requires an explicit `.nupkg`,
its sibling `.snupkg`, and the temporary `NUGET_PUSH_TOKEN`. Before uploading it
requires a v3 service index advertising both `PackagePublish/2.0.0` and
`SymbolPackagePublish/4.9.0` with usable endpoints. The production source is
`https://api.nuget.org/v3/index.json`.

One `dotnet nuget push` uploads the primary and then the associated `.snupkg`.
Do not set `--symbol-source` to the raw symbol upload URL: the .NET 8 client
selects legacy `.symbols.nupkg` lookup and can exit successfully without sending
the `.snupkg`. Do not add a second symbol push to a successful paired upload.
Missing files or unsupported sources reject before upload; primary or symbol
rejection fails the script and workflow. A failed push can leave an accepted
primary: preserve the version, tag, and accepted effects, and reconcile provider
state before any separately authorized recovery. Never replay the whole release
or use `--skip-duplicate` to hide a partial publication.

After exact-set/archive inspection, the workflow logs each archive's SHA-256 and
writes `SHA256SUMS`. It retains both archives and that manifest as a GitHub Actions
artifact for 90 days **before** OIDC authentication. Retention failure stops
publication. Save the artifact ID, URL, digest, archive hashes, and expiry with
the execution evidence; download recovery files before expiry. The artifact
container digest differs from each archive's hash. A rebuild is not evidence of
the original archive bytes.

The local protocol regression uses synthetic archives, a loopback HTTP feed, and
the repository-selected .NET client without real credentials or external uploads:

```powershell
dotnet test tests/KeelMatrix.AuthSurface.Tests -c Release --filter FullyQualifiedName~ReleasePublicationTests
```

It covers paired upload counts and bytes, missing archives, v3 discovery versus
raw legacy lookup, invalid/unsupported service indexes, primary rejection,
symbol rejection, duplicate conflicts, and child-process failure propagation.
Complete validation runs these tests on Windows, Linux, and macOS. A successful
workflow or accepted upload alone does not establish public availability or
symbol indexing.

For a symbol-only recovery, use the original retained `.snupkg` where available.
If it is unavailable, rebuild with the original tagged source, SDK, and operating
system (generated compiler inputs can differ across platforms), and compare each
PDB's portable content ID and checksum with the actual public DLL's CodeView and
PDB-checksum debug entries. Also verify the package ID/version and SourceLink
revision. Stop if the public DLL cannot be retrieved or any identity differs.
Only after independent verification and separate publication authorization may
an explicit `.snupkg` be pushed through the v3 source; do not send the primary
again, move the tag, or claim rebuilt archive-byte equivalence.

See [NuGet symbol publication](https://learn.microsoft.com/en-us/nuget/create-packages/symbol-packages-snupkg)
for the v3 requirement and separate symbol validation/indexing.

## Maintain action pins

Every `uses:` entry in every `.yml` or `.yaml` file under `.github/workflows/`
must use a full 40-character commit SHA. Each entry must have one exact matching
entry in `scripts/release-action-pins.txt`, and the allowlist may not contain
unused or duplicate entries. `scripts/validate-release-workflow.ps1` checks all
workflow files and checks each SHA through GitHub's Git commit API to confirm
that it is a commit object rather than an annotated tag object. It reads
`action.yml` (or `action.yaml` only after an actual HTTP 404) through the Contents
API at that same SHA and requires a supported runtime: Node 24 or newer,
composite, or Docker.

A successful REST result must be one non-null, non-array custom object or
dictionary. The validator checks the raw value before function output or member
access can enumerate it. It explicitly excludes arrays and recognizes the actual
custom-object type, because PowerShell's `[pscustomobject]` type test also matches
parser-wrapped arrays and scalars. Arrays of any size or depth, null, and scalars
reject. An internal success/not-found envelope keeps a real
HTTP 404 separate from every successful response; provider properties cannot
select that outcome. Only an `action.yml` HTTP 404 permits one `action.yaml`
request. A malformed successful response, fallback 404, or other error stops
validation without another filename or request. Commit `sha` must be the exact
case-sensitive requested string. Metadata `type`, `encoding`, and `content`
must be strings describing a file with base64 content that decodes as strict UTF-8.

Both API lookups use `GITHUB_TOKEN` when supplied. CI passes its existing
`contents: read` workflow token to the direct pin check and complete validation;
the token is inherited by their nested checks. No additional secret or permission
is needed. GitHub Actions validation rejects a missing token before making an
anonymous request. Local validation can use a token supplied through the
environment, or anonymous public access subject to GitHub's shared IP rate limit.
Never put a token in command arguments, source, or logs.

Requests have a 15-second timeout and do not follow redirects. Provider errors
(including 403/429), timeouts, malformed responses, invalid commit objects,
missing metadata, and deprecated or missing runtimes fail validation. There is
no retry or anonymous fallback after authenticated verification fails. Provider
error details and credentials are excluded from diagnostics.

To update an action pin, resolve the requested tag and verify the resulting
object type before changing either file. For an annotated tag, inspect the tag
object and then resolve its `object.sha` through the commits endpoint:

```text
gh api repos/OWNER/REPOSITORY/git/tags/TAG_OBJECT_SHA --jq '{sha: .sha, tag: .tag, object: .object}'
gh api repos/OWNER/REPOSITORY/git/commits/COMMIT_SHA --jq '{sha: .sha, url: .url}'
```

The commits endpoint must succeed for the SHA recorded in the allowlist. The
validator performs the equivalent object-type check itself, so action pin
validation requires network access to the public action repositories.

Current entries were verified as follows on 2026-09-21:

```text
gh api repos/NuGet/login/git/tags/ebc737b6fc418a6ca0073cf116ec8dc156d8b81e --jq '{sha: .sha, tag: .tag, object: .object}'
{"sha":"ebc737b6fc418a6ca0073cf116ec8dc156d8b81e","tag":"v1","object":{"sha":"8d196754b4036150537f80ac539e15c2f1028841","type":"commit","url":"https://api.github.com/repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841"}}

gh api repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841 --jq '{sha: .sha, url: .url}'
{"sha":"8d196754b4036150537f80ac539e15c2f1028841","url":"https://api.github.com/repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841"}

gh api repos/actions/checkout/git/commits/3d3c42e5aac5ba805825da76410c181273ba90b1 --jq '{sha: .sha, url: .url}'
{"sha":"3d3c42e5aac5ba805825da76410c181273ba90b1","url":"https://api.github.com/repos/actions/checkout/git/commits/3d3c42e5aac5ba805825da76410c181273ba90b1"}

gh api repos/actions/setup-dotnet/git/commits/a98b56852c35b8e3190ac28c8c2271da59106c68 --jq '{sha: .sha, url: .url}'
{"sha":"a98b56852c35b8e3190ac28c8c2271da59106c68","url":"https://api.github.com/repos/actions/setup-dotnet/git/commits/a98b56852c35b8e3190ac28c8c2271da59106c68"}
```

Run the real-workflow check and its negative mutation coverage from the
repository root:

```powershell
pwsh -NoProfile -File .\scripts\validate-release-workflow.ps1
pwsh -NoProfile -File .\scripts\test-release-workflow.ps1
pwsh -NoProfile -File .\scripts\test-action-pin-requests.ps1
```

The mutation check must reject publication bypasses, ignored publication or
retention failures, absent archive evidence, authentication before retention,
an unpinned action, a tag name, an annotated tag
object SHA, a workflow action absent from the allowlist, missing CI token wiring,
and ignored verification failures. The deterministic request tests exercise all
pins and all three response boundaries (commit, `action.yml`, and `action.yaml`)
against preserved JSON parsing in both custom-object and hashtable representations,
including a fixture check that parsed arrays match both `[array]` and the
`[pscustomobject]` shorthand. They exercise null/empty/single/multiple/nested-array
cases, valid objects (including inert provider envelope properties), ordinary
unparsed dictionary success, scalar and required-field failures, and true-404
valid and invalid fallback. They assert reached targets and exact request sequences,
authentication and request bounds, anonymous quota exhaustion, provider errors,
redacted diagnostics, content decoding, and supported/deprecated runtimes.
Fifteen child cases prove exit 1 from direct validation, mutation tests, and
the full validation entry point before controlled restore for HTTP 403, commit
singleton arrays, empty-array and singleton successful `action.yml`, and singleton
fallback. An unexpectedly returning child exits zero; a defensive restore sentinel
exits 86. Both fail the harness, as do missing diagnostics, changed request prefixes,
private output, or a success message on rejection.
The CI workflow runs both the validator and the complete repository validation on
Windows, Linux, and macOS. The release workflow remains tag-triggered only.
