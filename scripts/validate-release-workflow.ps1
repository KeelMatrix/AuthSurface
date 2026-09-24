[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $Root).Path
$workflowDirectory = Join-Path $root '.github/workflows'
if (-not (Test-Path -LiteralPath $workflowDirectory -PathType Container)) {
    throw "Workflow directory is missing: $workflowDirectory"
}

$workflowPaths = @(Get-ChildItem -LiteralPath $workflowDirectory -File | Where-Object { $_.Extension -in @('.yml', '.yaml') })
if ($workflowPaths.Count -eq 0) {
    throw "No workflow files were found in: $workflowDirectory"
}

$releaseWorkflowPath = Join-Path $workflowDirectory 'release.yml'
if (-not (Test-Path -LiteralPath $releaseWorkflowPath -PathType Leaf)) {
    throw "Release workflow is missing: $releaseWorkflowPath"
}

$ciWorkflowPath = Join-Path $workflowDirectory 'ci.yml'
if (-not (Test-Path -LiteralPath $ciWorkflowPath -PathType Leaf)) {
    throw "CI workflow is missing: $ciWorkflowPath"
}

$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw
$ciWorkflow = Get-Content -LiteralPath $ciWorkflowPath -Raw

if ($ciWorkflow -notmatch '(?ms)^on:\s*\r?\n\s+push:\s*\r?\n\s+branches:\s*\r?\n\s+-\s+main\s*\r?\n\s+pull_request:\s*(?:\r?\n|$)') {
    throw 'CI workflow must trigger on pushes to main and pull requests.'
}

foreach ($forbiddenCiTrigger in @('pull_request_target', 'schedule', 'workflow_dispatch', 'tags:', 'paths:')) {
    if ($ciWorkflow -match "(?m)^\s+$([regex]::Escape($forbiddenCiTrigger))") {
        throw "CI workflow contains a forbidden trigger or filter: $forbiddenCiTrigger"
    }
}

foreach ($requiredPlatform in @('windows-latest', 'ubuntu-latest', 'macos-latest')) {
    if ($ciWorkflow -notmatch "(?m)^\s+-\s+$([regex]::Escape($requiredPlatform))\s*$") {
        throw "CI workflow is missing required matrix platform: $requiredPlatform"
    }
}

if ($ciWorkflow -notmatch '(?ms)^permissions:\s*\r?\n\s+contents:\s+read\s*(?:\r?\n|$)' -or
    $ciWorkflow -match '(?mi)^\s+(?:id-token|packages|actions|pull-requests):\s+\S+' -or
    $ciWorkflow -match '(?mi)^\s+contents:\s+write\s*$') {
    throw 'CI workflow permissions must be limited to contents: read.'
}

if ($ciWorkflow -notmatch '(?mi)^\s+timeout-minutes:\s+[1-9]\d*\s*$') {
    throw 'CI workflow must define a bounded timeout.'
}

foreach ($requiredCiText in @(
    'KEELMATRIX_NO_TELEMETRY',
    'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DO_NOT_TRACK',
    'scripts/validate-release-workflow.ps1',
    'scripts/validate.ps1'
)) {
    if ($ciWorkflow -notlike "*$requiredCiText*") {
        throw "CI workflow is missing required validation text: $requiredCiText"
    }
}

if ($releaseWorkflow -notmatch '(?ms)^on:\s*\r?\n\s+push:\s*\r?\n\s+tags:') {
    throw 'Release workflow must use a tag-only push trigger.'
}

foreach ($forbiddenTrigger in @('pull_request', 'schedule', 'workflow_dispatch', 'branches:')) {
    if ($releaseWorkflow -match "(?m)^\s+$([regex]::Escape($forbiddenTrigger))") {
        throw "Release workflow contains a forbidden trigger or branch filter: $forbiddenTrigger"
    }
}

if ($releaseWorkflow -notmatch '(?m)^\s+-\s+''v\*''\s*$') {
    throw 'Release workflow must accept version tags through the tag trigger and validate the exact format in the job.'
}

$actionPinsPath = Join-Path $root 'scripts/release-action-pins.txt'
if (-not (Test-Path -LiteralPath $actionPinsPath -PathType Leaf)) {
    throw "Release action pin allowlist is missing: $actionPinsPath"
}

$allowlistedActions = @(Get-Content -LiteralPath $actionPinsPath | Where-Object { $_ -ne '' })
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

if ($releaseWorkflow -notmatch '(?m)^\s+id-token:\s+write\s*$' -or
    $releaseWorkflow -match '(?m)^\s+contents:\s+write\s*$') {
    throw 'Release workflow permissions are not least privilege for OIDC publishing.'
}

