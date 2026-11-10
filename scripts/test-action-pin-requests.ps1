[CmdletBinding()]
param(
    [ValidateSet('', 'direct', 'mutations', 'full')]
    [string] $ChildCaller = '',
    [ValidateSet('403', 'commit-singleton', 'yml-empty', 'fallback-singleton')]
    [string] $ChildCase = '403'
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$validator = Join-Path $PSScriptRoot 'validate-release-workflow.ps1'
$pins = @(Get-Content (Join-Path $PSScriptRoot 'release-action-pins.txt') | Where-Object { $_ } | Sort-Object -CaseSensitive)
$requests = [System.Collections.Generic.List[string]]::new()
$requestViolations = [System.Collections.Generic.List[string]]::new()
$failure = ''
$failurePin = ''
$failureKind = ''
$failureField = ''
$objectKind = 'PSCustomObject'
$testToken = 'synthetic-action-pin-test-token'
$providerBody = 'synthetic-private-provider-body'
$shapeCases = @('null', 'empty-array', 'null-array', 'two-null-array', 'singleton', 'multiple',
    'null-first', 'null-last', 'nested-empty', 'nested-singleton', 'nested-null', 'nested-multiple')

# The HTTP substitute must preserve each raw shape, just as Invoke-RestMethod does.
function Get-TestValue([string] $Case, $Object) {
    switch ($Case) {
        'null' { return $null }
        'empty-array' { return ,@() }
        'null-array' { return ,@($null) }
        'two-null-array' { return ,@($null, $null) }
        'singleton' { return ,@($Object) }
        'multiple' { return ,@($Object, $Object) }
        'null-first' { return ,@($null, $Object) }
        'null-last' { return ,@($Object, $null) }
        'nested-empty' { return ,@(,@()) }
        'nested-singleton' { return ,@(,@($Object)) }
        'nested-null' { return ,@(,@($null)) }
        'nested-multiple' { return ,@(@($Object), @($Object)) }
        'string' { return 'not an object' }
        'empty-string' { return '' }
        'number' { return 1 }
        'boolean' { return $true }
        'false' { return $false }
        'empty-object' { return @{} }
        'provider404' { return @{ message = 'Not Found'; status = 404 } }
        'envelope' { return @{ NotFound = $true; Body = $Object } }
        default { throw "Unknown response case: $Case" }
    }
}

function Throw-HttpFailure([int] $Status) {
    $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]$Status)
    throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("$providerBody $testToken", $response)
}

function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec, $MaximumRedirection, [switch] $UseBasicParsing)
    if (-not $UseBasicParsing -or $TimeoutSec -ne 15 -or $MaximumRedirection -ne 0 -or
        $Headers.Accept -cne 'application/vnd.github+json' -or
        $Headers.'User-Agent' -cne 'KeelMatrix-AuthSurface-Validator' -or
        $Headers.'X-GitHub-Api-Version' -cne '2022-11-28' -or
        ($env:GITHUB_TOKEN -and $Headers.Authorization -cne "Bearer $testToken")) {
        $requestViolations.Add('Request bounds or headers changed.')
        throw 'Request contract changed.'
    }
    if ($Uri -notmatch '^https://api\.github\.com/repos/(?<repo>[^/]+/[^/]+)/(?:git/commits/(?<commit>[a-f0-9]{40})|contents/(?<file>action\.ya?ml)\?ref=(?<ref>[a-f0-9]{40}))$') {
        $requestViolations.Add('Unexpected verification endpoint.')
        throw 'Unexpected verification endpoint.'
    }
    $sha = if ($Matches.commit) { $Matches.commit } else { $Matches.ref }
    $kind = if ($Matches.commit) { 'commit' } else { $Matches.file }
    $pin = "$($Matches.repo)@$sha"
    $requests.Add("$pin $kind")
    if ($ChildCaller) { Write-Host "Reached request: $pin $kind" }
    if (-not $Headers.Authorization) { Throw-HttpFailure 403 }
    # These two real historical pins support the workflow mutation controls.
    if ($sha -eq 'ebc737b6fc418a6ca0073cf116ec8dc156d8b81e') { Throw-HttpFailure 404 }
    if ($pin -eq $failurePin -and $failureKind -eq 'action.yaml' -and $kind -eq 'action.yml') {
        Throw-HttpFailure 404
    }
    $selected = $pin -ceq $failurePin -and $kind -ceq $failureKind
    if ($selected) {
        if ($failure -match '^\d{3}$') { Throw-HttpFailure ([int]$failure) }
        if ($failure -eq 'timeout') { throw [TimeoutException]::new("$providerBody $testToken") }
        if ($failure -in @('transport', 'message404')) { throw "$providerBody $testToken HTTP 404 Not Found" }
    }
    $body = if ($kind -eq 'commit') { @{ sha = $sha } } else {
        $runtime = if ($sha -eq '11bd71901bbe5b1630ceea73d27597364c9af683') { 'node20' } else { 'node24' }
        if ($selected -and $failure -in @('node20', 'node23', 'node25', 'composite', 'docker')) { $runtime = $failure }
        $metadata = "runs:`n  using: $runtime`n"
        if ($selected -and $failure -eq 'missing-runtime') { $metadata = 'name: missing runtime' }
        if ($selected -and $failure -eq 'empty') { $metadata = '' }
        @{ type = 'file'; encoding = 'base64'; content = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($metadata)) }
    }
    if ($selected) {
        if ($failureField) {
            if ($failure -eq 'missing') { $body.Remove($failureField) }
            else { $body[$failureField] = Get-TestValue $failure $body[$failureField] }
        }
        else {
            switch ($failure) {
                'noncommit' { $body = @{ sha = ('0' * 40); type = 'tag' } }
                'wrong-case-sha' { $body.sha = $sha.ToUpperInvariant() }
                'directory' { $body.type = 'dir' }
                'encoding' { $body.encoding = 'none' }
                'base64' { $body.content = '!' }
                'utf8' { $body.content = '/w==' }
            }
        }
    }
    if ($objectKind -eq 'PSCustomObject') { $body = [pscustomobject]$body }
    if ($selected -and -not $failureField -and $failure -in ($shapeCases + @('string', 'empty-string', 'number', 'boolean', 'false', 'empty-object', 'provider404', 'envelope'))) {
        $raw = Get-TestValue $failure $body
        return ,$raw
    }
    return $body
}

