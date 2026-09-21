[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DO_NOT_TRACK = '1'

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'KeelMatrix.AuthSurface.sln'
$library = Join-Path $root 'src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj'
$packageDirectory = Join-Path $root 'artifacts/package'
$version = '0.1.0'

function Invoke-Gate([string] $Name, [scriptblock] $Action) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n=== $Name ==="
    & $Action
    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    $timer.Stop()
    Write-Host ("{0}: {1:N3}s" -f $Name, $timer.Elapsed.TotalSeconds)
}

Push-Location $root
try {
    Invoke-Gate 'release workflow contract' { & (Join-Path $PSScriptRoot 'validate-release-workflow.ps1') }
    Invoke-Gate 'release workflow validator mutation tests' { & (Join-Path $PSScriptRoot 'test-release-workflow.ps1') }
    Invoke-Gate 'release contract tests' { & (Join-Path $PSScriptRoot 'test-release-contract.ps1') }
    Invoke-Gate 'pre-release changelog/version contract' {
        & (Join-Path $PSScriptRoot 'check-release-contract.ps1') -Mode Candidate -Version $version
    }

    Invoke-Gate 'controlled restore' { dotnet restore $solution --configfile (Join-Path $root 'NuGet.config') --force-evaluate }
    Invoke-Gate 'format whitespace' { dotnet format whitespace $solution --verify-no-changes --no-restore }
    Invoke-Gate 'format analyzers' { dotnet format analyzers $library --diagnostics RS0016,RS0017,RS0025,RS0037 --verify-no-changes --no-restore }
    Invoke-Gate 'Release build' { dotnet build $solution -c $Configuration --no-restore }
    Invoke-Gate 'unit and integration tests' { dotnet test $solution -c $Configuration --no-build --no-restore --logger 'console;verbosity=minimal' }

    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $packageDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Invoke-Gate 'package build' { dotnet pack $library -c $Configuration --no-build --no-restore -o $packageDirectory }
    $expectedArtifacts = @(
        "KeelMatrix.AuthSurface.$version.nupkg",
        "KeelMatrix.AuthSurface.$version.snupkg"
    )
    $actualArtifacts = @(Get-ChildItem -LiteralPath $packageDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
    if (@(Compare-Object -ReferenceObject $expectedArtifacts -DifferenceObject $actualArtifacts).Count -gt 0) {
        throw "Unexpected package artifact set. Expected: $($expectedArtifacts -join ', '); actual: $($actualArtifacts -join ', ')"
    }
    $nupkg = Get-Item -LiteralPath (Join-Path $packageDirectory $expectedArtifacts[0])
    $snupkg = Get-Item -LiteralPath (Join-Path $packageDirectory $expectedArtifacts[1])
    Invoke-Gate 'package inspection' {
        & (Join-Path $PSScriptRoot 'inspect-package.ps1') -PackagePath $nupkg.FullName -SymbolPackagePath $snupkg.FullName -ExpectedVersion $version
    }
    Invoke-Gate 'dependency vulnerability check' { dotnet list $solution package --vulnerable --include-transitive --format json }
    Invoke-Gate 'clean package consumer smoke' { & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $nupkg.FullName }

    $forbiddenPattern = 'Paper' + 'clip|Cod' + 'ex|KEE-' + '[0-9]+'
    $validatorPath = 'scripts/validate.ps1'
    $rgPath = Get-Command rg -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source
    $trackedFiles = @(git -C $root ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked product files.' }
    $leaks = foreach ($relativePath in $trackedFiles) {
        $normalizedPath = $relativePath.Replace('\', '/')
        if ($normalizedPath -eq $validatorPath) { continue }

        $fullPath = Join-Path $root $relativePath
        if ($null -ne $rgPath) {
            & $rgPath -n --no-heading --no-messages $forbiddenPattern -- $fullPath 2>$null
        }
        else {
            Select-String -LiteralPath $fullPath -Pattern $forbiddenPattern -AllMatches -ErrorAction SilentlyContinue |
                ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line)" }
        }
    }
    if ($leaks.Count -gt 0) {
        $leaks | Write-Output
        throw 'Internal wording was found in product files.'
    }
    Write-Host 'telemetry suppression/privacy checks: passed (suppression variables set; prohibited internal wording absent)'
}
finally {
    Pop-Location
}
