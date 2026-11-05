<#
.SYNOPSIS
    Builds RTSSPerformancePlugin and packages it as a .lplug4 for Logi Options+.

.DESCRIPTION
    A .lplug4 is a ZIP archive whose root contains exactly two folders:

        bin\        the plugin assembly named by pluginFileName in the manifest
        metadata\   LoupedeckPackage.yaml, Icon256x256.png, DefaultIconTemplate.ict

    It is NOT a JSON manifest with a .lplug4 extension. Verified against a working
    third-party package on 2026-08-21.

.PARAMETER Install
    After packaging, hand the .lplug4 to Logi Options+ so it runs its installer UI.
    Requires confirming a dialog; nothing is installed silently.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\Package-Plugin.ps1
    powershell -ExecutionPolicy Bypass -File tools\Package-Plugin.ps1 -Install
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'RTSSPerformancePlugin.sln'
$project = Join-Path $repoRoot 'src\RTSSPerformancePlugin.csproj'
$helperProject = Join-Path $repoRoot 'tools\RTSSProfileHelper\RTSSProfileHelper.csproj'
$metadataDir = Join-Path $repoRoot 'src\metadata'
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ('RTSSPerformancePlugin-stage-' + [Guid]::NewGuid().ToString('N'))
$packagePath = Join-Path $repoRoot 'RTSSPerformancePlugin.lplug4'

function Invoke-Checked {
    param(
        [string]$Command,
        [scriptblock]$Action
    )

    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}

