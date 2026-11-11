[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('Prepare', 'Submit')] [string] $Mode,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [string] $ContractPath = (Join-Path $PSScriptRoot '../release-assets/0.1.0/symbol-recovery.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$contractBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ContractPath).Path)
$contract = [Text.Encoding]::UTF8.GetString($contractBytes) | ConvertFrom-Json -AsHashtable
function Get-Hash([byte[]] $Bytes) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}
function Assert-Equal($Actual, $Expected, [string] $Name) {
    if ($Actual -isnot [string] -or $Actual -cne $Expected) { throw "Recovery $Name mismatch. No upload was attempted." }
}
function Write-Json([string] $Name, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory $Name) -Encoding utf8
}
function Assert-WebUri([string] $Value) {
    $uri = $null
    if (-not [uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or
        ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback)) -or
        $uri.UserInfo -ne '' -or $uri.Fragment -ne '' -or $uri.Query -ne '') {
        throw 'Recovery requires an absolute HTTPS endpoint (HTTP loopback is allowed for protocol tests).'
    }
}
function Read-Entry([IO.Compression.ZipArchive] $Zip, [string] $Name) {
    $entries = @($Zip.Entries | Where-Object { $_.FullName -ceq $Name })
    if ($entries.Count -ne 1 -or $entries[0].Length -gt 2097152) { throw 'Missing, duplicate or oversized recovery archive entry.' }
    $stream = $entries[0].Open()
    $buffer = [IO.MemoryStream]::new()
    try { $stream.CopyTo($buffer); return ,$buffer.ToArray() }
    finally { $stream.Dispose(); $buffer.Dispose() }
}
function Assert-Archive([byte[]] $Bytes) {
    if ($Bytes.Length -ne $contract.archiveSize -or (Get-Hash $Bytes) -cne $contract.archiveSha256) {
        throw 'Recovery archive size or SHA256 mismatch. No upload was attempted.'
    }
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
    try {
        $nuspec = Read-Entry $zip ($contract.packageId + '.nuspec')
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $reader = [Xml.XmlReader]::Create([IO.MemoryStream]::new($nuspec, $false), $settings)
        $xml = [Xml.XmlDocument]::new()
        try { $xml.Load($reader) } finally { $reader.Dispose() }
        foreach ($pair in @(@('id', 'packageId'), @('version', 'packageVersion'))) {
            $nodes = $xml.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='$($pair[0])']")
            if ($nodes.Count -ne 1) { throw 'Ambiguous symbol package metadata.' }
            Assert-Equal $nodes[0].InnerText $contract[$pair[1]] $pair[0]
        }
        $nodes = $xml.SelectNodes("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='repository']")
        if ($nodes.Count -ne 1) { throw 'Missing or ambiguous symbol source metadata.' }
        Assert-Equal $nodes[0].GetAttribute('commit') $contract.sourceCommit 'source commit'
        Assert-Equal $nodes[0].GetAttribute('url') $contract.repository 'source repository'
        Assert-Equal (Get-Hash (Read-Entry $zip $contract.pdbPath)) $contract.pdbSha256 'PDB'
    }
    finally { $zip.Dispose(); $stream.Dispose() }
}

