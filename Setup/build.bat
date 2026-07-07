@echo off
setlocal

set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
set PROJECT_DIR=%~dp0..
set SETUP_DIR=%~dp0

echo ============================================
echo  miPDFsign – Build and Installer
echo ============================================
echo.

:: ── 1. Publish ──────────────────────────────────────────────────────────────
echo [1/2] Publishing application (self-contained x86)...
dotnet publish "%PROJECT_DIR%\miPDFsign.csproj" ^
    /p:PublishProfile=Release ^
    -c Release ^
    --nologo

if errorlevel 1 (
    echo.
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)

echo Publish completed.
echo.

:: ── 2. Inno Setup ───────────────────────────────────────────────────────────
echo [2/2] Creating installer...

if not exist %ISCC% (
    echo ERROR: Inno Setup 6 not found at:
    echo   %ISCC%
    echo Please install: https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

%ISCC% "%SETUP_DIR%miPDFsign.iss"

if errorlevel 1 (
    echo.
    echo ERROR: Installer creation failed.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Done!
echo  Installer: Setup\output\miPDFsign_Setup_1.4.0.exe
echo ============================================
pause
