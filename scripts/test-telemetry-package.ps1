[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('authsurface-telemetry-package-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $root 'feed'
$config = Join-Path $root 'NuGet.config'
$probeProject = Join-Path $repositoryRoot 'tests\KeelMatrix.AuthSurface.TelemetryProbe\KeelMatrix.AuthSurface.TelemetryProbe.csproj'
$probeAssembly = Join-Path $repositoryRoot 'tests\KeelMatrix.AuthSurface.TelemetryProbe\bin\Release\net8.0\KeelMatrix.AuthSurface.TelemetryProbe.dll'
$oldPackages = $env:NUGET_PACKAGES
$ciVariables = @(
    'CI', 'GITHUB_ACTIONS', 'TF_BUILD', 'JENKINS_URL', 'BUILDKITE', 'GITLAB_CI',
    'TRAVIS', 'CIRCLECI', 'CODEBUILD_BUILD_ID', 'TEAMCITY_VERSION', 'BITBUCKET_BUILD_NUMBER', 'APPVEYOR'
)
$oldCiValues = @{}
$allowedPayloadFields = @(
    'event', 'tool', 'tool_version', 'telemetry_version', 'schema_version',
    'project_hash', 'installation_hash', 'runtime', 'os', 'ci', 'timestamp'
)
$requiredPayloadFields = @('project_hash', 'installation_hash')

function Invoke-Checked([string] $FileName, [string[]] $Arguments) {
    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FileName failed with exit code $LASTEXITCODE."
    }
}

try {
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    Copy-Item -LiteralPath (Resolve-Path -LiteralPath $PackagePath) -Destination (Join-Path $feed 'KeelMatrix.AuthSurface.0.1.0.nupkg')
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$($feed.Replace('\', '/'))" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="KeelMatrix.AuthSurface" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8

    $env:NUGET_PACKAGES = Join-Path $root 'packages'
    Invoke-Checked 'dotnet' @('restore', $probeProject, '--configfile', $config, '--force-evaluate', '--no-cache', '-p:UseAuthSurfacePackage=true')
    Invoke-Checked 'dotnet' @('build', $probeProject, '-c', 'Release', '--no-restore', '-p:UseAuthSurfacePackage=true')

    foreach ($variable in $ciVariables) {
        $oldCiValues[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process')
        [Environment]::SetEnvironmentVariable($variable, $null, 'Process')
    }

    try {
        foreach ($mode in @('suppressed:KEELMATRIX_NO_TELEMETRY', 'suppressed:DOTNET_CLI_TELEMETRY_OPTOUT', 'suppressed:DO_NOT_TRACK', 'capture')) {
            Write-Output "=== built-package telemetry probe: $mode ==="
            $output = & dotnet $probeAssembly $mode 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
            Write-Output $output.TrimEnd()
            if ($exitCode -ne 0 -or $output -notmatch 'RESULT=VALID;ENDPOINTS=' ) {
                throw "Built-package telemetry probe failed for $mode."
            }

            if ($mode -eq 'capture') {
                $payloadLines = @($output -split "`r?`n" | Where-Object { $_.StartsWith('PAYLOAD=', [StringComparison]::Ordinal) })
                if ($payloadLines.Count -ne 1) {
                    throw 'Built-package positive telemetry control emitted no payload.'
                }

                $payloadText = $payloadLines[0].Substring('PAYLOAD='.Length)
                try {
                    $payload = $payloadText | ConvertFrom-Json
                }
                catch {
                    throw 'Built-package positive telemetry control emitted a payload that is not valid JSON.'
                }

                $payloadProperties = @($payload.PSObject.Properties.Name)
                if ($payloadProperties.Count -eq 0) {
                    throw 'Built-package positive telemetry control emitted an empty payload.'
                }

                $unexpectedFields = @($payloadProperties | Where-Object { $allowedPayloadFields -notcontains $_ })
                if ($unexpectedFields.Count -gt 0) {
                    throw "Built-package telemetry payload contains undocumented fields: $($unexpectedFields -join ', ')."
                }

                foreach ($requiredField in $requiredPayloadFields) {
                    if ($payloadProperties -notcontains $requiredField) {
                        throw "Built-package telemetry payload is missing required field '$requiredField'."
                    }
                }

                $sensitivePattern = '(?i)route|policy|role|scheme|baseline|controller|action|path|claim|credential|diagnostic'
                if ([regex]::IsMatch($payloadText, $sensitivePattern)) {
                    throw 'Built-package telemetry payload contains route, policy, role, scheme, baseline, controller/action, path, claim, credential, or diagnostic data.'
                }
            }
            elseif ($output -notmatch 'NO_PAYLOAD') {
                throw "Built-package telemetry opt-out emitted a payload for $mode."
            }
        }
    }
    finally {
        foreach ($variable in $ciVariables) {
            [Environment]::SetEnvironmentVariable($variable, $oldCiValues[$variable], 'Process')
        }
    }

    Write-Output 'Built-package telemetry contract passed: all three fresh-process opt-outs suppress delivery, the scan/evaluation result remains valid, and the unsuppressed positive control emits a payload.'
}
finally {
    & dotnet restore $probeProject --configfile (Join-Path $repositoryRoot 'NuGet.config') --force-evaluate -p:UseAuthSurfacePackage=false | Out-Null
    if ($null -eq $oldPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $oldPackages }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