if ($contract.schemaVersion -ne 1 -or $contract.originalWholeArchiveEquality -cne 'unknown' -or
    $contract.archiveSize -isnot [long] -and $contract.archiveSize -isnot [int] -or
    $contract.archiveSize -lt 1 -or $contract.archiveSize -gt 2097152 -or
    $contract.archive -cne "release-assets/$($contract.packageVersion)/$($contract.packageId).$($contract.packageVersion).snupkg" -or
    $contract.packageId -cnotmatch '^[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)+$' -or
    $contract.packageVersion -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or
    $contract.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or $contract.sourceTag -cne "v$($contract.packageVersion)") {
    throw 'Unsupported recovery contract.'
}
foreach ($field in @('archiveSha256', 'primarySha256', 'dllSha256', 'pdbSha256')) {
    if ($contract[$field] -isnot [string] -or $contract[$field] -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid recovery hash contract.' }
}
foreach ($field in @('serviceIndex', 'symbolEndpoint', 'primaryUrl')) { Assert-WebUri $contract[$field] }
Assert-Equal $env:GITHUB_EVENT_NAME 'workflow_dispatch' 'event'
Assert-Equal $env:GITHUB_REF 'refs/heads/main' 'ref'
Assert-Equal $env:GITHUB_RUN_ATTEMPT '1' 'attempt'
Assert-Equal $env:GITHUB_REPOSITORY 'KeelMatrix/AuthSurface' 'repository'
if ($env:GITHUB_SHA -cnotmatch '^[0-9a-f]{40}$' -or $env:GITHUB_RUN_ID -cnotmatch '^[1-9]\d*$') { throw 'Invalid recovery run identity.' }
Assert-Equal $env:RECOVERY_CANDIDATE_COMMIT $env:GITHUB_SHA 'candidate input'
$candidateCommit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve recovery checkout.' }
Assert-Equal ([string] $candidateCommit) $env:GITHUB_SHA 'candidate checkout'
foreach ($binding in @(
    @('RECOVERY_ARCHIVE_SHA256', 'archiveSha256'), @('RECOVERY_PACKAGE_ID', 'packageId'),
    @('RECOVERY_PACKAGE_VERSION', 'packageVersion'), @('RECOVERY_SOURCE_COMMIT', 'sourceCommit'),
    @('RECOVERY_SOURCE_TAG', 'sourceTag'), @('RECOVERY_SERVICE_INDEX', 'serviceIndex')
)) {
    Assert-Equal ([Environment]::GetEnvironmentVariable($binding[0])) $contract[$binding[1]] $binding[1]
}
$tagCommit = & git -C $root rev-list --max-count=1 "refs/tags/$($contract.sourceTag)" 2>$null
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve original recovery tag.' }
Assert-Equal ([string] $tagCommit) $contract.sourceCommit 'tag target'
$archivePath = Join-Path $root $contract.archive
$archiveBytes = [IO.File]::ReadAllBytes($archivePath)
Assert-Archive $archiveBytes
$contractHash = Get-Hash $contractBytes
$preparedPath = Join-Path $OutputDirectory 'prepared.json'
$retainedArchive = Join-Path $OutputDirectory ([IO.Path]::GetFileName($archivePath))

# A new handler for each operation avoids reusing an authenticated or stale connection.
# No redirect, retry loop, default credentials, legacy symbol source or primary PUT.
function New-RecoveryClient {
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    return $client
}
function Get-Bytes([string] $Uri) {
    $client = New-RecoveryClient
    try {
        $response = $client.GetAsync($Uri).GetAwaiter().GetResult()
        try {
            if ([int] $response.StatusCode -ne 200) { throw 'Recovery read-only preflight HTTP failure.' }
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if ($bytes.Length -gt 2097152) { throw 'Recovery preflight response too large.' }
            return ,$bytes
        }
        finally { $response.Dispose() }
    }
    catch { throw 'Recovery read-only preflight failed. No upload was attempted.' }
    finally { $client.Dispose() }
}

if ($Mode -eq 'Prepare') {
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Recovery output must be a new directory; preserve any previous attempt.' }
    $indexBytes = Get-Bytes $contract.serviceIndex
    $index = [Text.UTF8Encoding]::new($false, $true).GetString($indexBytes) | ConvertFrom-Json -AsHashtable -NoEnumerate
    if ($index -isnot [Collections.IDictionary] -or $index.version -cne '3.0.0' -or $index.resources -isnot [array]) {
        throw 'Invalid modern symbol service index.'
    }
    $endpoints = @()
    foreach ($resource in $index.resources) {
        if ($resource -isnot [Collections.IDictionary]) { throw 'Malformed service resource.' }
        if (-not $resource.Contains('@type')) { throw 'Missing service resource type.' }
        $types = $resource['@type']
        if ($types -isnot [string] -and $types -isnot [array]) { throw 'Malformed service resource type.' }
        foreach ($type in @($types)) { if ($type -isnot [string]) { throw 'Malformed service resource type.' } }
        if (@($types) -ccontains 'SymbolPackagePublish/4.9.0') {
            if (-not $resource.Contains('@id')) { throw 'Missing modern symbol endpoint.' }
            Assert-Equal $resource['@id'] $contract.symbolEndpoint 'discovered symbol endpoint'
            $endpoints += $resource['@id']
        }
    }
    if ($endpoints.Count -eq 0) { throw 'Modern SymbolPackagePublish/4.9.0 discovery is required.' }
    $primaryBytes = Get-Bytes $contract.primaryUrl
    Assert-Equal (Get-Hash $primaryBytes) $contract.primarySha256 'public primary'
    $primaryStream = [IO.MemoryStream]::new($primaryBytes, $false)
    $primary = [IO.Compression.ZipArchive]::new($primaryStream, [IO.Compression.ZipArchiveMode]::Read)
    try { Assert-Equal (Get-Hash (Read-Entry $primary $contract.dllPath)) $contract.dllSha256 'public DLL' }
    finally { $primary.Dispose(); $primaryStream.Dispose() }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    [IO.File]::WriteAllBytes($retainedArchive, $archiveBytes)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'symbol-recovery.json'), $contractBytes)
    "$($contract.archiveSha256)  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS') -Encoding utf8
    Write-Json 'prepared.json' ([ordered]@{
        schemaVersion = 1; contractSha256 = $contractHash; archiveSha256 = $contract.archiveSha256
        serviceIndexSha256 = (Get-Hash $indexBytes); symbolEndpoint = $contract.symbolEndpoint
        publicPrimarySha256 = (Get-Hash $primaryBytes); sourceCommit = $contract.sourceCommit
        candidateCommit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
        preparedAt = [DateTime]::UtcNow.ToString('o'); effect = 'not-attempted'
    })
    Write-Output 'Exact symbols and public DLL verified; retain this directory before authentication.'
    $global:LASTEXITCODE = 0
    return
}

