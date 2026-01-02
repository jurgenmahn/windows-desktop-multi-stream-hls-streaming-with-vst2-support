@echo off
REM Build script for Audio Processor And Streamer
REM Creates a Release build and packages it into an installer

echo ========================================
echo Audio Processor And Streamer - Build
echo ========================================
echo.

REM Step 1: Publish the project (self-contained single-file)
echo [1/2] Publishing Release configuration...
dotnet publish -c Release -p:Platform=x64
if errorlevel 1 (
    echo.
    echo ERROR: Publish failed!
    pause
    exit /b 1
)
echo Publish completed successfully!
echo.

REM Step 2: Create installer
echo [2/2] Creating installer...

set ISCC=
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe
if exist "C:\Program Files\Inno Setup 6\ISCC.exe" set ISCC=C:\Program Files\Inno Setup 6\ISCC.exe
if exist "C:\Program Files (x86)\Inno Setup 5\ISCC.exe" set ISCC=C:\Program Files (x86)\Inno Setup 5\ISCC.exe
if exist "C:\Program Files\Inno Setup 5\ISCC.exe" set ISCC=C:\Program Files\Inno Setup 5\ISCC.exe

if "%ISCC%"=="" (
    echo.
    echo ERROR: Inno Setup not found!
    echo.
    echo Please install Inno Setup from: https://jrsoftware.org/isdl.php
    pause
    exit /b 1
)

"%ISCC%" "%~dp0Installer.iss"
if errorlevel 1 (
    echo.
    echo ERROR: Installer creation failed!
    pause
    exit /b 1
)

echo.
echo ========================================
echo Build completed successfully!
echo ========================================
echo.
echo Installer created in: %~dp0InstallerOutput\
echo.
pause
