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
    Invoke-TempGit @('config', 'user.email', 'devnull@keelmatrix.invalid')
    Set-Content -LiteralPath (Join-Path $tempRoot 'README.md') -Value 'A normal review note is allowed.' -Encoding utf8
    Invoke-TempGit @('add', '--', 'README.md')
    Invoke-TempGit @('commit', '--quiet', '-m', 'Add documentation', '-m', 'A normal review note remains developer-facing.')
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

    Write-Output 'Repository hygiene controls passed: ordinary review vocabulary is allowed; tracked internal-only paths and prohibited commit-body wording are rejected.'
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
