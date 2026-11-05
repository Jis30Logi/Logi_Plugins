[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$excludedDirectories = @('bin', 'obj', 'dist', '.git', '.vs', '.idea')
$patterns = @(
    'C:\\Users\\',
    '%USERNAME%',
    '%USERPROFILE%',
    '(?:^|[\\/])(?:Downloads|Desktop)(?:[\\/]|$)'
)

$files = Get-ChildItem $repoRoot -File -Recurse | Where-Object {
    $relative = $_.FullName.Substring($repoRoot.Length + 1)
    $_.FullName -ne $PSCommandPath -and
    $relative -notmatch '(^|[\\/])(?:bin|obj|dist|\.git|\.vs|\.idea)(?:[\\/]|$)'
}
$matches = foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    foreach ($pattern in $patterns) {
        if ($text -match $pattern) {
            [PSCustomObject]@{ File = $file.FullName.Substring($repoRoot.Length + 1); Pattern = $pattern }
        }
    }
}

if ($matches) {
    $matches | Format-Table -AutoSize | Out-String | Write-Host
    throw 'Portability audit found machine-specific references.'
}

Write-Host 'Portability audit passed.' -ForegroundColor Green
