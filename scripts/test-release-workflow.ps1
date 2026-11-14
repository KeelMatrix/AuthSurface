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

foreach ($line in @(
    "    if: `${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') }}",
    "    if: `${{ github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && github.run_attempt == 1 }}",
    '      RECOVERY_ARCHIVE_SHA256: ${{ inputs.archive_sha256 }}',
    '      RECOVERY_CANDIDATE_COMMIT: ${{ inputs.candidate_commit }}',
    '      cancel-in-progress: false',
    "        if: `${{ always() && steps.recovery-preflight.outcome == 'success' }}"
)) {
    Assert-Rejected "missing recovery routing/binding: $line" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        $workflow = (Get-Content -LiteralPath $path -Raw).Replace($line, '')
        Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
    }
}
foreach ($stepName in @('Prepare exact symbols recovery', 'Retain verified recovery archive before login',
    'Authenticate symbols recovery to NuGet.org', 'Submit exact symbols only', 'Retain recovery effect receipt')) {
    foreach ($bypass in @('continue-on-error: true', 'if: always()')) {
        Assert-Rejected "recovery step bypass: $stepName / $bypass" {
            param($testRoot)
            $path = Join-Path $testRoot '.github/workflows/release.yml'
            $workflow = (Get-Content -LiteralPath $path -Raw).Replace("- name: $stepName", "- name: $stepName`n        $bypass")
            Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
        }
    }
}
foreach ($mode in @('Prepare', 'Submit')) {
    Assert-Rejected "recovery caller ignores native exit: $mode" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        $workflow = Get-Content -LiteralPath $path -Raw
        $workflow = [regex]::Replace($workflow, "(?m)(-Mode $mode -OutputDirectory [^\r\n]+\r?\n)          if \([^\r\n]+", '$1')
        Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
    }
}
Assert-Rejected 'recovery authenticates before durable retention' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -LiteralPath $path -Raw
    $retention = [regex]::Match($workflow, '(?ms)^      - name: Retain verified recovery archive before login.*?(?=^      - name:)').Value
    $workflow = $workflow.Replace($retention, '').Replace('      - name: Submit exact symbols only', $retention + '      - name: Submit exact symbols only')
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'recovery reaches primary publication' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = (Get-Content -LiteralPath $path -Raw).Replace('-Mode Submit', '-Mode Submit; ./scripts/push-release-package.ps1')
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}

$releaseWorkflow = Get-Content -LiteralPath (Join-Path $sourceRoot '.github/workflows/release.yml') -Raw
foreach ($text in @('. ./build/Invoke-PublicPackageSession.ps1', 'Invoke-PublicPackageSession -Root', '--no-incremental', 'Set-PublicPackageInspection $session')) {
    Assert-Rejected "missing public session control: $text" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        Set-Content -LiteralPath $path -Value ((Get-Content -LiteralPath $path -Raw).Replace($text, 'removed')) -Encoding utf8
    }
}
foreach ($boundary in @('Build','Test','Pack','Public preparation')) {
    Assert-Rejected "missing fatal $boundary check" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        $workflow = [regex]::Replace((Get-Content -LiteralPath $path -Raw), ('(?m)^ +if \(\$LASTEXITCODE -ne 0\) \{ throw "' + [regex]::Escape($boundary) + ' failed[^\r\n]+'), '')
        Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
    }
}
$releaseJobTimeout = [regex]::Match(
    $releaseWorkflow,
    '(?ms)^  release:\s*\r?\n(?:(?!^  \S).)*?^    timeout-minutes:\s+(?<minutes>\S+)\s*$')
