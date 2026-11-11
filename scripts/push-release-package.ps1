[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [uri] $Source = 'https://api.nuget.org/v3/index.json'
)

$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
if ($package.PSIsContainer -or $package.Extension -cne '.nupkg') {
    throw 'Publication requires one explicit .nupkg file.'
}
$symbols = [System.IO.Path]::ChangeExtension($package.FullName, '.snupkg')
if (-not (Test-Path -LiteralPath $symbols -PathType Leaf)) {
    throw 'The matching sibling .snupkg must exist before publication.'
}
if ([string]::IsNullOrWhiteSpace($env:NUGET_PUSH_TOKEN)) {
    throw 'The temporary NUGET_PUSH_TOKEN is required.'
}

if (-not $Source.IsAbsoluteUri -or $Source.AbsolutePath -notlike '*/index.json' -or
    ($Source.Scheme -ne 'https' -and -not ($Source.Scheme -eq 'http' -and $Source.IsLoopback))) {
    throw 'Publication requires an HTTPS v3 service index (HTTP is allowed only for localhost tests).'
}
try {
    $index = Invoke-RestMethod -Uri $Source.AbsoluteUri -TimeoutSec 30 -MaximumRedirection 0
}
catch {
    throw 'Could not discover the v3 publication services. No upload was attempted.'
}
foreach ($type in @('PackagePublish/2.0.0', 'SymbolPackagePublish/4.9.0')) {
    $services = @($index.resources | Where-Object { $_.'@type' -contains $type })
    if ($services.Count -eq 0) {
        throw "The v3 source must advertise $type before any upload."
    }
    foreach ($service in $services) {
        $endpoint = $null
        if ($service.'@id' -isnot [string] -or
            -not [uri]::TryCreate($service.'@id', [UriKind]::Absolute, [ref]$endpoint) -or
            ($endpoint.Scheme -ne 'https' -and -not ($endpoint.Scheme -eq 'http' -and $endpoint.IsLoopback))) {
            throw "The v3 source must advertise a usable $type endpoint before any upload."
        }
    }
}

# Let the v3 index select .snupkg support. A raw --symbol-source selects the legacy
# protocol, looks for .symbols.nupkg, and silently omits the actual .snupkg.
dotnet nuget push $package.FullName --source $Source.AbsoluteUri --api-key $env:NUGET_PUSH_TOKEN --symbol-api-key $env:NUGET_PUSH_TOKEN --timeout 60
if ($LASTEXITCODE -ne 0) { throw "Package and symbol publication failed with exit code $LASTEXITCODE. Preserve partial effects; do not replay the primary upload." }
