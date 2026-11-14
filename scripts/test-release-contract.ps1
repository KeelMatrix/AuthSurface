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
<Project><ItemGroup><PackageVersion Include="KeelMatrix.Telemetry" Version="[0.1.1]" /></ItemGroup></Project>
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

    foreach ($token in @('0.1.0', '[0.1.0]', "'[0.1.0]'", '"[0.1.0]"')) {
        foreach ($path in @('README.md','src/KeelMatrix.AuthSurface/README.md')) {
            Set-Content -LiteralPath (Join-Path $root $path) -Value "dotnet add package KeelMatrix.AuthSurface --version $token --no-restore"
        }
        & $contract -Root $root -Mode Candidate -Version '0.1.0'
    }
    foreach ($token in @('0.1.00','0.1.01','0.1.0-preview.1',"'[0.1.1]'", "'[0.1.0,)'", '0.1.*')) {
        foreach ($path in @('README.md','src/KeelMatrix.AuthSurface/README.md')) {
            Set-Content -LiteralPath (Join-Path $root 'README.md') -Value $install
            Set-Content -LiteralPath (Join-Path $root 'src/KeelMatrix.AuthSurface/README.md') -Value $install
            Set-Content -LiteralPath (Join-Path $root $path) -Value "dotnet add package KeelMatrix.AuthSurface --version $token --no-restore"
            $rejected = $false
            try { & $contract -Root $root -Mode Candidate -Version '0.1.0' }
            catch { $rejected = $_ -like '*install example does not use release version*' }
            if (-not $rejected) { throw "Release version alignment accepted $token in $path." }
        }
    }
    foreach ($path in @('README.md','src/KeelMatrix.AuthSurface/README.md')) { Set-Content -LiteralPath (Join-Path $root $path) -Value $install }

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

    $impossibleDate = $finalized.Replace('2026-09-21', '2026-02-30')
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $impossibleDate -Encoding utf8
    $impossibleDateRejected = $false
    try { & $contract -Root $root -Mode Main -Version '0.1.0' }
    catch { $impossibleDateRejected = $true }
    if (-not $impossibleDateRejected) { throw 'Main mode accepted an impossible calendar date.' }

    $duplicate = $finalized + @'

## [0.1.0] - 2026-09-22

### Added

- A second target entry is never unambiguous.
'@
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $duplicate -Encoding utf8
    $duplicateRejected = $false
    try { & $contract -Root $root -Mode Main -Version '0.1.0' }
    catch { $duplicateRejected = $true }
    if (-not $duplicateRejected) { throw 'Main mode accepted duplicate target-version headings.' }

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

    $firstFixedRejected = $false
    Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $finalized.Replace('### Added', '### Fixed') -Encoding utf8
    try { & $contract -Root $root -Mode Release -Version '0.1.0' -Tag 'v0.1.0' }
    catch { $firstFixedRejected = $true }
    if (-not $firstFixedRejected) { throw 'The first public release accepted a Fixed category.' }

    foreach ($laterVersion in @('0.1.1', '0.2.0', '1.0.0')) {
        Set-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$laterVersion</Version></PropertyGroup></Project>" -Encoding utf8
        foreach ($readmePath in @('README.md', 'src/KeelMatrix.AuthSurface/README.md')) {
            Set-Content -LiteralPath (Join-Path $root $readmePath) -Value $install.Replace('0.1.0', $laterVersion) -Encoding utf8
        }
        $laterCandidate = "# Changelog`n`n## $laterVersion - Unreleased`n`n### Fixed`n`n- Corrects source loading from the preceding published version.`n`n" + $finalized.Substring($finalized.IndexOf('## [0.1.0]'))
        Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $laterCandidate -Encoding utf8
        & $contract -Root $root -Mode Candidate -Version $laterVersion
        & $contract -Root $root -Mode Main -Version $laterVersion
        $laterPlannedRejected = $false
        try { & $contract -Root $root -Mode Release -Version $laterVersion -Tag "v$laterVersion" }
        catch { $laterPlannedRejected = $true }
        if (-not $laterPlannedRejected) { throw "Release mode accepted planned $laterVersion." }

        $laterFinal = $laterCandidate.Replace("## $laterVersion - Unreleased", "## [$laterVersion] - 2026-11-14")
        foreach ($category in @('Added', 'Changed', 'Deprecated', 'Removed', 'Fixed', 'Security')) {
            Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $laterFinal.Replace('### Fixed', "### $category") -Encoding utf8
            & $contract -Root $root -Mode Main -Version $laterVersion
            & $contract -Root $root -Mode Release -Version $laterVersion -Tag "v$laterVersion"
        }
        foreach ($invalidEntry in @(
            $laterFinal.Replace('2026-11-14', '2026-02-30'),
            $laterFinal.Replace('### Fixed', '### Internal'),
            $laterFinal.Replace('### Fixed', ''),
            $laterFinal.Replace('Corrects source', 'Pending release: corrects source'),
            ($laterFinal + "`n## [$laterVersion] - 2026-11-14`n`n### Fixed`n`n- Duplicate entry.")
        )) {
            Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $invalidEntry -Encoding utf8
            foreach ($mode in @('Main', 'Release')) {
                $laterInvalidRejected = $false
                try { & $contract -Root $root -Mode $mode -Version $laterVersion -Tag "v$laterVersion" }
                catch { $laterInvalidRejected = $true }
                if (-not $laterInvalidRejected) { throw "$mode accepted an invalid later-release entry: $invalidEntry" }
            }
        }
        Set-Content -LiteralPath (Join-Path $root 'CHANGELOG.md') -Value $laterFinal -Encoding utf8
        $laterTagRejected = $false
        try { & $contract -Root $root -Mode Release -Version $laterVersion -Tag 'v0.1.0' }
        catch { $laterTagRejected = $true }
        if (-not $laterTagRejected) { throw "Release mode accepted a historical tag for $laterVersion." }
        Set-Content -LiteralPath (Join-Path $root 'README.md') -Value $install -Encoding utf8
        $laterInstallRejected = $false
        try { & $contract -Root $root -Mode Main -Version $laterVersion }
        catch { $laterInstallRejected = $true }
        if (-not $laterInstallRejected) { throw "Main mode accepted a stale install version for $laterVersion." }
    }

    Write-Output 'Release contract tests passed: first-release rules retained; later patch/minor/major categories, candidate/final states, dates, duplicates, unfinished wording, metadata/install and tag mismatch checks.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