$actionPinPattern = '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@[0-9a-f]{40}$'
$workflowActions = @()
foreach ($workflowPath in $workflowPaths) {
    $workflow = Get-Content -LiteralPath $workflowPath.FullName -Raw
    $uses = @([regex]::Matches($workflow, '(?m)^\s+uses:\s+([^\s]+)') | ForEach-Object { $_.Groups[1].Value })
    if ($uses.Count -eq 0) {
        throw "Workflow must contain at least one action: $($workflowPath.Name)"
    }

    foreach ($use in $uses) {
        if ($use -notmatch $actionPinPattern) {
            throw "Workflow action is not pinned to a full commit SHA: $($workflowPath.Name): $use"
        }
    }

    $workflowActions += $uses
}

$workflowActions = @($workflowActions | Sort-Object -Unique -CaseSensitive)
$allowlistedActions = @($allowlistedActions | Sort-Object -Unique -CaseSensitive)
$actionPinDifferences = @(Compare-Object -ReferenceObject $allowlistedActions -DifferenceObject $workflowActions -CaseSensitive)
if ($actionPinDifferences.Count -gt 0) {
    $differences = $actionPinDifferences | ForEach-Object { "$($_.SideIndicator): $($_.InputObject)" }
    throw "Action pins must exactly match every workflow uses entry. Differences: $($differences -join '; ')"
}

if ($releaseWorkflow -notmatch 'NuGet/login@[0-9a-f]{40}' -or $releaseWorkflow -notmatch '(?m)^\s+user:\s+dmitriyzen\s*$') {
    throw 'Release workflow does not use the required NuGet Trusted Publishing login identity.'
}

if ($releaseWorkflow -match '(?i)secrets\.[^\s}]*NUGET|NUGET_API_KEY\s*:\s*\$\{\{\s*secrets\.') {
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
    if ($releaseWorkflow -notlike "*$requiredText*") {
        throw "Release workflow is missing required release-path text: $requiredText"
    }
}

$buildIndex = $releaseWorkflow.IndexOf('dotnet build')
$testIndex = $releaseWorkflow.IndexOf('dotnet test')
$packIndex = $releaseWorkflow.IndexOf('dotnet pack')
if ($buildIndex -lt 0 -or $testIndex -lt $buildIndex -or $packIndex -lt $testIndex) {
    throw 'Release workflow must build and test before packing.'
}

function Assert-CommitObject([string] $Action) {
    $parts = $Action.Split('@', 2)
    $repository = $parts[0]
    $sha = $parts[1]
    $probeRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-action-pin-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $probeRoot -Force | Out-Null
    $oldPrompt = $env:GIT_TERMINAL_PROMPT
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        & git init --bare --quiet $probeRoot 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not initialize action pin probe repository for $Action." }

        & git -C $probeRoot fetch --no-tags --depth=1 "https://github.com/$repository.git" $sha 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Action pin does not resolve in its repository: $Action." }

        $objectType = (& git -C $probeRoot cat-file -t $sha 2>$null).Trim()
        if ($LASTEXITCODE -ne 0 -or $objectType -ne 'commit') {
            throw "Action pin must resolve to a commit object, not '$objectType': $Action."
        }
    }
    finally {
        if ($null -eq $oldPrompt) { Remove-Item Env:GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue }
        else { $env:GIT_TERMINAL_PROMPT = $oldPrompt }
        Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

 $actionMetadataCache = @{}
function Get-ActionMetadata([string] $Action) {
    if ($actionMetadataCache.ContainsKey($Action)) {
        return $actionMetadataCache[$Action]
    }

    $parts = $Action.Split('@', 2)
    $repository = $parts[0]
    $sha = $parts[1]
    $metadata = $null
    foreach ($metadataFile in @('action.yml', 'action.yaml')) {
        $uri = "https://raw.githubusercontent.com/$repository/$sha/$metadataFile"
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $uri
            if ($response.StatusCode -eq 200) {
                $metadata = $response.Content
                break
            }
        }
        catch {
            if ($metadataFile -eq 'action.yaml') {
                throw "Could not read pinned action metadata for $Action."
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($metadata)) {
        throw "Pinned action metadata is missing for $Action."
    }

    $actionMetadataCache[$Action] = $metadata
    return $metadata
}

function Assert-SupportedActionRuntime([string] $Action) {
    $metadata = Get-ActionMetadata $Action
    $usingMatch = [regex]::Match(
        $metadata,
        '(?im)^[ \t]*using:[ \t]*[\x27\x22]?(?<runtime>node\d+|composite|docker)[\x27\x22]?[ \t]*(?:#.*)?\r?$')
    if (-not $usingMatch.Success) {
        throw "Pinned action metadata does not declare a supported runtime: $Action."
    }

    $runtime = $usingMatch.Groups['runtime'].Value.ToLowerInvariant()
    if ($runtime -like 'node*') {
        $version = [int]$runtime.Substring(4)
        if ($version -lt 24) {
            throw "Pinned JavaScript action uses deprecated $runtime runtime; Node 24 or newer is required: $Action."
        }
    }
}

foreach ($action in $workflowActions) {
    Assert-CommitObject $action
    Assert-SupportedActionRuntime $action
}

Write-Output "Workflow action pin contract passed: $($workflowPaths.Count) workflow files, $($workflowActions.Count) unique actions; every pin resolves to a commit object and meets the Node runtime floor."
