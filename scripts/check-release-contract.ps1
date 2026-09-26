[CmdletBinding()]
param(
    [ValidateSet('Candidate', 'Main', 'Release')]
    [string] $Mode = 'Candidate',

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $Tag,

    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Read-RequiredFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release-contract file is missing: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw
}

if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw "Invalid release version '$Version'. Expected X.Y.Z."
}

if ($Mode -eq 'Release' -and $Tag -ne "v$Version") {
    throw "Release tag '$Tag' does not match version '$Version'."
}

$buildPropsPath = Join-Path $Root 'Directory.Build.props'
$packagesPropsPath = Join-Path $Root 'Directory.Packages.props'
$projectPath = Join-Path $Root 'src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj'
$changelogPath = Join-Path $Root 'CHANGELOG.md'
$rootReadmePath = Join-Path $Root 'README.md'
$packageReadmePath = Join-Path $Root 'src/KeelMatrix.AuthSurface/README.md'

[xml]$buildProps = Read-RequiredFile $buildPropsPath
[xml]$packagesProps = Read-RequiredFile $packagesPropsPath
[xml]$project = Read-RequiredFile $projectPath
$changelog = Read-RequiredFile $changelogPath
$rootReadme = Read-RequiredFile $rootReadmePath
$packageReadme = Read-RequiredFile $packageReadmePath

$declaredVersion = [string]$buildProps.Project.PropertyGroup.Version
if ($declaredVersion -ne $Version) {
    throw "Directory.Build.props declares version '$declaredVersion', expected '$Version'."
}

$packageMetadata = @($project.Project.PropertyGroup | Where-Object { $_.PackageId -or $_.TargetFramework }) | Select-Object -First 1
if ([string]$packageMetadata.PackageId -ne 'KeelMatrix.AuthSurface' -or
    [string]$packageMetadata.TargetFramework -ne 'net8.0') {
    throw 'Shipping project identity or target framework is inconsistent with the release contract.'
}

$telemetryVersion = @($packagesProps.Project.ItemGroup.PackageVersion | Where-Object Include -eq 'KeelMatrix.Telemetry' | Select-Object -First 1)
if ($null -eq $telemetryVersion -or [string]$telemetryVersion.Version -ne '[0.1.1]') {
    throw 'KeelMatrix.Telemetry must remain the exact [0.1.1] package dependency.'
}

foreach ($readme in @($rootReadme, $packageReadme)) {
    if ($readme -notmatch [regex]::Escape("dotnet add package KeelMatrix.AuthSurface --version $Version")) {
        throw "An install example does not use release version '$Version'."
    }
}

$headingPattern = "(?m)^##\s+\[?$([regex]::Escape($Version))\]?\b.*$"
$headings = @([regex]::Matches($changelog, $headingPattern))
if ($headings.Count -eq 0) {
    throw "CHANGELOG.md has no entry for version '$Version'."
}
if ($headings.Count -ne 1) {
    throw "CHANGELOG.md must contain exactly one entry for version '$Version'; found $($headings.Count)."
}
$heading = $headings[0]

$nextHeading = [regex]::Match($changelog.Substring($heading.Index + $heading.Length), '(?m)^##\s+')
$entryLength = if ($nextHeading.Success) { $nextHeading.Index } else { $changelog.Length - ($heading.Index + $heading.Length) }
$entry = $changelog.Substring($heading.Index, $heading.Length + $entryLength)

function Assert-FinalizedEntry([string] $HeadingValue, [string] $EntryText, [string] $ReleaseVersion) {
    $dateMatch = [regex]::Match($HeadingValue, "^##\s+\[$([regex]::Escape($ReleaseVersion))\]\s+-\s+(?<date>\d{4}-\d{2}-\d{2})\s*$")
    if (-not $dateMatch.Success) {
        throw "Release changelog entry must use '## [$ReleaseVersion] - YYYY-MM-DD'."
    }

    [datetime]$releaseDate = [datetime]::MinValue
    if (-not [datetime]::TryParseExact(
        $dateMatch.Groups['date'].Value,
        'yyyy-MM-dd',
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::None,
        [ref]$releaseDate)) {
        throw "Release changelog entry for '$ReleaseVersion' has an impossible calendar date."
    }

    if ($EntryText -match '(?i)\b(?:unreleased|planned|pre-release|pre release|not yet published|tbd|pending)\b') {
        throw "Release changelog entry for '$ReleaseVersion' still contains pre-release wording."
    }

    $categories = @([regex]::Matches($EntryText, '(?m)^###\s+(.+?)\s*$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    if ($categories.Count -ne 1 -or $categories[0] -ne 'Added') {
        throw "The first public release changelog entry must contain only an Added section."
    }

    if ($EntryText -match '(?i)\b(?:now|no longer|previously|formerly|used to|fixed|fixes|corrected|resolved|addressed|this removes|this fixes|changed from)\b') {
        throw 'The first public release changelog entry contains pre-release remediation wording.'
    }
}

if ($Mode -eq 'Candidate') {
    if ($entry -notmatch '(?i)\b(?:unreleased|planned|pre-release|pre release|not yet published|tbd|pending)\b') {
        throw "Candidate changelog entry for '$Version' is not clearly pre-release."
    }

    Write-Output "Candidate changelog contract passed for $($Version): target remains pre-release."
    exit 0
}

if ($Mode -eq 'Main') {
    $hasFinalizedHeading = $heading.Value -match "^##\s+\[$([regex]::Escape($Version))\]\s+-"
    if (-not $hasFinalizedHeading -and
        $entry -match '(?i)\b(?:unreleased|planned|pre-release|pre release|not yet published|tbd|pending)\b') {
        Write-Output "Main changelog contract passed for $($Version): target remains pre-release."
        exit 0
    }

    Assert-FinalizedEntry $heading.Value $entry $Version
    Write-Output "Main changelog contract passed for $($Version): finalized entry is consistent."
    exit 0
}

Assert-FinalizedEntry $heading.Value $entry $Version
Write-Output "Release changelog contract passed for $($Version)."
