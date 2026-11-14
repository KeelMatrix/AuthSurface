function Read-CandidateInstall([string] $Text, [string] $Version) {
    $install = [regex]::Match($Text, '(?ms)^## Install\r?\n(?<section>.*?)(?=^## |\z)').Groups['section'].Value
    $xml = [regex]::Matches($install, '(?ms)^```xml\r?\n(?<code>.*?)^```')
    $commands = [regex]::Matches($install, '(?ms)^```powershell\r?\n(?<code>.*?)^```')
    if ($xml.Count -ne 1 -or $commands.Count -ne 1) { throw 'Install must supply one XML config and one command sequence.' }
    $lines = @($commands[0].Groups['code'].Value.Trim() -split '\r?\n')
    if ($lines.Count -ne 2 -or $lines[0] -cne "dotnet add package KeelMatrix.AuthSurface --version '[$Version]' --no-restore" -or
        $lines[1] -cne 'dotnet restore --configfile ./NuGet.config') { throw 'Unexpected literal candidate installation commands.' }
    return [pscustomobject]@{ Config = [xml]$xml[0].Groups['code'].Value; Commands = $lines }
}
