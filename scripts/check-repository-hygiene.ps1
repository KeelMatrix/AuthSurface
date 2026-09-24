[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$validatorPath = 'scripts/validate.ps1'
$forbiddenPattern = '(?i)Paper' + 'clip|Cod' + 'ex|KEE-' + '[0-9]+|Fron' + 'tier|acceptance[- ]' + 'delta|orches' + 'trat|Task ' + 'Delegator|frontier ' + 'review|frontier ' + 'regression|review ' + 'findings|review ' + 'gaps|previous ' + 'matrix|false/' + 'incomplete|task[- ]' + 'delegator'

function Invoke-Git([string[]] $Arguments) {
    $output = & git -C $Root @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git -C $Root $($Arguments -join ' ')"
    }

    return $output
}

$trackedFiles = @(Invoke-Git @('ls-files'))
$violations = [System.Collections.Generic.List[string]]::new()
foreach ($relativePath in $trackedFiles) {
    $normalizedPath = ([string]$relativePath).Replace('\', '/')
    if ([regex]::IsMatch($normalizedPath, $forbiddenPattern)) {
        $violations.Add("tracked path: $normalizedPath")
    }

    if ($normalizedPath -eq $validatorPath -or $normalizedPath -eq 'scripts/check-repository-hygiene.ps1') {
        continue
    }

    $fullPath = Join-Path $Root ([string]$relativePath)
    if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
        $content = Get-Content -LiteralPath $fullPath -Raw
        if ([regex]::IsMatch($content, $forbiddenPattern)) {
            $violations.Add("tracked content: $normalizedPath")
        }
    }
}

$history = [string](Invoke-Git @('log', 'HEAD', '--format=%B'))
if ([regex]::IsMatch($history, $forbiddenPattern)) {
    $violations.Add('complete candidate commit message')
}

if ($violations.Count -gt 0) {
    $violations | Write-Output
    throw 'Tracked paths, tracked contents, or complete candidate commit messages contain prohibited internal wording.'
}

Write-Output 'Repository hygiene passed: tracked paths, tracked contents, and complete candidate commit messages are free of the focused prohibited vocabulary.'
