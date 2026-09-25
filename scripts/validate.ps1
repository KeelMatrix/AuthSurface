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
    Invoke-Gate 'main changelog/version contract' {
        & (Join-Path $PSScriptRoot 'check-release-contract.ps1') -Mode Main -Version $version
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
    Invoke-Gate 'built-package telemetry contract' { & (Join-Path $PSScriptRoot 'test-telemetry-package.ps1') -PackagePath $nupkg.FullName }
    $vulnerabilityReportPath = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-vulnerabilities-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
        Write-Host "`n=== dependency vulnerability audit ==="
        $auditTimer = [System.Diagnostics.Stopwatch]::StartNew()
        $auditOutput = & dotnet list $solution package --vulnerable --include-transitive --format json 2>&1
        $auditExitCode = $LASTEXITCODE
        if ($auditExitCode -ne 0) {
            throw "Dependency vulnerability audit failed with exit code $auditExitCode."
        }
        $auditOutput | Set-Content -LiteralPath $vulnerabilityReportPath -Encoding utf8
        & (Join-Path $PSScriptRoot 'check-vulnerability-report.ps1') -ReportPath $vulnerabilityReportPath
        $checkerExitCode = $LASTEXITCODE
        if ($checkerExitCode -ne 0) {
            throw "Dependency vulnerability report enforcement failed with exit code $checkerExitCode."
        }
        $auditTimer.Stop()
        Write-Host ("dependency vulnerability audit: {0:N3}s" -f $auditTimer.Elapsed.TotalSeconds)
    }
    finally {
        Remove-Item -LiteralPath $vulnerabilityReportPath -Force -ErrorAction SilentlyContinue
    }
    Invoke-Gate 'clean package consumer smoke' { & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $nupkg.FullName }
    Invoke-Gate 'stale-cache package consumer regression' { & (Join-Path $PSScriptRoot 'test-consumer-smoke.ps1') -PackagePath $nupkg.FullName }
    Invoke-Gate 'vulnerability gate negative test' { & (Join-Path $PSScriptRoot 'test-vulnerability-gate.ps1') }

    Invoke-Gate 'repository hygiene and authorship negative controls' { & (Join-Path $PSScriptRoot 'test-repository-hygiene.ps1') }
    Invoke-Gate 'release-candidate repository hygiene and authorship' { & (Join-Path $PSScriptRoot 'check-repository-hygiene.ps1') }
    Write-Host 'telemetry suppression/privacy checks: passed (suppression variables set; tracked paths/content, complete candidate commit messages, and candidate-history author metadata satisfy the public contract)'
}
finally {
    Pop-Location
}
