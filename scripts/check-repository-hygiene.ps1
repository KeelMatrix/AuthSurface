[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$validatorPath = 'scripts/validate.ps1'
$forbiddenPattern = '(?i)Paper' + 'clip|Cod' + 'ex|KEE-' + '[0-9]+|Fron' + 'tier|acceptance[- ]' + 'delta|orches' + 'trat|Task ' + 'Delegator|frontier ' + 'review|frontier ' + 'regression|review ' + 'findings|review ' + 'gaps|previous ' + 'matrix|false/' + 'incomplete|task[- ]' + 'delegator'
$canonicalName = 'KeelMatrix'
$canonicalEmail = 'keelmatrix@gmail.com'
$dependabotName = 'dependabot[bot]'
$dependabotEmails = @(
    '49699333+dependabot[bot]@users.noreply.github.com',
    'dependabot[bot]@users.noreply.github.com'
)
$githubWebName = 'GitHub'
$githubWebEmail = 'noreply@github.com'

function Invoke-Git([string[]] $Arguments) {
    $output = & git -C $Root @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git -C $Root $($Arguments -join ' ')"
    }

    return $output
}

$trackedFiles = @(Invoke-Git @('ls-files'))
$violations = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in $trackedFiles) {
    $normalizedPath = ([string]$relativePath).Replace('\', '/')
    if ([regex]::IsMatch($normalizedPath, $forbiddenPattern)) {
        $violations.Add("tracked path: $normalizedPath")
    }

    if ($normalizedPath -eq $validatorPath -or $normalizedPath -eq 'scripts/check-repository-hygiene.ps1') {
        continue
    }

    $fullPath = Join-Path $Root ([string]$relativePath)
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $content = Get-Content -LiteralPath $fullPath -Raw
        if ([regex]::IsMatch($content, $forbiddenPattern)) {
            $violations.Add("tracked content: $normalizedPath")
        }
    }
}

$shallow = ([string](Invoke-Git @('rev-parse', '--is-shallow-repository'))).Trim()
if ($shallow -eq 'true') {
    throw 'Repository history is shallow; fetch the complete candidate history before claiming a repository-hygiene pass.'
}

$candidateCommits = @(Invoke-Git @('rev-list', 'HEAD')) | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ }
foreach ($commit in $candidateCommits) {
    $identity = [string](Invoke-Git @('show', '-s', '--format=%an%x09%ae%x09%cn%x09%ce', $commit))
    $identityParts = $identity -split "`t"
    if ($identityParts.Count -ne 4) {
        $violations.Add("commit identity unreadable: $commit")
        continue
    }

    $authorAllowed =
        ($identityParts[0] -eq $canonicalName -and $identityParts[1] -eq $canonicalEmail) -or
        ($identityParts[0] -eq $dependabotName -and $dependabotEmails -contains $identityParts[1])
    $committerAllowed =
        ($identityParts[2] -eq $canonicalName -and $identityParts[3] -eq $canonicalEmail) -or
        ($identityParts[0] -eq $canonicalName -and $identityParts[1] -eq $canonicalEmail -and
            $identityParts[2] -eq $githubWebName -and $identityParts[3] -eq $githubWebEmail) -or
        ($identityParts[2] -eq $dependabotName -and $dependabotEmails -contains $identityParts[3])

    if (-not $authorAllowed) {
        $violations.Add("commit author identity: $commit ($($identityParts[0]) <$($identityParts[1])>)")
    }

    if (-not $committerAllowed) {
        $violations.Add("commit committer identity: $commit ($($identityParts[2]) <$($identityParts[3])>)")
    }

    $message = [string]::Join([Environment]::NewLine, @(Invoke-Git @('show', '-s', '--format=%B', $commit)))
    if ([regex]::IsMatch($message, $forbiddenPattern)) {
        $violations.Add("complete candidate commit message: $commit")
    }

    if ([regex]::IsMatch($message, '(?im)^\s*Co-authored-by\s*:')) {
        $violations.Add("unapproved commit trailer: $commit")
    }
}

if ($violations.Count -gt 0) {
    $violations | Write-Output
    throw 'Repository hygiene failed: tracked paths/content, complete candidate commit messages, or candidate-history author metadata violate the public repository contract.'
}

Write-Output 'Repository hygiene passed: tracked paths/content, complete candidate commit messages, and candidate-history author metadata satisfy the public repository contract.'