$prepared = Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json -AsHashtable
if ($prepared.schemaVersion -ne 1 -or $prepared.effect -cne 'not-attempted') { throw 'Invalid preparation receipt.' }
Assert-Equal $prepared.contractSha256 $contractHash 'prepared contract'
Assert-Equal $prepared.archiveSha256 $contract.archiveSha256 'prepared archive'
Assert-Equal $prepared.symbolEndpoint $contract.symbolEndpoint 'prepared endpoint'
Assert-Equal $prepared.publicPrimarySha256 $contract.primarySha256 'prepared primary'
Assert-Equal $prepared.sourceCommit $contract.sourceCommit 'prepared source'
foreach ($binding in @(@('candidateCommit', 'GITHUB_SHA'), @('runId', 'GITHUB_RUN_ID'), @('runAttempt', 'GITHUB_RUN_ATTEMPT'))) {
    Assert-Equal $prepared[$binding[0]] ([Environment]::GetEnvironmentVariable($binding[1])) 'prepared run'
}
Assert-Equal (Get-Hash ([IO.File]::ReadAllBytes((Join-Path $OutputDirectory 'symbol-recovery.json')))) $contractHash 'retained contract'
$retainedBytes = [IO.File]::ReadAllBytes($retainedArchive)
Assert-Archive $retainedBytes
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'receipt.json')) { throw 'Recovery was already attempted. Preserve effects; do not replay.' }
if ([string]::IsNullOrWhiteSpace($env:NUGET_PUSH_TOKEN)) { throw 'Temporary NUGET_PUSH_TOKEN is required for submit.' }
$receipt = [ordered]@{
    schemaVersion = 1; effect = 'unknown'; archiveSha256 = $contract.archiveSha256; archiveSize = $retainedBytes.Length
    packageId = $contract.packageId; packageVersion = $contract.packageVersion; sourceCommit = $contract.sourceCommit
    sourceTag = $contract.sourceTag; symbolEndpoint = $contract.symbolEndpoint; contractSha256 = $contractHash
    candidateCommit = $env:GITHUB_SHA; runId = $env:GITHUB_RUN_ID; runAttempt = $env:GITHUB_RUN_ATTEMPT
    startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null; httpStatus = $null
    applicationSendCalls = 1; applicationRetries = 0; providerRequestId = $null; providerDate = $null
    originalWholeArchiveEquality = 'unknown'; symbolIndexing = 'unverified'
}
# Save intent before any network effect. Interruption leaves unknown, never success.
Write-Json 'receipt.json' $receipt
$client = New-RecoveryClient
$request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put, $contract.symbolEndpoint)
$request.Version = [version] '1.1'
$request.VersionPolicy = [Net.Http.HttpVersionPolicy]::RequestVersionExact
$request.Headers.ConnectionClose = $true
$null = $request.Headers.TryAddWithoutValidation('X-NuGet-ApiKey', $env:NUGET_PUSH_TOKEN)
$multipart = [Net.Http.MultipartFormDataContent]::new()
$content = [Net.Http.ByteArrayContent]::new($retainedBytes)
$content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
$multipart.Add($content, 'package', [IO.Path]::GetFileName($retainedArchive))
$request.Content = $multipart
try {
    $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $status = [int] $response.StatusCode
        $receipt.httpStatus = $status
        $receipt.effect = if ($status -eq 201) { 'accepted' } elseif ($status -eq 409) { 'conflict' }
            elseif ($status -in @(400, 401, 403, 404, 413)) { 'rejected' } else { 'unknown' }
        foreach ($header in @('x-ms-request-id', 'x-nuget-request-id')) {
            if ($response.Headers.Contains($header)) {
                $value = ($response.Headers.GetValues($header) -join ',')
                if ($value -cmatch '^[A-Za-z0-9-]{1,100}$' -and
                    -not $value.Contains($env:NUGET_PUSH_TOKEN, [StringComparison]::Ordinal)) { $receipt.providerRequestId = $value }
            }
        }
        if ($null -ne $response.Headers.Date) { $receipt.providerDate = $response.Headers.Date.ToString() }
    }
    finally { $response.Dispose() }
}
catch {
    # Never print provider bodies, credential-bearing headers or exception details.
    $receipt.effect = 'unknown'
}
finally {
    $receipt.finishedAt = [DateTime]::UtcNow.ToString('o')
    Write-Json 'receipt.json' $receipt
    $request.Dispose(); $client.Dispose()
}
if ($receipt.effect -cne 'accepted') {
    throw "Symbol recovery did not receive HTTP 201 (effect: $($receipt.effect)). Preserve receipts and reconcile; do not replay."
}
Write-Output 'The symbol endpoint returned HTTP 201 for the verified archive. Symbol validation/indexing remains unverified.'
$global:LASTEXITCODE = 0
