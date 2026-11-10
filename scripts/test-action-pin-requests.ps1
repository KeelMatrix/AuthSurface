[CmdletBinding()]
param(
    [ValidateSet('', 'direct', 'mutations', 'full')]
    [string] $ChildCaller = ''
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $PSScriptRoot 'validate-release-workflow.ps1'
$pins = @(Get-Content (Join-Path $PSScriptRoot 'release-action-pins.txt') | Where-Object { $_ })
$requests = [System.Collections.Generic.List[object]]::new()
$failure = ''
$failurePin = ''
$failureKind = ''
$testToken = 'synthetic-action-pin-test-token'

function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec, $MaximumRedirection, [switch] $UseBasicParsing)
    if ($TimeoutSec -ne 15 -or $MaximumRedirection -ne 0) { throw 'Request bounds changed.' }
    if ($Uri -notmatch '^https://api\.github\.com/repos/(?<repo>[^/]+/[^/]+)/(?:git/commits/(?<commit>[a-f0-9]{40})|contents/(?<file>action\.ya?ml)\?ref=(?<ref>[a-f0-9]{40}))$') {
        throw 'Unexpected verification endpoint.'
    }
    $repository = $Matches.repo
    $sha = if ($Matches.commit) { $Matches.commit } else { $Matches.ref }
    $kind = if ($Matches.commit) { 'commit' } else { $Matches.file }
    $pin = "$repository@$sha"
    $requests.Add([pscustomobject]@{ Pin = $pin; Kind = $kind; Authenticated = $Headers.Authorization -ceq "Bearer $testToken" })
    $selected = $pin -eq $failurePin -and ($kind -eq $failureKind -or ($failureKind -eq 'metadata' -and $kind -ne 'commit'))
    if (-not $Headers.Authorization -or ($selected -and $failure -in @('403', '429', '404'))) {
        $status = if (-not $Headers.Authorization) { 403 } else { [int]$failure }
        $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$status)
        throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("Provider detail must not escape: $testToken", $response)
    }
    if ($selected -and $failure -eq 'timeout') { throw [TimeoutException]::new("Provider detail must not escape: $testToken") }
    if ($selected -and $failure -eq 'malformed') { return 'not JSON' }
    if ($selected -and $failure -eq 'null') { return $null }
    if ($kind -eq 'commit') {
        if ($selected -and $failure -eq 'noncommit') { return @{ sha = ('0' * 40); type = 'tag' } }
        if ($selected -and $failure -eq 'array') { return ,@(@{ sha = $sha }, @{ sha = $sha }) }
        return @{ sha = $sha }
    }
    $metadata = "runs:`n  using: node24`n"
    if ($selected) {
        switch ($failure) {
            'runtime' { $metadata = "runs:`n  using: node20`n" }
            'missing-runtime' { $metadata = 'name: missing runtime' }
            'empty' { $metadata = '' }
            'encoding' { return @{ type = 'file'; encoding = 'none'; content = $metadata } }
            'base64' { return @{ type = 'file'; encoding = 'base64'; content = '!' } }
            'utf8' { return @{ type = 'file'; encoding = 'base64'; content = '/w==' } }
            'directory' { return @(@{ type = 'dir' }) }
        }
    }
    return @{ type = 'file'; encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($metadata)) }
}

$originalToken = $env:GITHUB_TOKEN
$originalActions = $env:GITHUB_ACTIONS
try {
    $env:GITHUB_TOKEN = $testToken
    $env:GITHUB_ACTIONS = 'true'
    if ($ChildCaller) {
        $failure = '403'
        $failurePin = $pins[0]
        $failureKind = 'commit'
        switch ($ChildCaller) {
            'direct' { & $validator }
            'mutations' { & (Join-Path $PSScriptRoot 'test-release-workflow.ps1') }
            'full' { & (Join-Path $PSScriptRoot 'validate.ps1') -Configuration Release }
        }
        throw 'Unavailable verification did not stop the caller.'
    }

    & $validator
    if ($requests.Count -ne 2 * $pins.Count -or @($requests | Where-Object { -not $_.Authenticated }).Count) {
        throw 'Every pin must have an authenticated commit and metadata lookup.'
    }
    foreach ($pin in $pins) {
        foreach ($kind in @('commit', 'metadata')) {
            $cases = @('403', '429', 'timeout', 'malformed', 'null')
            $cases += if ($kind -eq 'commit') { @('noncommit', '404', 'array') } else { @('404', 'runtime', 'missing-runtime', 'empty', 'encoding', 'base64', 'utf8', 'directory') }
            foreach ($case in $cases) {
                $failure = $case
                $failurePin = $pin
                $failureKind = $kind
                $requests.Clear()
                $rejected = $false
                try { & $validator | Out-Null }
                catch {
                    if ($_ -like "*$testToken*") { throw 'Provider error leaked credentials.' }
                    $rejected = $true
                }
                if (-not $rejected) { throw "$pin $kind $case was accepted." }
                $targetRequests = @($requests | Where-Object { $_.Pin -eq $pin })
                $expectedCount = if ($kind -eq 'commit') { 1 } elseif ($case -eq '404') { 3 } else { 2 }
                if ($targetRequests.Count -ne $expectedCount -or @($requests | Where-Object { -not $_.Authenticated }).Count) {
                    throw "$pin $kind $case did not reach the intended authenticated boundary or used an unexpected fallback."
                }
            }
        }
        $failure = '404'
        $failurePin = $pin
        $failureKind = 'action.yml'
        $requests.Clear()
        & $validator | Out-Null
        if (@($requests | Where-Object { $_.Pin -eq $pin -and $_.Kind -eq 'action.yaml' -and $_.Authenticated }).Count -ne 1) {
            throw 'Missing action.yml must use authenticated action.yaml fallback.'
        }
    }

    $failure = ''
    $requests.Clear()
    $env:GITHUB_TOKEN = ''
    $missingTokenRejected = $false
    try { & $validator | Out-Null } catch { $missingTokenRejected = $true }
    if (-not $missingTokenRejected -or $requests.Count) { throw 'CI without its token must fail before any anonymous request.' }
    $env:GITHUB_ACTIONS = 'false'
    $anonymousRejected = $false
    try { & $validator | Out-Null } catch { $anonymousRejected = $true }
    if (-not $anonymousRejected -or $requests.Count -ne 1) { throw 'Anonymous rate limiting must fail closed.' }

    . (Join-Path $sourceRoot 'build/Invoke-NestedPwsh.ps1')
    foreach ($caller in @('direct', 'mutations', 'full')) {
        $output = Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ChildCaller $caller 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -or $output -notlike '*Could not verify pinned action commit*' -or
            $output -like '*controlled restore*' -or $output -like "*$testToken*") {
            throw "Verification failure did not propagate safely through $caller."
        }
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Action pin request tests passed: all pins, authenticated requests, anonymous quota, provider failures, object/metadata/runtime rejection, YAML fallback, and caller exit propagation.'
}
finally {
    $env:GITHUB_TOKEN = $originalToken
    $env:GITHUB_ACTIONS = $originalActions
}
