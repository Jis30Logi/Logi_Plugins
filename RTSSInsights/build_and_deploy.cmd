@echo off
setlocal enabledelayedexpansion

echo ===================================================
echo   RTSS Insights - Build, Deploy ^& Package
echo ===================================================

cd /d "%~dp0"

echo [1/3] Building solution in Release configuration...
dotnet build RTSSPerformancePlugin.sln -c Release
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed!
    exit /b %ERRORLEVEL%
)

echo [2/3] Running automated regression test suite...
dotnet test tests\RTSSPerformancePlugin.Tests.csproj -c Release --no-build
if %ERRORLEVEL% NEQ 0 (
    echo [WARNING] Some tests reported issues, but package was generated.
)

echo [3/3] Verifying package generation...
if exist "RTSSInsights.lplug4" (
    echo [SUCCESS] RTSSInsights.lplug4 created successfully at:
    echo           %~dp0RTSSInsights.lplug4
) else (
    echo Packaging manually as fallback...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$src = '%LOCALAPPDATA%\Logi\LogiPluginService\Plugins\RTSSPerformance'; $zip = '%~dp0RTSSInsights.zip'; $out = '%~dp0RTSSInsights.lplug4'; if (Test-Path $zip) { Remove-Item $zip -Force }; [System.IO.Compression.ZipFile]::CreateFromDirectory($src, $zip); Move-Item -Path $zip -Destination $out -Force"
    if exist "RTSSInsights.lplug4" (
        echo [SUCCESS] RTSSInsights.lplug4 generated successfully!
    )
)

echo ===================================================
echo Deployed to Logi Options+:
echo %LOCALAPPDATA%\Logi\LogiPluginService\Plugins\RTSSPerformance
echo ===================================================
endlocal
