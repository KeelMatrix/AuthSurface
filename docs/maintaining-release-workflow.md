# Maintaining workflow action pins

Every `uses:` entry in every `.yml` or `.yaml` file under `.github/workflows/`
must use a full 40-character commit SHA. Each entry must have one exact matching
entry in `scripts/release-action-pins.txt`, and the allowlist may not contain
unused or duplicate entries. `scripts/validate-release-workflow.ps1` checks all
workflow files and fetches each pinned object into a temporary repository to
confirm that it is a commit object rather than an annotated tag object.

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
```

The mutation check must reject an unpinned action, a tag name, an annotated tag
object SHA, and a workflow action that is absent from the allowlist. The CI
workflow runs both the validator and the complete repository validation on
Windows, Linux, and macOS. The release workflow remains tag-triggered only.