function Assert-SafeOutput([string] $Output) {
    if ($Output.Contains($testToken) -or $Output.Contains($providerBody)) { throw 'Provider error leaked private data.' }
}

function Assert-Sequence([string[]] $Expected) {
    if ($requestViolations.Count -or ($requests -join "`n") -cne ($Expected -join "`n")) {
        throw "Unexpected request sequence or controls. Expected: $($Expected -join '; '); actual: $($requests -join '; '). $requestViolations"
    }
}

function Get-ExpectedSequence([string] $Pin, [string] $Kind, [switch] $Complete) {
    foreach ($item in $pins) {
        "$item commit"
        if (-not $Complete -and $item -ceq $Pin -and $Kind -eq 'commit') { return }
        "$item action.yml"
        if ($item -ceq $Pin -and ($Kind -eq 'action.yaml' -or ($Complete -and $failure -eq '404'))) { "$item action.yaml" }
        if (-not $Complete -and $item -ceq $Pin) { return }
    }
}

function Assert-Boundary([string] $Pin, [string] $Kind, [string] $Case, [string] $Field = '', [switch] $Accept) {
    $script:failurePin = $Pin
    $script:failureKind = $Kind
    $script:failure = $Case
    $script:failureField = $Field
    $requests.Clear()
    $requestViolations.Clear()
    $rejected = $false
    try { & $validator | Out-Null }
    catch { Assert-SafeOutput ($_ | Out-String); $rejected = $true }
    Assert-Sequence @(Get-ExpectedSequence $Pin $Kind -Complete:$Accept)
    if ($rejected -eq $Accept.IsPresent) { throw "Unexpected outcome: $Pin $Kind $Field $Case (rejected=$rejected)." }
}