if (-not $releaseJobTimeout.Success -or $releaseJobTimeout.Groups['minutes'].Value -notmatch '^[1-9]\d*$' -or [int]$releaseJobTimeout.Groups['minutes'].Value -gt 120) {
    throw 'Release workflow must define a job-level timeout between 1 and 120 minutes.'
}
# Native protocol behavior and real child exit codes are exercised by
# ReleasePublicationTests; these mutations cover the workflow's call site.
foreach ($stepName in @('Push package', 'Retain inspected release archives', 'Prepare public locked package pair')) {
    Assert-Rejected "ignored failure for $stepName" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        $workflow = Get-Content -LiteralPath $path -Raw
        $workflow = $workflow.Replace("- name: $stepName", "- name: $stepName`n        continue-on-error: true")
        Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
    }
}
foreach ($text in @('-RepositoryRoot $env:GITHUB_WORKSPACE', '-ExpectedCommit ''${{ github.sha }}''', '-SymbolPackagePath (Join-Path $output $expected[1])')) {
    Assert-Rejected "missing packaged source identity: $text" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        Set-Content -LiteralPath $path -Value ((Get-Content -Raw -LiteralPath $path).Replace($text, '')) -Encoding utf8
    }
}
Assert-Rejected 'missing package inspection exit check' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = [regex]::Replace((Get-Content -Raw -LiteralPath $path), '(?m)^ +if \(\$LASTEXITCODE -ne 0\) \{ throw "Package inspection failed[^\r\n]+', '')
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'package inspection bypass condition' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = (Get-Content -Raw -LiteralPath $path).Replace('- name: Prepare public locked package pair', "- name: Prepare public locked package pair`n        if: always()")
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'package inspection before pack' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -Raw -LiteralPath $path
    $inspection = [regex]::Match($workflow, '(?m)^ +& \.\\scripts\\inspect-package\.ps1[^\r\n]+\r?\n +if \(\$LASTEXITCODE[^\r\n]+').Value
    $workflow = $workflow.Replace($inspection, '').Replace('              dotnet pack', $inspection + "`n              dotnet pack")
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'duplicate package inspection' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -Raw -LiteralPath $path
    $inspection = [regex]::Match($workflow, '(?ms)^      - name: Prepare public locked package pair.*?(?=^      - name:)').Value
    Set-Content -LiteralPath $path -Value ($workflow.Replace($inspection, $inspection + $inspection)) -Encoding utf8
}
Assert-Rejected 'duplicate publication call' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -LiteralPath $path -Raw
    $workflow += "`n      - name: Duplicate publication`n        run: pwsh ./scripts/push-release-package.ps1`n"
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
foreach ($text in @('SHA256SUMS', 'if-no-files-found: error', 'artifacts/packages/', 'artifacts/build-evidence/', 'retention-days: 90')) {
    Assert-Rejected "missing archive evidence: $text" {
        param($testRoot)
        $path = Join-Path $testRoot '.github/workflows/release.yml'
        $workflow = (Get-Content -LiteralPath $path -Raw).Replace($text, 'removed')
        Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
    }
}
Assert-Rejected 'authentication before archive retention' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -LiteralPath $path -Raw
    $retention = [regex]::Match($workflow, '(?ms)^      - name: Retain inspected release archives.*?(?=^      - name:)').Value
    $workflow = $workflow.Replace($retention, '').Replace('      - name: Push package', $retention + '      - name: Push package')
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'raw symbol endpoint override' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = (Get-Content -LiteralPath $path -Raw).Replace('push-release-package.ps1 -PackagePath', "push-release-package.ps1 --symbol-source 'https://symbols.nuget.org/upload' -PackagePath")
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}
Assert-Rejected 'missing publication exit check' {
    param($testRoot)
    $path = Join-Path $testRoot '.github/workflows/release.yml'
    $workflow = Get-Content -LiteralPath $path -Raw
    $workflow = [regex]::Replace($workflow, '(?m)^ +if \(\$LASTEXITCODE -ne 0\) \{ throw "Package and symbol publication[^\r\n]+', '')
    Set-Content -LiteralPath $path -Value $workflow -Encoding utf8
}

