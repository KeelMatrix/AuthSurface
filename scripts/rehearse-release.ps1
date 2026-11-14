[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('Candidate', 'Main')]
    [string] $ContractMode = 'Main',

    [string] $Version,

    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $Root).Path
. (Join-Path $root 'build/Invoke-PublicPackageSession.ps1')
if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
}
$output = Join-Path $root 'artifacts/release-rehearsal'
$solution = '.\KeelMatrix.AuthSurface.sln'
$library = '.\src\KeelMatrix.AuthSurface\KeelMatrix.AuthSurface.csproj'

function Invoke-Native([string] $Name, [scriptblock] $Action) {
    Write-Output "=== $Name ==="
    & $Action
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode."
    }
    Write-Output "$Name passed (exit code 0)."
}

Push-Location $root
try {
    $expectedCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot determine expected source commit.' }
    Invoke-Native 'release contract' {
        & '.\scripts\check-release-contract.ps1' -Mode $ContractMode -Version $Version
    }
    Invoke-PublicPackageSession -Root $root -Solution (Join-Path $root $solution) -OutputDirectory $output -ExpectedCommit $expectedCommit -Version $Version -Configuration $Configuration -Action {
    param($session)
    $root = $session.RepositoryRoot
    $solution = $session.Solution
    $library = Join-Path $root 'src/KeelMatrix.AuthSurface/KeelMatrix.AuthSurface.csproj'
    $output = $session.Output
    $buildProperties = $session.Properties
    Invoke-Native 'release-equivalent build' {
        dotnet build $solution --configuration $Configuration --no-restore --no-incremental @buildProperties
    }
    Invoke-Native 'release-equivalent test' {
        dotnet test $solution --configuration $Configuration --no-build --no-restore @buildProperties --logger 'console;verbosity=minimal'
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-Native 'release-equivalent pack' {
        dotnet pack $library --configuration $Configuration --no-build --no-restore @buildProperties --include-symbols --output $output
    }

    $nupkg = Join-Path $output "KeelMatrix.AuthSurface.$Version.nupkg"
    $snupkg = Join-Path $output "KeelMatrix.AuthSurface.$Version.snupkg"
    $actual = @(Get-ChildItem -LiteralPath $output -File | Select-Object -ExpandProperty Name | Sort-Object)
    $expected = @(
        "KeelMatrix.AuthSurface.$Version.nupkg",
        "KeelMatrix.AuthSurface.$Version.snupkg"
    )
    if (@(Compare-Object -ReferenceObject $expected -DifferenceObject $actual).Count -gt 0) {
        throw "Release rehearsal produced an unexpected artifact set: $($actual -join ', ')"
    }

    Invoke-Native 'release-equivalent package inspection' {
        & '.\scripts\inspect-package.ps1' -PackagePath $nupkg -SymbolPackagePath $snupkg -ExpectedVersion $Version -RepositoryRoot $root -ExpectedCommit $expectedCommit -VerifierPath (Join-Path $root "build/SourceProvenance/bin/$Configuration/net8.0/SourceProvenance.dll")
    }
    Set-PublicPackageInspection $session
    }
    Write-Output "Release rehearsal passed without authentication or publication. Artifacts: $(Join-Path $output "KeelMatrix.AuthSurface.$Version.nupkg"), $(Join-Path $output "KeelMatrix.AuthSurface.$Version.snupkg")"
}
finally {
    Pop-Location
}
