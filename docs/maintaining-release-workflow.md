# Maintaining the release workflow

The release workflow is validated without network access. Every `uses:` entry in
`.github/workflows/release.yml` must have one exact matching entry in
`scripts/release-action-pins.txt`, and the allowlist may not contain unused or
duplicate entries.

To update an action pin, resolve the requested tag and verify the resulting
object type before changing either file. For an annotated tag, inspect the tag
object and then resolve its `object.sha` through the commits endpoint:

```text
gh api repos/OWNER/REPOSITORY/git/tags/TAG_OBJECT_SHA --jq '{sha: .sha, tag: .tag, object: .object}'
gh api repos/OWNER/REPOSITORY/git/commits/COMMIT_SHA --jq '{sha: .sha, url: .url}'
```

The commits endpoint must succeed for the SHA recorded in the allowlist. The
validator intentionally does not contact GitHub; the command output is the
maintainer's record that each allowlist entry was verified as a commit object.

Current entries were verified as follows on 2026-09-21:

```text
gh api repos/NuGet/login/git/tags/ebc737b6fc418a6ca0073cf116ec8dc156d8b81e --jq '{sha: .sha, tag: .tag, object: .object}'
{"sha":"ebc737b6fc418a6ca0073cf116ec8dc156d8b81e","tag":"v1","object":{"sha":"8d196754b4036150537f80ac539e15c2f1028841","type":"commit","url":"https://api.github.com/repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841"}}

gh api repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841 --jq '{sha: .sha, url: .url}'
{"sha":"8d196754b4036150537f80ac539e15c2f1028841","url":"https://api.github.com/repos/NuGet/login/git/commits/8d196754b4036150537f80ac539e15c2f1028841"}

gh api repos/actions/checkout/git/commits/11bd71901bbe5b1630ceea73d27597364c9af683 --jq '{sha: .sha, url: .url}'
{"sha":"11bd71901bbe5b1630ceea73d27597364c9af683","url":"https://api.github.com/repos/actions/checkout/git/commits/11bd71901bbe5b1630ceea73d27597364c9af683"}

gh api repos/actions/setup-dotnet/git/commits/67a3573c9a986a3f9c594539f4ab511d57bb3ce9 --jq '{sha: .sha, url: .url}'
{"sha":"67a3573c9a986a3f9c594539f4ab511d57bb3ce9","url":"https://api.github.com/repos/actions/setup-dotnet/git/commits/67a3573c9a986a3f9c594539f4ab511d57bb3ce9"}
```
