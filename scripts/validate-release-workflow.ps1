[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$workflowPath = Join-Path $root '.github/workflows/release.yml'
if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf)) {
    throw "Release workflow is missing: $workflowPath"
}

$workflow = Get-Content -LiteralPath $workflowPath -Raw

if ($workflow -notmatch '(?ms)^on:\s*\r?\n\s+push:\s*\r?\n\s+tags:') {
    throw 'Release workflow must use a tag-only push trigger.'
}

foreach ($forbiddenTrigger in @('pull_request', 'schedule', 'workflow_dispatch', 'branches:')) {
    if ($workflow -match "(?m)^\s+$([regex]::Escape($forbiddenTrigger))") {
        throw "Release workflow contains a forbidden trigger or branch filter: $forbiddenTrigger"
    }
}

if ($workflow -notmatch '(?m)^\s+-\s+''v\*''\s*$') {
    throw 'Release workflow must accept version tags through the tag trigger and validate the exact format in the job.'
}

$actionPinsPath = Join-Path $root 'scripts/release-action-pins.txt'
if (-not (Test-Path -LiteralPath $actionPinsPath -PathType Leaf)) {
    throw "Release action pin allowlist is missing: $actionPinsPath"
}

$allowlistedActions = @(Get-Content -LiteralPath $actionPinsPath)
if ($allowlistedActions.Count -eq 0) {
    throw 'Release action pin allowlist must contain at least one action.'
}

foreach ($action in $allowlistedActions) {
    if ($action -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$') {
        throw "Release action pin allowlist contains an invalid entry: $action"
    }
}

if (@($allowlistedActions | Sort-Object -Unique -CaseSensitive).Count -ne $allowlistedActions.Count) {
    throw 'Release action pin allowlist contains duplicate entries.'
}

if ($workflow -notmatch '(?m)^\s+id-token:\s+write\s*$' -or
    $workflow -match '(?m)^\s+contents:\s+write\s*$') {
    throw 'Release workflow permissions are not least privilege for OIDC publishing.'
}

$uses = @([regex]::Matches($workflow, '(?m)^\s+uses:\s+([^\s]+)') | ForEach-Object { $_.Groups[1].Value })
if ($uses.Count -eq 0) {
    throw 'Release workflow must contain at least one action.'
}

$workflowActions = @($uses | Sort-Object -Unique -CaseSensitive)
$allowlistedActions = @($allowlistedActions | Sort-Object -Unique -CaseSensitive)
$actionPinDifferences = @(Compare-Object -ReferenceObject $allowlistedActions -DifferenceObject $workflowActions -CaseSensitive)
if ($actionPinDifferences.Count -gt 0) {
    $differences = $actionPinDifferences | ForEach-Object { "$($_.SideIndicator): $($_.InputObject)" }
    throw "Release action pins must exactly match every workflow uses entry. Differences: $($differences -join '; ')"
}

if ($workflow -notmatch 'NuGet/login@[0-9a-f]{40}' -or $workflow -notmatch '(?m)^\s+user:\s+dmitriyzen\s*$') {
    throw 'Release workflow does not use the required NuGet Trusted Publishing login identity.'
}

if ($workflow -match '(?i)secrets\.[^\s}]*NUGET|NUGET_API_KEY\s*:\s*\$\{\{\s*secrets\.') {
    throw 'Release workflow references a long-lived NuGet API-key secret.'
}

foreach ($requiredText in @(
    'check-release-contract.ps1 -Mode Release',
    '--configfile .\NuGet.config --no-cache --force',
    'dotnet build .\KeelMatrix.AuthSurface.sln --configuration Release --no-restore',
    'dotnet test .\KeelMatrix.AuthSurface.sln --configuration Release --no-build --no-restore',
    'dotnet pack .\src\KeelMatrix.AuthSurface\KeelMatrix.AuthSurface.csproj',
    'KeelMatrix.AuthSurface.${env:RELEASE_VERSION}.nupkg',
    'KeelMatrix.AuthSurface.${env:RELEASE_VERSION}.snupkg',
    'inspect-package.ps1',
    'KEELMATRIX_NO_TELEMETRY',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DO_NOT_TRACK'
)) {
    if ($workflow -notlike "*$requiredText*") {
        throw "Release workflow is missing required release-path text: $requiredText"
    }
}

$buildIndex = $workflow.IndexOf('dotnet build')
$testIndex = $workflow.IndexOf('dotnet test')
$packIndex = $workflow.IndexOf('dotnet pack')
if ($buildIndex -lt 0 -or $testIndex -lt $buildIndex -or $packIndex -lt $testIndex) {
    throw 'Release workflow must build and test before packing.'
}

Write-Output "Release workflow contract passed: $workflowPath"
