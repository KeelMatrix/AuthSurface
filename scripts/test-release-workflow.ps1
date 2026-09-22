[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$validator = Join-Path $sourceRoot 'scripts/validate-release-workflow.ps1'
$annotatedTagObjectSha = 'ebc737b6fc418a6ca0073cf116ec8dc156d8b81e'

function New-TestRoot {
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-workflow-test-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $testRoot '.github/workflows'), (Join-Path $testRoot 'scripts') -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $sourceRoot '.github/workflows') -File | Copy-Item -Destination (Join-Path $testRoot '.github/workflows')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'scripts/release-action-pins.txt') -Destination (Join-Path $testRoot 'scripts/release-action-pins.txt')
    return $testRoot
}

function Assert-Rejected([string] $Name, [scriptblock] $Mutation) {
    $testRoot = New-TestRoot
    try {
        & $Mutation $testRoot
        $rejected = $false
        try { & $validator -Root $testRoot }
        catch {
            $rejected = $true
            Write-Output "${Name}: rejected as expected"
        }
        if (-not $rejected) { throw "$Name was accepted unexpectedly." }
    }
    finally {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

& $validator -Root $sourceRoot

$releaseWorkflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.github/workflows/release.yml') -Raw
$pushCommands = @([regex]::Matches($releaseWorkflow, '(?m)^\s*dotnet nuget push\s+.*$') | ForEach-Object { $_.Value.Trim() })
if ($pushCommands.Count -ne 1 -or $pushCommands[0] -notmatch '\.nupkg') {
    throw 'Release workflow must use one package push that covers the associated symbol package.'
}
if ($pushCommands[0] -match '\.snupkg') {
    throw 'Release workflow must not push the symbol package a second time.'
}
if ($releaseWorkflow -notmatch '(?ms)dotnet nuget push.*?\r?\n\s*if \(\$LASTEXITCODE -ne 0\)') {
    throw 'Release workflow must check the publication command exit code immediately.'
}

function Invoke-CheckedNative([scriptblock] $Command) {
    & $Command
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Native command failed with exit code $exitCode."
    }
}

$publicationFailureRejected = $false
try {
    Invoke-CheckedNative { dotnet --authsurface-release-test-invalid-option }
}
catch {
    $publicationFailureRejected = $true
    Write-Output 'publication command failure: rejected as expected'
}
if (-not $publicationFailureRejected) {
    throw 'Publication command failure was accepted unexpectedly.'
}

Assert-Rejected 'unpinned action' {
    param($testRoot)
    $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
    $ci = Get-Content -LiteralPath $ciPath -Raw
    $ci = $ci.Replace('actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683', 'actions/checkout@main')
    Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
}

Assert-Rejected 'tag action reference' {
    param($testRoot)
    $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
    $ci = Get-Content -LiteralPath $ciPath -Raw
    $ci = $ci.Replace('actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9', 'actions/setup-dotnet@v4')
    Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
}

Assert-Rejected 'annotated tag object SHA' {
    param($testRoot)
    $releasePath = Join-Path $testRoot '.github/workflows/release.yml'
    $release = Get-Content -LiteralPath $releasePath -Raw
    $release = $release.Replace('NuGet/login@8d196754b4036150537f80ac539e15c2f1028841', "NuGet/login@$annotatedTagObjectSha")
    Set-Content -LiteralPath $releasePath -Value $release -Encoding utf8
    $allowlistPath = Join-Path $testRoot 'scripts/release-action-pins.txt'
    $allowlist = Get-Content -LiteralPath $allowlistPath -Raw
    $allowlist = $allowlist.Replace('NuGet/login@8d196754b4036150537f80ac539e15c2f1028841', "NuGet/login@$annotatedTagObjectSha")
    Set-Content -LiteralPath $allowlistPath -Value $allowlist -Encoding utf8
}

Assert-Rejected 'workflow action missing from allowlist' {
    param($testRoot)
    $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
    $ci = Get-Content -LiteralPath $ciPath -Raw
    $ci = $ci.Replace('actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683', 'actions/checkout@67a3573c9a986a3f9c594539f4ab511d57bb3ce9')
    Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
}

Write-Output 'Workflow validator mutation tests passed: real workflows, operational single-push/exit-code checks, publication failure control, unpinned action, tag reference, annotated tag object SHA, and missing allowlist entry.'
