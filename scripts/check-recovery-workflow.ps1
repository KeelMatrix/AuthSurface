[CmdletBinding()]
param([string] $Root = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$workflow = Get-Content -LiteralPath (Join-Path $Root '.github/workflows/release.yml') -Raw
$header = [regex]::Match($workflow, '(?ms)^on:\r?\n(?<header>.*?)(?=^\S)').Groups['header'].Value
$triggers = @([regex]::Matches($header, '(?m)^  ([a-z_]+):') | ForEach-Object { $_.Groups[1].Value })
if (($triggers -join ',') -cne 'push,workflow_dispatch') { throw 'Release events must be tag push and manual symbols recovery only.' }
$jobs = @([regex]::Matches($workflow.Substring($workflow.IndexOf('jobs:')), '(?m)^  ([a-z-]+):') | ForEach-Object { $_.Groups[1].Value })
if (($jobs -join ',') -cne 'release,recover-symbols') { throw 'Release must have exactly the primary and isolated recovery jobs.' }
$primary = [regex]::Match($workflow, '(?ms)^  release:.*?(?=^  recover-symbols:)').Value
$recovery = [regex]::Match($workflow, '(?ms)^  recover-symbols:.*\z').Value
$primaryGuard = "    if: `${{ github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') }}"
$recoveryGuard = "    if: `${{ github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && github.run_attempt == 1 }}"
if (($primary -split '\r?\n')[1] -cne $primaryGuard -or ($recovery -split '\r?\n')[1] -cne $recoveryGuard) {
    throw 'Primary tag routing or isolated first-attempt main recovery guard is missing.'
}
if ([regex]::Matches($primary, '(?m)^    if:').Count -ne 1 -or [regex]::Matches($recovery, '(?m)^    if:').Count -ne 1) {
    throw 'Release jobs must have exactly one event guard.'
}
$steps = @([regex]::Split($recovery, '(?m)(?=^      - name:)') | Select-Object -Skip 1)
$names = @($steps | ForEach-Object { [regex]::Match($_, '- name: ([^\r\n]+)').Groups[1].Value })
$expected = @('Check out recovery source', 'Prepare exact symbols recovery', 'Retain verified recovery archive before login',
    'Authenticate symbols recovery to NuGet.org', 'Submit exact symbols only', 'Retain recovery effect receipt')
if (($names -join '|') -cne ($expected -join '|') -or $recovery -match 'continue-on-error:|\bneeds:|dotnet |push-release-package|--skip-duplicate|--symbol-source') {
    throw 'Recovery must preserve prepare/retain/login/submit/receipt order with fatal failures and no primary publication.'
}
for ($i = 0; $i -lt 5; $i++) { if ($steps[$i] -match '(?m)^        if:') { throw 'Recovery cannot bypass an earlier step.' } }
if ([regex]::Matches($steps[5], '(?m)^        if:').Count -ne 1) { throw 'Receipt retention must have one exact outcome guard.' }
foreach ($pair in @(@(1, 'Prepare'), @(4, 'Submit'))) {
    $expectedCall = "          & ./scripts/recover-symbols.ps1 -Mode $($pair[1]) -OutputDirectory ./artifacts/symbol-recovery"
    $step = $steps[$pair[0]]
    if ($step -notlike "*$expectedCall*" -or $step -notmatch '(?m)^          if \(\$LASTEXITCODE -ne 0\) \{ throw ' -or
        @([regex]::Matches($step, '(?m)^          & ')).Count -ne 1 -or $step -match '(?s)run:.*\$\{\{') {
        throw 'Recovery must call the exact helper mode and propagate its exit without input interpolation.'
    }
}
foreach ($i in @(2, 5)) {
    foreach ($text in @('uses: actions/upload-artifact@', 'path: artifacts/symbol-recovery/', 'if-no-files-found: error', 'retention-days: 90')) {
        if ($steps[$i] -notlike "*$text*") { throw 'Recovery archive and receipts must be retained with missing-file failure.' }
    }
}
foreach ($text in @("id: recovery-preflight", "id: recovery-login", 'uses: NuGet/login@', 'user: dmitriyzen',
    'group: authsurface-symbol-recovery-0.1.0', 'cancel-in-progress: false', 'timeout-minutes: 10',
    "if: `${{ always() && steps.recovery-preflight.outcome == 'success' }}",
    'NUGET_PUSH_TOKEN: ${{ steps.recovery-login.outputs.NUGET_API_KEY }}')) {
    if ($recovery -notlike "*$text*") { throw "Recovery workflow is missing required binding: $text" }
}
foreach ($pair in @(@('CANDIDATE_COMMIT', 'candidate_commit'), @('ARCHIVE_SHA256', 'archive_sha256'), @('PACKAGE_ID', 'package_id'),
    @('PACKAGE_VERSION', 'package_version'), @('SOURCE_COMMIT', 'source_commit'), @('SOURCE_TAG', 'source_tag'), @('SERVICE_INDEX', 'service_index'))) {
    $binding = '      RECOVERY_' + $pair[0] + ': ${{ inputs.' + $pair[1] + ' }}'
    if ($recovery -notlike "*$binding*" -or $header -notmatch ("(?m)^      " + $pair[1] + ":\r?\n        description: [^\r\n]+\r?\n        required: true\r?\n        type: string")) {
        throw 'Recovery dispatch inputs must be required strings bound through environment variables.'
    }
}
$global:LASTEXITCODE = 0
