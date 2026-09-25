[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$checker = Join-Path $PSScriptRoot 'check-repository-hygiene.ps1'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-hygiene-' + [guid]::NewGuid().ToString('N'))

function Invoke-TempGit([string[]] $Arguments) {
    & git -C $tempRoot @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Temporary git command failed: git -C $tempRoot $($Arguments -join ' ')"
    }
}

function Assert-HygieneFails {
    param([string] $Reason)
    $failed = $false
    try {
        & $checker -Root $tempRoot | Out-Null
    }
    catch {
        $failed = $true
    }

    if (-not $failed) {
        throw "Repository hygiene negative control did not fail: $Reason"
    }
}

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    Invoke-TempGit @('init', '--quiet')
    Invoke-TempGit @('config', 'user.name', 'KeelMatrix')
    Invoke-TempGit @('config', 'user.email', 'keelmatrix@gmail.com')
    Set-Content -LiteralPath (Join-Path $tempRoot 'README.md') -Value 'A normal review note is allowed.' -Encoding utf8
    Invoke-TempGit @('add', '--', 'README.md')
    Invoke-TempGit @('commit', '--quiet', '-m', 'Add documentation', '-m', 'A normal review note remains developer-facing.')
    & $checker -Root $tempRoot | Out-Null

    function Restore-CanonicalLatestCommit {
        Invoke-TempGit @('config', 'user.name', 'KeelMatrix')
        Invoke-TempGit @('config', 'user.email', 'keelmatrix@gmail.com')
        Invoke-TempGit @('commit', '--quiet', '--amend', '--author', 'KeelMatrix <keelmatrix@gmail.com>', '-m', 'Canonicalize test identity')
    }

    function Assert-IdentityFails {
        param([string] $Reason)
        Assert-HygieneFails $Reason
        Restore-CanonicalLatestCommit
        & $checker -Root $tempRoot | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $tempRoot 'author-name.txt') -Value 'identity control' -Encoding utf8
    Invoke-TempGit @('add', '--', 'author-name.txt')
    Invoke-TempGit @('commit', '--quiet', '--author', 'Wrong Author <keelmatrix@gmail.com>', '-m', 'Author name control')
    Assert-IdentityFails 'wrong author name'

    Set-Content -LiteralPath (Join-Path $tempRoot 'author-email.txt') -Value 'identity control' -Encoding utf8
    Invoke-TempGit @('add', '--', 'author-email.txt')
    Invoke-TempGit @('commit', '--quiet', '--author', 'KeelMatrix <wrong-author@example.invalid>', '-m', 'Author email control')
    Assert-IdentityFails 'wrong author email'

    Set-Content -LiteralPath (Join-Path $tempRoot 'committer-name.txt') -Value 'identity control' -Encoding utf8
    Invoke-TempGit @('add', '--', 'committer-name.txt')
    Invoke-TempGit @('config', 'user.name', 'Wrong Committer')
    Invoke-TempGit @('commit', '--quiet', '-m', 'Committer name control')
    Assert-IdentityFails 'wrong committer name'

    Set-Content -LiteralPath (Join-Path $tempRoot 'committer-email.txt') -Value 'identity control' -Encoding utf8
    Invoke-TempGit @('add', '--', 'committer-email.txt')
    Invoke-TempGit @('config', 'user.email', 'wrong-committer@example.invalid')
    Invoke-TempGit @('commit', '--quiet', '-m', 'Committer email control')
    Assert-IdentityFails 'wrong committer email'

    Set-Content -LiteralPath (Join-Path $tempRoot 'trailer.txt') -Value 'identity control' -Encoding utf8
    Invoke-TempGit @('add', '--', 'trailer.txt')
    Invoke-TempGit @('commit', '--quiet', '-m', 'Trailer control', '-m', 'Co-authored-by: Other <other@example.invalid>')
    Assert-HygieneFails 'unapproved co-authored-by trailer'
    Restore-CanonicalLatestCommit
    & $checker -Root $tempRoot | Out-Null

    $internalPath = 'internal-only-task-' + 'delegator-notes.md'
    Set-Content -LiteralPath (Join-Path $tempRoot $internalPath) -Value 'private note' -Encoding utf8
    Invoke-TempGit @('add', '--', $internalPath)
    Invoke-TempGit @('commit', '--quiet', '-m', 'Add note')
    Assert-HygieneFails 'internal-only tracked filename'

    Remove-Item -LiteralPath (Join-Path $tempRoot $internalPath) -Force
    Invoke-TempGit @('rm', '--quiet', '--', $internalPath)
    Set-Content -LiteralPath (Join-Path $tempRoot 'body.txt') -Value 'developer-facing body' -Encoding utf8
    Invoke-TempGit @('add', '--', 'body.txt')
    $bodyPhrase = 'internal ' + 'orches' + 'tration detail'
    Invoke-TempGit @('commit', '--quiet', '-m', 'Add body control', '-m', $bodyPhrase)
    Assert-HygieneFails 'prohibited wording in a complete commit body'

    Write-Output 'Repository hygiene controls passed: canonical history identities, unapproved trailers, tracked internal-only paths, and prohibited commit-body wording are rejected.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
