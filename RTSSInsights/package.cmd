@echo off
setlocal enabledelayedexpansion

echo ===================================================
echo   Packaging RTSS Insights (.lplug4)
echo ===================================================

cd /d "%~dp0"

set "PLUGIN_DIR=%~dp0srcin\Release\staging_package"
set "OUT_FILE=%~dp0RTSSInsights.lplug4"

if not exist "%PLUGIN_DIR%\metadata\LoupedeckPackage.yaml" (
    echo Release staging package not found. Building first...
    call "%~dp0build_and_deploy.cmd"
    exit /b %ERRORLEVEL%
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$src = '%PLUGIN_DIR%'; $zip = '%~dp0temp_pkg.zip'; if (Test-Path $zip) { Remove-Item $zip -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory($src, $zip); Move-Item -Path $zip -Destination '%OUT_FILE%' -Force"

echo [SUCCESS] Package created:
echo   %OUT_FILE%
endlocal
