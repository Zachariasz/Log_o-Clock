@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "ROOT=%~dp0"
pushd "%ROOT%" >nul || (
    echo Unable to open the Log O'clock project folder.
    exit /b 1
)

set "LAST_VERSION=unknown"
for /f "usebackq delims=" %%A in (`powershell.exe -NoProfile -Command "$xml = [xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw); $xml.Project.PropertyGroup.Version"`) do set "LAST_VERSION=%%A"

set "APP_VERSION="
set /p "APP_VERSION=New version number (current: %LAST_VERSION%; for example 1.143.0): "

if not defined APP_VERSION (
    echo A version number is required. Build cancelled.
    popd
    exit /b 1
)

powershell.exe -NoProfile -Command "if ($env:APP_VERSION -notmatch '^\d+\.\d+\.\d+$') { exit 1 }"
if errorlevel 1 (
    echo Invalid version "%APP_VERSION%". Use major.minor.patch, for example 1.143.0.
    popd
    exit /b 1
)

echo.
echo Building Log O'clock version %APP_VERSION%...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%ROOT%scripts\build-release.ps1" -AppVersion "%APP_VERSION%"
set "BUILD_EXIT_CODE=%ERRORLEVEL%"

if not "%BUILD_EXIT_CODE%"=="0" (
    echo.
    echo Build failed with exit code %BUILD_EXIT_CODE%.
    popd
    exit /b %BUILD_EXIT_CODE%
)

echo.
echo Build completed successfully.
popd
exit /b 0
