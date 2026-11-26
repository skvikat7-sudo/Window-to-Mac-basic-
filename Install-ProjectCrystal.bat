@echo off
setlocal EnableExtensions
title Project Crystal - Installer & Launcher

echo ============================================================
echo   PROJECT CRYSTAL - Liquid Glass Desktop for Windows 11
echo ============================================================
echo.

:: Check for .NET 8 Desktop Runtime (x64)
set "HAS_DOTNET8=0"
where dotnet >nul 2>nul
if %ERRORLEVEL%==0 (
    dotnet --list-runtimes 2>nul | findstr /R "Microsoft\.WindowsDesktop\.App 8\." >nul 2>nul
    if %ERRORLEVEL%==0 set "HAS_DOTNET8=1"
)

if "%HAS_DOTNET8%"=="0" (
    if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" set "HAS_DOTNET8=1"
)

if "%HAS_DOTNET8%"=="0" (
    echo [1/2] .NET 8 Desktop Runtime not detected. Installing via winget...
    where winget >nul 2>nul
    if %ERRORLEVEL%==0 (
        winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-package-agreements --accept-source-agreements
        if errorlevel 1 (
            echo.
            echo Could not auto-install .NET 8 Desktop Runtime.
            echo Please download and install it from:
            echo   https://dotnet.microsoft.com/en-us/download/dotnet/8.0
            echo Then re-run this script.
            pause
            exit /b 1
        )
    ) else (
        echo Please install .NET 8.0 Desktop Runtime ^(x64^) from:
        echo   https://dotnet.microsoft.com/en-us/download/dotnet/8.0
        echo Then re-run this script.
        pause
        exit /b 1
    )
) else (
    echo [1/2] .NET 8 Desktop Runtime detected.
)

echo [2/2] Launching Project Crystal Setup...
if exist "%~dp0dist\ProjectCrystalSetup.exe" (
    start "" "%~dp0dist\ProjectCrystalSetup.exe"
) else (
    echo Prebuilt installer not found. Building from source via package.ps1...
    powershell -ExecutionPolicy Bypass -File "%~dp0package.ps1"
    if exist "%~dp0dist\ProjectCrystalSetup.exe" (
        start "" "%~dp0dist\ProjectCrystalSetup.exe"
    ) else (
        echo Build failed. Please ensure .NET 8 SDK is installed.
        pause
        exit /b 1
    )
)

exit /b 0
