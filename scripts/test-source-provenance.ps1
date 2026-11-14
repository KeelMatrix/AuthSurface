[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PackagePath,
    [Parameter(Mandatory = $true)][string] $SymbolPackagePath,
    [Parameter(Mandatory = $true)][string] $ExpectedCommit,
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'build/Invoke-NestedPwsh.ps1')
$scratch = if ($env:AUTHSURFACE_TEST_SCRATCH) { $env:AUTHSURFACE_TEST_SCRATCH } else { [IO.Path]::GetTempPath() }
$testRoot = Join-Path $scratch ('authsurface-source-callers-' + [guid]::NewGuid().ToString('N'))
$originalVariables = @{}
foreach ($name in @('AUTHSURFACE_SOURCE_ROOT', 'AUTHSURFACE_SOURCE_PRIMARY', 'AUTHSURFACE_SOURCE_SYMBOLS', 'AUTHSURFACE_SOURCE_COMMIT', 'AUTHSURFACE_SOURCE_CONFIGURATION', 'AUTHSURFACE_SOURCE_DOTNET')) {
    $originalVariables[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    New-Item -ItemType Directory -Path (Join-Path $testRoot 'scripts'), (Join-Path $testRoot 'build') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'Directory.Build.props') -Destination $testRoot
    $env:AUTHSURFACE_SOURCE_ROOT = $root
    $env:AUTHSURFACE_SOURCE_PRIMARY = (Resolve-Path -LiteralPath $PackagePath).Path
    $env:AUTHSURFACE_SOURCE_SYMBOLS = (Resolve-Path -LiteralPath $SymbolPackagePath).Path
    $env:AUTHSURFACE_SOURCE_COMMIT = $ExpectedCommit
    $env:AUTHSURFACE_SOURCE_CONFIGURATION = $Configuration
    $env:AUTHSURFACE_SOURCE_DOTNET = (Get-Command dotnet -CommandType Application | Select-Object -First 1).Source
    foreach ($name in @('validate.ps1', 'rehearse-release.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $testRoot "scripts/$name")
    }
    # Earlier gates are outside this process test. The real inspector and its final native gate run below.
    foreach ($name in @('check-website-metadata.ps1', 'validate-release-workflow.ps1', 'test-release-workflow.ps1', 'test-action-pin-requests.ps1', 'test-release-contract.ps1', 'check-release-contract.ps1', 'test-website-metadata.ps1')) {
        Set-Content -LiteralPath (Join-Path $testRoot "scripts/$name") -Value '$global:LASTEXITCODE = 0'
    }
    Set-Content -LiteralPath (Join-Path $testRoot 'build/Test-NestedPwshLaunch.ps1') -Value '$global:LASTEXITCODE = 0'
    Set-Content -LiteralPath (Join-Path $testRoot 'scripts/inspect-package.ps1') -Value @'
& (Join-Path $env:AUTHSURFACE_SOURCE_ROOT 'scripts/inspect-package.ps1') -PackagePath $env:AUTHSURFACE_SOURCE_PRIMARY -SymbolPackagePath $env:AUTHSURFACE_SOURCE_SYMBOLS -RepositoryRoot $env:AUTHSURFACE_SOURCE_ROOT -ExpectedCommit $env:AUTHSURFACE_SOURCE_COMMIT -VerifierPath (Join-Path $env:AUTHSURFACE_SOURCE_ROOT "build/SourceProvenance/bin/$env:AUTHSURFACE_SOURCE_CONFIGURATION/net8.0/SourceProvenance.dll")
'@
    $childPath = Join-Path $testRoot 'child.ps1'
    $count = 0
    foreach ($mode in @('native', 'throw', 'actual')) {
        foreach ($caller in @('inspect-package.ps1', 'validate.ps1', 'rehearse-release.ps1', 'release-step')) {
            $child = @'
$ErrorActionPreference = 'Stop'
function git {
    if ($args[0] -eq 'rev-parse') { $global:LASTEXITCODE = 0; return $env:AUTHSURFACE_SOURCE_COMMIT }
    & (Get-Command git -CommandType Application | Select-Object -First 1).Source @args
    $global:LASTEXITCODE = $LASTEXITCODE
}
function dotnet {
    if ($args[0] -like '*SourceProvenance.dll') {
        if ($mode -eq 'throw') { throw 'source-provenance-script-error' }
        if ($mode -eq 'native') { & $env:AUTHSURFACE_SOURCE_DOTNET --definitely-invalid-source-provenance-test; $global:LASTEXITCODE = $LASTEXITCODE; return }
        # Invoke the actual verifier on the right pair with a deliberately wrong expected commit.
        & $env:AUTHSURFACE_SOURCE_DOTNET $args[0] $args[1] $args[2] $args[3] ('0' * 40)
        $global:LASTEXITCODE = $LASTEXITCODE
        return
    }
    if ($args[0] -eq 'msbuild') { & $env:AUTHSURFACE_SOURCE_DOTNET @args; $global:LASTEXITCODE = $LASTEXITCODE; return }
    if ($args[0] -eq 'pack') {
        $output = if ($args -contains '-o') { $args[[array]::IndexOf($args, '-o') + 1] } else { $args[[array]::IndexOf($args, '--output') + 1] }
        Copy-Item -LiteralPath $env:AUTHSURFACE_SOURCE_PRIMARY, $env:AUTHSURFACE_SOURCE_SYMBOLS -Destination $output
    }
    $global:LASTEXITCODE = 0
}
'@
            $child += "`n`$mode = '$mode'`n"
            if ($caller -eq 'release-step') {
                # Execute the actual committed release preparation block, substituting workflow expressions only.
                $workflow = Get-Content -Raw -LiteralPath (Join-Path $root '.github/workflows/release.yml')
                $step = [regex]::Match($workflow, '(?ms)^      - name: Validate exact package set and contents\r?\n.*?^        run: \|\r?\n(?<code>.*?)(?=^      - name:)').Groups['code'].Value
                if (-not $step) { throw 'Release preparation block not found.' }
                $step = [regex]::Replace($step, '(?m)^          ', '')
                $step = $step.Replace("'`${{ github.sha }}'", '$env:AUTHSURFACE_SOURCE_COMMIT')
                $child += @'
$env:GITHUB_WORKSPACE = $PSScriptRoot
$env:RELEASE_VERSION = ([xml](Get-Content -LiteralPath (Join-Path $env:AUTHSURFACE_SOURCE_ROOT 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$output = Join-Path $PSScriptRoot 'artifacts/packages'
New-Item -ItemType Directory -Path $output -Force | Out-Null
Copy-Item -LiteralPath $env:AUTHSURFACE_SOURCE_PRIMARY, $env:AUTHSURFACE_SOURCE_SYMBOLS -Destination $output
Set-Location $PSScriptRoot
'@ + "`n" + $step
            }
            else {
                $child += "& (Join-Path `$PSScriptRoot 'scripts/$caller')`n"
            }
            $child += "`nWrite-Output 'UNREACHABLE_SUCCESS_LOGIN_PUSH'`nexit 0`n"
            Set-Content -LiteralPath $childPath -Value $child -Encoding utf8
            $output = Invoke-NestedPwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $childPath) 2>&1
            $code = $LASTEXITCODE
            $text = $output -join "`n"
            $expected = if ($mode -eq 'throw') { 'source-provenance-script-error' } else { 'Packaged source provenance failed' }
            if ($code -eq 0 -or $text -notlike "*$expected*" -or $text -like '*UNREACHABLE_SUCCESS_LOGIN_PUSH*' -or $text -like '*Packaged source provenance passed*') {
                throw "Source failure escaped $caller ($mode, exit $code): $text"
            }
            if (Test-Path -LiteralPath (Join-Path $testRoot 'artifacts/packages/SHA256SUMS')) { throw 'Release preparation reached the hash/retention prerequisite.' }
            Write-Output "Source provenance: $caller / $mode rejected, exit $code; later success/login/push unreachable."
            $count++
        }
    }
    Write-Output "Source provenance caller regressions passed: $count actual child-process failures."
}
finally {
    foreach ($name in $originalVariables.Keys) { [Environment]::SetEnvironmentVariable($name, $originalVariables[$name]) }
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($scratch).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source fixture cleanup escaped scratch.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
}
$global:LASTEXITCODE = 0
