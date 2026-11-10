# Maintaining workflow action pins

Every `uses:` entry in every `.yml` or `.yaml` file under `.github/workflows/`
must use a full 40-character commit SHA. Each entry must have one exact matching
entry in `scripts/release-action-pins.txt`, and the allowlist may not contain
unused or duplicate entries. `scripts/validate-release-workflow.ps1` checks all
workflow files and checks each SHA through GitHub's Git commit API to confirm
that it is a commit object rather than an annotated tag object. It reads
`action.yml` (or `action.yaml` only after a 404) through the Contents API at that
same SHA and requires a supported runtime: Node 24 or newer, composite, or Docker.

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

The mutation check must reject an unpinned action, a tag name, an annotated tag
object SHA, a workflow action absent from the allowlist, missing CI token wiring,
and ignored verification failures. The deterministic request tests exercise all
pins against authenticated success, anonymous quota exhaustion, provider and
response failures, metadata fallback, runtime rejection, and nonzero exits from
direct validation, mutation tests, and the full validation entry point. The CI
workflow runs both the validator and the complete repository validation on
Windows, Linux, and macOS. The release workflow remains tag-triggered only.
