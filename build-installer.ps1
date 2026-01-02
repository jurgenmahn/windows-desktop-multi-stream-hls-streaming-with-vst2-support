# Build script for Audio Processor And Streamer
# Creates a Release build and packages it into an installer

param(
    [switch]$SkipBuild,
    [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Audio Processor And Streamer - Build" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build the project
if (-not $SkipBuild) {
    Write-Host "[1/2] Building Release configuration..." -ForegroundColor Yellow

    Push-Location $ScriptDir
    try {
        dotnet build -c Release -p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed with exit code $LASTEXITCODE"
        }
        Write-Host "Build completed successfully!" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "[1/2] Skipping build (using existing Release build)" -ForegroundColor Gray
}

Write-Host ""

# Step 2: Create installer
Write-Host "[2/2] Creating installer..." -ForegroundColor Yellow

# Check if Inno Setup is installed
if (-not (Test-Path $InnoSetupPath)) {
    # Try alternative paths
    $altPaths = @(
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
        "C:\Program Files\Inno Setup 5\ISCC.exe"
    )

    $found = $false
    foreach ($path in $altPaths) {
        if (Test-Path $path) {
            $InnoSetupPath = $path
            $found = $true
            break
        }
    }

    if (-not $found) {
        Write-Host ""
        Write-Host "ERROR: Inno Setup not found!" -ForegroundColor Red
        Write-Host ""
        Write-Host "Please install Inno Setup from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Or specify the path using: .\build-installer.ps1 -InnoSetupPath 'C:\path\to\ISCC.exe'" -ForegroundColor Yellow
        exit 1
    }
}

$IssFile = Join-Path $ScriptDir "Installer.iss"

if (-not (Test-Path $IssFile)) {
    Write-Host "ERROR: Installer.iss not found at $IssFile" -ForegroundColor Red
    exit 1
}

Write-Host "Using Inno Setup: $InnoSetupPath" -ForegroundColor Gray

& $InnoSetupPath $IssFile

if ($LASTEXITCODE -ne 0) {
    throw "Installer creation failed with exit code $LASTEXITCODE"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Show output location
$OutputDir = Join-Path $ScriptDir "InstallerOutput"
if (Test-Path $OutputDir) {
    $Installer = Get-ChildItem $OutputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($Installer) {
        Write-Host "Installer created: $($Installer.FullName)" -ForegroundColor Green
        Write-Host "Size: $([math]::Round($Installer.Length / 1MB, 2)) MB" -ForegroundColor Gray
    }
}
