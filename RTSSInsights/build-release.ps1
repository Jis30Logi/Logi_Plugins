[CmdletBinding()]
param(
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

& (Join-Path $repoRoot 'tools\Portability-Audit.ps1')
if (-not $?) { throw 'Portability audit failed.' }

& (Join-Path $repoRoot 'tools\Package-Plugin.ps1') -Configuration Release -Install:$Install
if (-not $?) { throw 'Release packaging failed.' }

$rootPackage = Join-Path $repoRoot 'RTSSPerformancePlugin.lplug4'
Write-Host "Release package: $rootPackage" -ForegroundColor Green
