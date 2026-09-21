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

function Invoke-Gate([string] $Name, [scriptblock] $Action) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n=== $Name ==="
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE" }
    $timer.Stop()
    Write-Host ("{0}: {1:N3}s" -f $Name, $timer.Elapsed.TotalSeconds)
}

Push-Location $root
try {
    if (Test-Path -LiteralPath (Join-Path $root '.github/workflows')) {
        throw 'Private repository validation does not permit workflow files.'
    }

    Invoke-Gate 'controlled restore' { dotnet restore $solution --configfile (Join-Path $root 'NuGet.config') --force-evaluate }
    Invoke-Gate 'format whitespace' { dotnet format whitespace $solution --verify-no-changes --no-restore }
    Invoke-Gate 'format analyzers' { dotnet format analyzers $library --diagnostics RS0016,RS0017,RS0025,RS0037 --verify-no-changes --no-restore }
    Invoke-Gate 'Release build' { dotnet build $solution -c $Configuration --no-restore }
    Invoke-Gate 'unit and integration tests' { dotnet test $solution -c $Configuration --no-build --no-restore --logger 'console;verbosity=minimal' }

    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $packageDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
    Invoke-Gate 'package build' { dotnet pack $library -c $Configuration --no-build --no-restore -o $packageDirectory }
    $nupkg = Get-ChildItem -LiteralPath $packageDirectory -Filter 'KeelMatrix.AuthSurface.*.nupkg' | Where-Object Name -notlike '*.snupkg' | Select-Object -First 1
    if ($null -eq $nupkg) { throw 'No package was produced.' }
    $snupkg = Get-ChildItem -LiteralPath $packageDirectory -Filter 'KeelMatrix.AuthSurface.*.snupkg' | Select-Object -First 1
    if ($null -eq $snupkg) { throw 'No symbol package was produced.' }
    Invoke-Gate 'package inspection' { & (Join-Path $PSScriptRoot 'inspect-package.ps1') -PackagePath $nupkg.FullName -SymbolPackagePath $snupkg.FullName }
    Invoke-Gate 'dependency vulnerability check' { dotnet list $solution package --vulnerable --include-transitive --format json }
    Invoke-Gate 'clean package consumer smoke' { & (Join-Path $PSScriptRoot 'consumer-smoke.ps1') -PackagePath $nupkg.FullName }

    $leaks = & rg.exe -n --hidden --glob '!.git/**' --glob '!**/bin/**' --glob '!**/obj/**' 'Paperclip|Codex|KEE-[0-9]+' $root 2>$null
    if ($LASTEXITCODE -eq 0) {
        $leaks | Write-Output
        throw 'Internal wording was found in product files.'
    }
    Write-Host 'telemetry suppression/privacy checks: passed (suppression variables set; prohibited internal wording absent)'
}
finally {
    Pop-Location
}
