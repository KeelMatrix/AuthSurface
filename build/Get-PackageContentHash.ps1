function Get-PackageContentHash([string] $ArchivePath) {
    $sdk = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot select SDK for NuGet content hash.' }
    $line = @(& dotnet --list-sdks | Where-Object { $_ -match "^$([regex]::Escape($sdk)) \[" })
    if ($LASTEXITCODE -ne 0 -or $line.Count -ne 1) { throw 'Cannot resolve SDK for NuGet content hash.' }
    $directory = Join-Path ([regex]::Match($line[0], '\[(.+)\]').Groups[1].Value) $sdk
    foreach ($assembly in @('NuGet.Common', 'NuGet.Versioning', 'NuGet.Frameworks', 'NuGet.Packaging')) {
        [void][Reflection.Assembly]::LoadFrom((Join-Path $directory "$assembly.dll"))
    }
    $raw = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA512).Hash))
    $reader = [NuGet.Packaging.PackageArchiveReader]::new($ArchivePath)
    try { return $reader.GetContentHash([Threading.CancellationToken]::None, [Func[string]]{ $raw }) }
    finally { $reader.Dispose() }
}