Write-Host '==> Cleaning' -ForegroundColor Cyan
Invoke-Checked 'dotnet clean' { dotnet clean $project -c $Configuration -v q --nologo }
foreach ($generatedDirectory in @(
    'src\bin',
    'src\obj',
    'tools\RTSSProfileHelper\bin',
    'tools\RTSSProfileHelper\obj',
    'dist')) {
    $path = Join-Path $repoRoot $generatedDirectory
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

Write-Host '==> Restoring' -ForegroundColor Cyan
if (Test-Path $solution) {
    Invoke-Checked 'dotnet restore' { dotnet restore $solution --nologo }
} else {
    Invoke-Checked 'dotnet restore' { dotnet restore $project --nologo }
    Invoke-Checked 'dotnet restore helper' { dotnet restore $helperProject --nologo }
}

Write-Host '==> Building isolated RTSS helper' -ForegroundColor Cyan
Invoke-Checked 'dotnet build RTSSProfileHelper' { dotnet build $helperProject -c $Configuration -v q --nologo }

Write-Host "==> Building $Configuration" -ForegroundColor Cyan
Invoke-Checked 'dotnet build' { dotnet build $project -c $Configuration -v q --nologo }

Write-Host '==> Staging package' -ForegroundColor Cyan
$binStage = Join-Path $stageRoot 'bin'
$metaStage = Join-Path $stageRoot 'metadata'
New-Item -ItemType Directory -Path $binStage, $metaStage -Force | Out-Null

# Ship only files produced by this clean Release build. PluginApi, SkiaSharp, Svg.Skia, Svg.Model,
# and libSkiaSharp are supplied by the installed Logi Plugin Service and remain deliberately
# excluded from the package.
$buildOutput = Join-Path $repoRoot "src\bin\$Configuration"
$helperOutput = Join-Path $repoRoot "tools\RTSSProfileHelper\bin\$Configuration"
if (-not (Test-Path (Join-Path $buildOutput 'RTSSPerformancePlugin.dll'))) {
    throw "Expected $Configuration output was not produced at '$buildOutput'."
}

Get-ChildItem -Path $buildOutput -File |
    Where-Object { $_.Extension -in '.dll', '.json' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $binStage }

$nativeRuntime = Join-Path $buildOutput 'runtimes'
if (Test-Path $nativeRuntime) {
    Copy-Item $nativeRuntime -Destination $binStage -Recurse -Force
}

foreach ($helperFileName in @(
        'RTSSProfileHelper.exe',
        'RTSSProfileHelper.dll',
        'RTSSProfileHelper.deps.json',
        'RTSSProfileHelper.runtimeconfig.json')) {
    $helperPath = Join-Path $helperOutput $helperFileName
    if (-not (Test-Path $helperPath)) {
        throw "Required isolated helper file is missing: '$helperFileName'."
    }

    Copy-Item $helperPath -Destination $binStage
}

foreach ($runtimeTargetAssembly in @(
        'System.Diagnostics.EventLog.dll',
        'System.Diagnostics.PerformanceCounter.dll')) {
    $rootAssembly = Join-Path $binStage $runtimeTargetAssembly
    $runtimeAssembly = Join-Path $binStage (Join-Path 'runtimes\win\lib\net8.0' $runtimeTargetAssembly)
    if ((Test-Path $rootAssembly) -and (Test-Path $runtimeAssembly)) {
        Remove-Item $rootAssembly -Force
    }
}

Copy-Item (Join-Path $metadataDir '*') -Destination $metaStage -Recurse

Write-Host '==> Verifying staged package' -ForegroundColor Cyan
$manifest = Join-Path $metaStage 'LoupedeckPackage.yaml'
if (-not (Test-Path $manifest)) { throw 'metadata\LoupedeckPackage.yaml is missing.' }

$manifestText = Get-Content $manifest -Raw
$requiredMetadata = @('LoupedeckPackage.yaml', 'DefaultIconTemplate.ict', 'Icon256x256.png')
foreach ($metadataFile in $requiredMetadata) {
    if (-not (Test-Path (Join-Path $metaStage $metadataFile))) {
        throw "Required metadata asset '$metadataFile' is missing."
    }
}
$pluginFileName = ([regex]::Match($manifestText, '(?m)^\s*pluginFileName:\s*(\S+)')).Groups[1].Value
if (-not $pluginFileName) { throw 'pluginFileName is not declared in the manifest.' }
if (-not (Test-Path (Join-Path $binStage $pluginFileName))) {
    throw "Manifest declares pluginFileName '$pluginFileName' but bin\ does not contain it."
}
$depsPath = Join-Path $binStage 'RTSSPerformancePlugin.deps.json'
if (-not (Test-Path $depsPath)) { throw 'bin\RTSSPerformancePlugin.deps.json is missing.' }

foreach ($helperFileName in @(
        'RTSSProfileHelper.exe',
        'RTSSProfileHelper.dll',
        'RTSSProfileHelper.deps.json',
        'RTSSProfileHelper.runtimeconfig.json')) {
    if (-not (Test-Path (Join-Path $binStage $helperFileName))) {
        throw "bin\$helperFileName is missing."
    }
}

$depsText = Get-Content $depsPath -Raw
if ($depsText -match '(?i)libSkiaSharp|SkiaSharp.NativeAssets') {
    throw 'Plugin dependency manifest unexpectedly requires bundled SkiaSharp native assets.'
}
if (Get-ChildItem $binStage -Recurse -File -Filter '*.pdb') {
    throw 'PDB files are not allowed in the production package.'
}
if (Get-ChildItem $binStage -Recurse -File | Where-Object { $_.FullName -match '[\\/]native[\\/]' }) {
    throw 'Native runtime payloads are not allowed in this host-owned production package.'
}
if ($manifestText -match '(?m)^\s*-\s*All\s*$') {
    throw "supportedDevices contains 'All', which is not a valid device family and makes the service reject the plugin."
}
if ($manifestText -match '(?i)(?:[A-Z]:[\\/]|C:\\Users\\|Downloads|Desktop)') {
    throw 'Package metadata contains a machine-specific path.'
}
Write-Host '==> Creating .lplug4 package' -ForegroundColor Cyan
if (Test-Path $packagePath) { Remove-Item $packagePath -Force }

if (Get-Command 'logiplugintool' -ErrorAction SilentlyContinue) {
    Invoke-Checked 'logiplugintool pack' { logiplugintool pack $stageRoot $packagePath }
    Write-Host ''
    Write-Host "Package: $packagePath" -ForegroundColor Green
    Invoke-Checked 'logiplugintool verify' { logiplugintool verify $packagePath }
} else {
    Write-Host 'logiplugintool not found in PATH; packing via Compress-Archive...' -ForegroundColor Yellow
    $zipPath = $packagePath + '.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Move-Item $zipPath $packagePath -Force
    Write-Host ''
    Write-Host "Package: $packagePath" -ForegroundColor Green
}

$archiveListing = Join-Path $env:TEMP ('RTSSPerformancePlugin-package-' + [Guid]::NewGuid().ToString('N'))
$archiveZip = $archiveListing + '.zip'
try {
    Copy-Item $packagePath $archiveZip
    Expand-Archive -LiteralPath $archiveZip -DestinationPath $archiveListing -Force
    $requiredPaths = @(
        ('bin\' + $pluginFileName)
        'bin\RTSSPerformancePlugin.deps.json'
        'bin\RTSSProfileHelper.exe'
        'bin\RTSSProfileHelper.dll'
        'bin\RTSSProfileHelper.deps.json'
        'bin\RTSSProfileHelper.runtimeconfig.json'
        'metadata\LoupedeckPackage.yaml'
        'metadata\DefaultIconTemplate.ict'
        'metadata\Icon256x256.png'
    )
    foreach ($requiredPath in $requiredPaths) {
        if (-not (Test-Path (Join-Path $archiveListing $requiredPath))) {
            throw "Final archive is missing '$requiredPath'."
        }
    }

    $packagedFiles = Get-ChildItem $archiveListing -File -Recurse
    if ($packagedFiles | Where-Object { $_.Extension -eq '.pdb' }) {
        throw 'Final archive contains PDB files.'
    }
    if ($packagedFiles | Where-Object { $_.FullName -match '[\\/]native[\\/]' }) {
        throw 'Final archive contains an unexpected native runtime directory.'
    }

    $leakPattern = 'C:\\Users\\'
    foreach ($file in Get-ChildItem $archiveListing -File -Recurse) {
        $content = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($file.FullName))

        if ($content -match $leakPattern) {
            throw "Final archive contains a machine-specific path in '$($file.Name)'."
        }
    }
}
finally {
    if (Test-Path $archiveZip) {
        Remove-Item $archiveZip -Force
    }
    if (Test-Path $archiveListing) {
        Remove-Item $archiveListing -Recurse -Force
    }
}

if ($Install) {
    Write-Host ''
    Write-Host '==> Handing the package to Logi Options+ (confirm the installer dialog)' -ForegroundColor Cyan
    Start-Process $packagePath
}
