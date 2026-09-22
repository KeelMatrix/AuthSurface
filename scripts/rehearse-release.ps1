[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('Candidate', 'Main')]
    [string] $ContractMode = 'Main',

    [string] $Version = '0.1.0',

    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$env:KEELMATRIX_NO_TELEMETRY = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DO_NOT_TRACK = '1'

$root = (Resolve-Path -LiteralPath $Root).Path
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
    Invoke-Native 'release contract' {
        & '.\scripts\check-release-contract.ps1' -Mode $ContractMode -Version $Version
    }
    Invoke-Native 'release-equivalent restore' {
        dotnet restore $solution --configfile '.\NuGet.config' --no-cache --force
    }
    Invoke-Native 'release-equivalent build' {
        dotnet build $solution --configuration $Configuration --no-restore -p:Version=$Version -p:PackageVersion=$Version
    }
    Invoke-Native 'release-equivalent test' {
        dotnet test $solution --configuration $Configuration --no-build --no-restore --logger 'console;verbosity=minimal'
    }

    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Get-ChildItem -LiteralPath $output -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Invoke-Native 'release-equivalent pack' {
        dotnet pack $library --configuration $Configuration --no-build --no-restore --include-symbols --p:SymbolPackageFormat=snupkg -p:Version=$Version -p:PackageVersion=$Version --output $output
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
        & '.\scripts\inspect-package.ps1' -PackagePath $nupkg -SymbolPackagePath $snupkg -ExpectedVersion $Version
    }
    Write-Output "Release rehearsal passed without authentication or publication. Artifacts: $nupkg, $snupkg"
}
finally {
    Pop-Location
}
