# Packaging script for RTSS Insights (.lplug4)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$pluginDir = Join-Path $root "srcin\Release\staging_package"
$outLplug = Join-Path $root "RTSSInsights.lplug4"

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host "   Packaging RTSS Insights (.lplug4)" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

if (-not (Test-Path "$pluginDir\metadata\LoupedeckPackage.yaml")) {
    Write-Host "Running release build first..." -ForegroundColor Yellow
    dotnet build (Join-Path $root "RTSSPerformancePlugin.sln") -c Release
}

$tempZip = Join-Path $root "temp_staging.zip"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($pluginDir, $tempZip)
Move-Item -Path $tempZip -Destination $outLplug -Force

Write-Host "[SUCCESS] Distributable package generated:" -ForegroundColor Green
Write-Host "  $outLplug" -ForegroundColor Green
