[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$contract = Join-Path $PSScriptRoot 'check-release-contract.ps1'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-release-contract-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $root 'src/KeelMatrix.AuthSurface') -Force | Out-Null

try {
    @'
<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Encoding utf8
    @'
<Project><ItemGroup><PackageVersion Include="KeelMatrix.Telemetry" Version="[0.1.0]" /></ItemGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Encoding utf8
    @'
<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.AuthSurface</PackageId></PropertyGroup></Project>
'@ | Set-Content -LiteralPath (Join-Path $root 'src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj') -Encoding utf8
    $install = "dotnet add package KeelMatrix.AuthSurface --version 0.1.0"
    Set-Content -LiteralPath (Join-Path $root 'README.md') -Value $install -Encoding utf8
    Set-Content -LiteralPath (Join-Path $root 'src/KeelMatrix.AuthSurface/README.md') -Value $install -Encoding utf8
    $planned = @'
# Changelog

## 0.1.0 - Pre-release

Initial pre-release implementation.
'@
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $planned -Encoding utf8

    & $contract -Root $root -Mode Candidate -Version '0.1.0'

    $plannedReleaseRejected = $false
    try { & $contract -Root $root -Mode Release -Version '0.1.0' -Tag 'v0.1.0' }
    catch { $plannedReleaseRejected = $true }
    if (-not $plannedReleaseRejected) { throw 'Release mode accepted a planned changelog entry.' }

    $finalized = @'
# Changelog

## [Unreleased]

## [0.1.0] - 2026-09-21

### Added

- Provides deterministic runtime authorization-surface checks.
'@
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $finalized -Encoding utf8
    & $contract -Root $root -Mode Main -Version '0.1.0'
    & $contract -Root $root -Mode Release -Version '0.1.0' -Tag 'v0.1.0'

    $unfinishedFinalizedHeading = $finalized.Replace('### Added', '### Added').Replace('Provides deterministic', 'Pending release: provides deterministic')
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $unfinishedFinalizedHeading -Encoding utf8
    $unfinishedFinalizedRejected = $false
    try { & $contract -Root $root -Mode Main -Version '0.1.0' }
    catch { $unfinishedFinalizedRejected = $true }
    if (-not $unfinishedFinalizedRejected) { throw 'Main mode accepted a finalized heading with unfinished wording.' }

    $versionMismatchRejected = $false
    try { & $contract -Root $root -Mode Main -Version '0.2.0' }
    catch { $versionMismatchRejected = $true }
    if (-not $versionMismatchRejected) { throw 'Main mode accepted a version mismatch.' }

    $mismatchRejected = $false
    try { & $contract -Root $root -Mode Release -Version '0.1.0' -Tag 'v0.2.0' }
    catch { $mismatchRejected = $true }
    if (-not $mismatchRejected) { throw 'Release mode accepted a mismatched tag.' }

    $remediation = $finalized.Replace('Provides deterministic', 'Now provides deterministic')
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $remediation -Encoding utf8
    $remediationRejected = $false
    try { & $contract -Root $root -Mode Release -Version '0.1.0' -Tag 'v0.1.0' }
    catch { $remediationRejected = $true }
    if (-not $remediationRejected) { throw 'Release mode accepted first-release remediation wording.' }

    Write-Output 'Release contract tests passed: candidate, planned rejection, finalized main/release, unfinished finalized rejection, tag mismatch, version mismatch, and remediation rejection.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