$originalToken = $env:GITHUB_TOKEN
$originalActions = $env:GITHUB_ACTIONS
try {
    $env:GITHUB_TOKEN = $testToken
    $env:GITHUB_ACTIONS = 'true'
    if ($ChildCaller) {
        $failurePin = $pins[0]
        switch ($ChildCase) {
            '403' { $failure = '403'; $failureKind = 'commit' }
            'commit-singleton' { $failure = 'singleton'; $failureKind = 'commit' }
            'yml-empty' { $failure = 'empty-array'; $failureKind = 'action.yml' }
            'fallback-singleton' { $failure = 'singleton'; $failureKind = 'action.yaml' }
        }
        switch ($ChildCaller) {
            'direct' { & $validator }
            'mutations' { & (Join-Path $PSScriptRoot 'test-release-workflow.ps1') }
            'full' { & (Join-Path $PSScriptRoot 'validate.ps1') -Configuration Release }
        }
        # A returning caller is a failed negative control, not an artificial nonzero exit.
        exit 0
    }

    foreach ($representation in @('PSCustomObject', 'Hashtable')) {
        $objectKind = $representation
        Assert-Boundary '' '' '' -Accept
        foreach ($pin in $pins) { Assert-Boundary $pin 'action.yml' '404' -Accept }
    }
    $objectKind = 'PSCustomObject'
    $shapeCount = 0
    foreach ($pin in $pins) {
        foreach ($kind in @('commit', 'action.yml', 'action.yaml')) {
            foreach ($case in $shapeCases) {
                Assert-Boundary $pin $kind $case
                $shapeCount++
            }
            foreach ($case in @('string', 'empty-string', 'number', 'boolean', 'false', 'empty-object', 'provider404', 'envelope',
                    '302', '401', '403', '404', '429', '500', 'timeout', 'transport', 'message404')) {
                # A real action.yml 404 is the sole successful fallback control.
                Assert-Boundary $pin $kind $case -Accept:($kind -eq 'action.yml' -and $case -eq '404')
            }
            $fields = if ($kind -eq 'commit') { @('sha') } else { @('type', 'encoding', 'content') }
            foreach ($field in $fields) {
                foreach ($case in ($shapeCases + @('missing', 'empty-string', 'string', 'number', 'boolean', 'empty-object'))) {
                    Assert-Boundary $pin $kind $case $field
                }
            }
            if ($kind -eq 'commit') {
                foreach ($case in @('noncommit', 'wrong-case-sha')) { Assert-Boundary $pin $kind $case }
            }
            else {
                foreach ($case in @('directory', 'encoding', 'base64', 'utf8', 'empty', 'missing-runtime', 'node20', 'node23')) {
                    Assert-Boundary $pin $kind $case
                }
                foreach ($case in @('node25', 'composite', 'docker')) { Assert-Boundary $pin $kind $case -Accept }
            }
        }
    }
    if ($shapeCount -ne 108) { throw 'The raw-shape matrix did not cover every pin and endpoint.' }

    $failure = ''
    $failureField = ''
    $requests.Clear()
    $env:GITHUB_TOKEN = ''
    $missingTokenRejected = $false
    try { & $validator | Out-Null } catch { Assert-SafeOutput ($_ | Out-String); $missingTokenRejected = $true }
    if (-not $missingTokenRejected -or $requests.Count) { throw 'CI without its token must fail before any anonymous request.' }
    $env:GITHUB_ACTIONS = 'false'
    $anonymousRejected = $false
    try { & $validator | Out-Null } catch { Assert-SafeOutput ($_ | Out-String); $anonymousRejected = $true }
    Assert-Sequence @("$($pins[0]) commit")
    if (-not $anonymousRejected) { throw 'Anonymous rate limiting must fail closed.' }
    $env:GITHUB_TOKEN = $testToken
    $env:GITHUB_ACTIONS = 'true'

    # Exercise workflow mutations through the same deterministic transport boundary.
    & (Join-Path $PSScriptRoot 'test-release-workflow.ps1') | Out-Null
    if ($requestViolations.Count) { throw 'Workflow mutations changed request controls.' }

    . (Join-Path $sourceRoot 'build/Invoke-NestedPwsh.ps1')
    foreach ($caller in @('direct', 'mutations', 'full')) {
        foreach ($case in @('403', 'commit-singleton', 'yml-empty', 'fallback-singleton')) {
            $output = Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ChildCaller $caller -ChildCase $case 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
            Assert-SafeOutput $output
            $kind = switch ($case) { 'yml-empty' { 'action.yml' }; 'fallback-singleton' { 'action.yaml' }; default { 'commit' } }
            $diagnostic = if ($kind -eq 'commit') { 'Could not verify pinned action commit' } else { 'Could not read pinned action metadata' }
            $expected = @(Get-ExpectedSequence $pins[0] $kind)
            $actual = @([regex]::Matches($output, '(?m)^Reached request: ([^\r\n]+)\r?$') | ForEach-Object { $_.Groups[1].Value })
            if ($exitCode -eq 0 -or $output -notlike "*$diagnostic*" -or $output -like '*controlled restore*' -or
                ($actual -join "`n") -cne ($expected -join "`n")) {
                throw "Verification failure did not propagate safely through $caller ($case)."
            }
        }
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Action pin request tests passed: 108 raw-shape cases across all pins and endpoints, object/field/content/runtime controls, actual-404-only fallback, request sequences, authentication/redaction, workflow mutations, and 12 caller propagation cases.'
}
finally {
    $env:GITHUB_TOKEN = $originalToken
    $env:GITHUB_ACTIONS = $originalActions
}