Assert-Rejected 'unpinned action' {
    param($testRoot)
    $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
    $ci = Get-Content -LiteralPath $ciPath -Raw
    $ci = $ci.Replace('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'actions/checkout@main')
    Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
}

Assert-Rejected 'tag action reference' {
    param($testRoot)
    $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
    $ci = Get-Content -LiteralPath $ciPath -Raw
    $ci = $ci.Replace('actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68', 'actions/setup-dotnet@v4')
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
    $ci = $ci.Replace('actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1', 'actions/checkout@a98b56852c35b8e3190ac28c8c2271da59106c68')
    Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
}

foreach ($scriptName in @('validate-release-workflow.ps1', 'validate.ps1')) {
    Assert-Rejected "missing CI token for $scriptName" {
        param($testRoot)
        $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
        $ci = Get-Content -LiteralPath $ciPath -Raw
        $ci = [regex]::Replace($ci,
            "(?m)^        env:\r?\n          GITHUB_TOKEN: [^\r\n]+\r?\n(?=        run: pwsh -NoProfile -File ./scripts/$([regex]::Escape($scriptName))(?:\s|$))", '')
        Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
    }
    Assert-Rejected "ignored CI failure for $scriptName" {
        param($testRoot)
        $ciPath = Join-Path $testRoot '.github/workflows/ci.yml'
        $ci = Get-Content -LiteralPath $ciPath -Raw
        $ci = [regex]::Replace($ci,
            "(?m)^(        run: pwsh -NoProfile -File ./scripts/$([regex]::Escape($scriptName))(?:[^\r\n]*))$", "        continue-on-error: true`n`$1")
        Set-Content -LiteralPath $ciPath -Value $ci -Encoding utf8
    }
}

Assert-Rejected 'release job timeout missing' {
    param($testRoot)
    $releasePath = Join-Path $testRoot '.github/workflows/release.yml'
    $release = Get-Content -LiteralPath $releasePath -Raw
    $release = $release -replace '(?m)^    timeout-minutes:\s+\S+\r?\n', ''
    Set-Content -LiteralPath $releasePath -Value $release -Encoding utf8
}

Assert-Rejected 'release job timeout zero' {
    param($testRoot)
    $releasePath = Join-Path $testRoot '.github/workflows/release.yml'
    $release = Get-Content -LiteralPath $releasePath -Raw
    $release = $release -replace '(?m)^    timeout-minutes:\s+\S+\r?\n', "    timeout-minutes: 0`n"
    Set-Content -LiteralPath $releasePath -Value $release -Encoding utf8
}

Assert-Rejected 'release job timeout unreasonably large' {
    param($testRoot)
    $releasePath = Join-Path $testRoot '.github/workflows/release.yml'
    $release = Get-Content -LiteralPath $releasePath -Raw
    $release = $release -replace '(?m)^    timeout-minutes:\s+\S+\r?\n', "    timeout-minutes: 121`n"
    Set-Content -LiteralPath $releasePath -Value $release -Encoding utf8
}

Assert-Rejected 'release job timeout invalid' {
    param($testRoot)
    $releasePath = Join-Path $testRoot '.github/workflows/release.yml'
    $release = Get-Content -LiteralPath $releasePath -Raw
    $release = $release -replace '(?m)^    timeout-minutes:\s+\S+\r?\n', "    timeout-minutes: thirty`n"
    Set-Content -LiteralPath $releasePath -Value $release -Encoding utf8
}

Assert-Rejected 'correctly pinned deprecated JavaScript runtime' {
    param($testRoot)
    $currentCheckout = 'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1'
    $deprecatedCheckout = 'actions/checkout@11bd71901bbe5b1630ceea73d27597364c9af683'
    Get-ChildItem -LiteralPath (Join-Path $testRoot '.github/workflows') -File | ForEach-Object {
        $workflow = Get-Content -LiteralPath $_.FullName -Raw
        Set-Content -LiteralPath $_.FullName -Value $workflow.Replace($currentCheckout, $deprecatedCheckout) -Encoding utf8
    }
    $allowlistPath = Join-Path $testRoot 'scripts/release-action-pins.txt'
    $allowlist = Get-Content -LiteralPath $allowlistPath -Raw
    Set-Content -LiteralPath $allowlistPath -Value $allowlist.Replace($currentCheckout, $deprecatedCheckout) -Encoding utf8
}

$global:LASTEXITCODE = 0
Write-Output 'Workflow validator mutation tests passed: publication wiring/failure propagation, archive evidence before authentication, action pins, CI token wiring, bounded job timeouts, and deprecated JavaScript runtime.'
