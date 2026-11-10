[CmdletBinding()]
param(
    [ValidateSet('', 'direct', 'mutations', 'full')]
    [string] $ChildCaller = '',
    [ValidateSet('403', 'commit-singleton', 'yml-empty', 'yml-singleton', 'fallback-singleton')]
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

# Preserve the real JSON parser's wrappers as well as array cardinality and nulls.
function Get-TestJson([string] $Case, [string] $ObjectJson) {
    switch ($Case) {
        'null' { return 'null' }
        'empty-array' { return '[]' }
        'null-array' { return '[null]' }
        'two-null-array' { return '[null,null]' }
        'singleton' { return "[$ObjectJson]" }
        'multiple' { return "[$ObjectJson,$ObjectJson]" }
        'null-first' { return "[null,$ObjectJson]" }
        'null-last' { return "[$ObjectJson,null]" }
        'nested-empty' { return '[[]]' }
        'nested-singleton' { return "[[$ObjectJson]]" }
        'nested-null' { return '[[null]]' }
        'nested-multiple' { return "[[$ObjectJson],[$ObjectJson]]" }
        'string' { return '"not an object"' }
        'empty-string' { return '""' }
        'number' { return '1' }
        'boolean' { return 'true' }
        'false' { return 'false' }
        'empty-object' { return '{}' }
        'provider404' { return '{"message":"Not Found","status":404}' }
        'envelope' { return '{"NotFound":true,"Body":' + $ObjectJson + '}' }
        default { throw "Unknown response case: $Case" }
    }
}

function Read-TestJson([string] $Json) {
    $raw = Microsoft.PowerShell.Utility\ConvertFrom-Json -InputObject $Json -NoEnumerate -AsHashtable:($objectKind -eq 'Hashtable')
    return ,$raw
}

function Assert-ParserSeam {
    $value = Read-TestJson '{"sha":"synthetic-sha"}'
    $array = Read-TestJson '[{"sha":"synthetic-sha"}]'
    $validObject = if ($objectKind -eq 'Hashtable') { $value -is [System.Collections.IDictionary] }
    else { $value -is [System.Management.Automation.PSCustomObject] }
    if (-not $validObject -or $value -is [array] -or $value.sha -cne 'synthetic-sha' -or
        $array -isnot [array] -or $array -isnot [pscustomobject] -or $array.Count -ne 1) {
        throw "JSON parser seam did not preserve native object/array wrappers ($objectKind)."
    }
    Write-Output "Parser seam ($objectKind): object=True; parsed array=True; shorthand=True."
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
            else {
                $fieldJson = ConvertTo-Json -InputObject $body[$failureField] -Compress
                $body[$failureField] = Read-TestJson (Get-TestJson $failure $fieldJson)
            }
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
    if ($selected -and $failure -eq 'extra-envelope-properties') {
        $body.NotFound = $true
        $body.Body = $null
    }
    # Retain compatibility with a caller supplying an ordinary, unparsed dictionary.
    if ($objectKind -eq 'ManualHashtable') { return $body }
    $json = ConvertTo-Json -InputObject $body -Depth 20 -Compress
    if ($selected -and -not $failureField -and $failure -in ($shapeCases + @('string', 'empty-string', 'number', 'boolean', 'false', 'empty-object', 'provider404', 'envelope'))) {
        $json = Get-TestJson $failure $json
    }
    $raw = Read-TestJson $json
    return ,$raw
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
    $output = [System.Collections.Generic.List[string]]::new()
    $diagnostic = ''
    try { & $validator | ForEach-Object { $output.Add([string]$_) } }
    catch { $diagnostic = $_.Exception.Message; $rejected = $true }
    Assert-SafeOutput (($output -join "`n") + $diagnostic)
    if ($rejected -and ($output -match 'Workflow action pin contract passed:')) { throw 'Rejected verification printed success.' }
    Assert-Sequence @(Get-ExpectedSequence $Pin $Kind -Complete:$Accept)
    if ($rejected -eq $Accept.IsPresent) { throw "Unexpected outcome: $Pin $Kind $Field $Case (rejected=$rejected)." }
    if (-not $Accept -and -not $Field -and $Case -in $shapeCases) {
        $expectedDiagnostic = if ($Kind -eq 'commit') {
            "Could not verify pinned action commit through GitHub: $Pin. GitHub action verification failed: expected one response object."
        }
        else { "Could not read pinned action metadata for $Pin. Verification was unavailable or invalid." }
        if ($diagnostic -cne $expectedDiagnostic) { throw "Unexpected raw-shape diagnostic ($Case): $diagnostic" }
    }
    if ($Accept -and -not ($output -match 'Workflow action pin contract passed:')) { throw 'Valid verification did not print success.' }
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
            'yml-singleton' { $failure = 'singleton'; $failureKind = 'action.yml' }
            'fallback-singleton' { $failure = 'singleton'; $failureKind = 'action.yaml' }
        }
        $nativeDotnet = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
        function dotnet {
            if ($args[0] -eq 'restore') { Write-Host 'FORBIDDEN_RESTORE'; exit 86 }
            & $nativeDotnet @args
            $global:LASTEXITCODE = $LASTEXITCODE
        }
        switch ($ChildCaller) {
            'direct' { & $validator }
            'mutations' { & (Join-Path $PSScriptRoot 'test-release-workflow.ps1') }
            'full' { & (Join-Path $PSScriptRoot 'validate.ps1') -Configuration Release }
        }
        # A returning caller is a failed negative control, not an artificial nonzero exit.
        exit 0
    }

    $objectKind = 'ManualHashtable'
    Assert-Boundary '' '' '' -Accept
    $shapeCount = 0
    foreach ($representation in @('PSCustomObject', 'Hashtable')) {
        $objectKind = $representation
        Assert-ParserSeam
        Assert-Boundary '' '' '' -Accept
        foreach ($pin in $pins) { Assert-Boundary $pin 'action.yml' '404' -Accept }
        foreach ($pin in $pins) {
            foreach ($kind in @('commit', 'action.yml', 'action.yaml')) {
                Assert-Boundary $pin $kind '' -Accept
                Assert-Boundary $pin $kind 'extra-envelope-properties' -Accept
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
    }
    if ($shapeCount -ne 216) { throw 'The raw-shape matrix did not cover both representations at every pin and endpoint.' }
    $objectKind = 'PSCustomObject'

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
        foreach ($case in @('403', 'commit-singleton', 'yml-empty', 'yml-singleton', 'fallback-singleton')) {
            $output = Invoke-NestedPwsh -NoProfile -File $PSCommandPath -ChildCaller $caller -ChildCase $case 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
            Assert-SafeOutput $output
            $kind = switch ($case) { 'yml-empty' { 'action.yml' }; 'yml-singleton' { 'action.yml' }; 'fallback-singleton' { 'action.yaml' }; default { 'commit' } }
            $diagnostic = if ($kind -eq 'commit') { 'Could not verify pinned action commit' } else { 'Could not read pinned action metadata' }
            $expected = @(Get-ExpectedSequence $pins[0] $kind)
            $actual = @([regex]::Matches($output, '(?m)^Reached request: ([^\r\n]+)\r?$') | ForEach-Object { $_.Groups[1].Value })
            if ($exitCode -ne 1 -or $output -notlike "*$diagnostic*" -or $output -match 'controlled restore|FORBIDDEN_RESTORE|Workflow action pin contract passed:' -or
                ($actual -join "`n") -cne ($expected -join "`n")) {
                throw "Verification failure did not propagate safely through $caller ($case)."
            }
            Write-Output "Caller rejection ($caller/$case): exit=$exitCode; requests=$($actual.Count); before restore."
        }
    }
    $global:LASTEXITCODE = 0
    Write-Output 'Action pin request tests passed: 216 parser-backed raw-shape cases across both representations, all pins and endpoints, object/field/content/runtime controls, actual-404-only fallback, request sequences, authentication/redaction, workflow mutations, and 15 caller propagation cases (exit 1, before restore).'
}
finally {
    $env:GITHUB_TOKEN = $originalToken
    $env:GITHUB_ACTIONS = $originalActions
}
