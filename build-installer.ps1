# Build script for Audio Processor And Streamer
# Creates a Release build and packages it into an installer
#
# Usage:
#   .\build-installer.ps1                                    # Build with current version
#   .\build-installer.ps1 -Version 1.0.0                     # Update version and build
#   .\build-installer.ps1 -SkipBuild                         # Only create installer (skip publish)
#   .\build-installer.ps1 -Version 1.0.0 -GitPublish         # Build and git commit/tag/push
#   .\build-installer.ps1 -Version 1.0.0 -PublishRemote      # Build and upload to server
#   .\build-installer.ps1 -Version 1.0.0 -GitPublish -PublishRemote -ReleaseNotes "Fixed bugs"

param(
    [string]$Version,
    [switch]$SkipBuild,
    [string]$InnoSetupPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    [switch]$GitPublish,
    [switch]$PublishRemote,
    [string]$ReleaseNotes,
    [string]$RemoteUser = "root",
    [string]$RemoteServer = "192.168.113.2",
    [string]$RemotePassword,
    [string]$RemotePath = "/data/server/mahn.it/software/audioprocessorandstreamer/"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Audio Processor And Streamer - Build" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Validate parameters
if ($GitPublish -or $PublishRemote) {
    if (-not $Version) {
        Write-Host "ERROR: -Version is required when using -GitPublish or -PublishRemote" -ForegroundColor Red
        exit 1
    }
    
    if (-not $ReleaseNotes) {
        Write-Host "ERROR: -ReleaseNotes is required when using -GitPublish or -PublishRemote" -ForegroundColor Red
        exit 1
    }
    
    # Validate version format (x.y.z)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        Write-Host "ERROR: Invalid version format. Use x.y.z (e.g., 1.0.0)" -ForegroundColor Red
        exit 1
    }
}

# Step 0: Update version if specified
if ($Version) {
    # Validate version format (x.y.z)
    if ($Version -notmatch '^\d+\.\d+\.\d+$') {
        Write-Host "ERROR: Invalid version format. Use x.y.z (e.g., 1.0.0)" -ForegroundColor Red
        exit 1
    }

    Write-Host "[0/3] Updating version to $Version..." -ForegroundColor Yellow

    # Update .csproj file
    $CsprojFile = Join-Path $ScriptDir "AudioProcessorAndStreamer.csproj"
    if (Test-Path $CsprojFile) {
        $csprojContent = Get-Content $CsprojFile -Raw

        # Update Version, AssemblyVersion, and FileVersion
        $csprojContent = $csprojContent -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>"
        $csprojContent = $csprojContent -replace '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>"
        $csprojContent = $csprojContent -replace '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>"

        Set-Content $CsprojFile $csprojContent -NoNewline
        Write-Host "  Updated: $CsprojFile" -ForegroundColor Gray
    }
    else {
        Write-Host "WARNING: .csproj file not found at $CsprojFile" -ForegroundColor Yellow
    }

    # Update Installer.iss file
    $IssFile = Join-Path $ScriptDir "Installer.iss"
    if (Test-Path $IssFile) {
        $issContent = Get-Content $IssFile -Raw

        # Update #define MyAppVersion
        $issContent = $issContent -replace '#define MyAppVersion "[^"]+"', "#define MyAppVersion `"$Version`""

        Set-Content $IssFile $issContent -NoNewline
        Write-Host "  Updated: $IssFile" -ForegroundColor Gray
    }
    else {
        Write-Host "WARNING: Installer.iss file not found at $IssFile" -ForegroundColor Yellow
    }

    Write-Host "Version updated to $Version" -ForegroundColor Green
    Write-Host ""
}

# Step 1: Publish the project (creates self-contained deployment in publish folder)
$stepPrefix = if ($Version) { "[1/3]" } else { "[1/2]" }
if (-not $SkipBuild) {
    Write-Host "$stepPrefix Publishing Release configuration..." -ForegroundColor Yellow

    Push-Location $ScriptDir
    try {
        # Use dotnet publish to create self-contained deployment
        # Installer.iss expects files in bin\x64\Release\net8.0-windows\win-x64\publish
        dotnet publish -c Release -p:Platform=x64
        if ($LASTEXITCODE -ne 0) {
            throw "Publish failed with exit code $LASTEXITCODE"
        }
        Write-Host "Publish completed successfully!" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "[1/2] Skipping publish (using existing Release publish)" -ForegroundColor Gray
}

Write-Host ""

# Step 2: Create installer
$installerStepPrefix = if ($Version) { "[2/3]" } else { "[2/2]" }
Write-Host "$installerStepPrefix Creating installer..." -ForegroundColor Yellow

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
$Installer = $null
if (Test-Path $OutputDir) {
    $Installer = Get-ChildItem $OutputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($Installer) {
        Write-Host "Installer created: $($Installer.FullName)" -ForegroundColor Green
        Write-Host "Size: $([math]::Round($Installer.Length / 1MB, 2)) MB" -ForegroundColor Gray
    }
}

# Step 3: Git commit/tag and push
if ($GitPublish) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Publishing to Git" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "[3/3] Committing and pushing to git..." -ForegroundColor Yellow
    
    Push-Location $ScriptDir
    try {
        # Git operations
        git add .
        if ($LASTEXITCODE -ne 0) { throw "git add failed" }
        
        git commit -am $ReleaseNotes
        if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
        
        git push
        if ($LASTEXITCODE -ne 0) { throw "git push failed" }
        
        $tag = "V$Version"
        git tag $tag
        if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
        
        git push origin tag $tag
        if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }
        
        Write-Host "Git operations completed (tagged as $tag)" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}

# Step 4: Upload to remote server
if ($PublishRemote) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Publishing to Remote Server" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "Creating autoupdate.json and uploading to server..." -ForegroundColor Yellow
    
    # Create autoupdate.json
    $autoUpdateJson = @{
        downloadUrl = "https://www.mahn.it/software/audioprocessorandstreamer/AudioProcessorAndStreamer-Setup-$Version.exe"
        releaseNotes = $ReleaseNotes
        version = $Version
    } | ConvertTo-Json
    
    $tempJsonPath = Join-Path $env:TEMP "autoupdate.json"
    Set-Content -Path $tempJsonPath -Value $autoUpdateJson -NoNewline
    
    Write-Host "  Created: $tempJsonPath" -ForegroundColor Gray
    
    # Verify installer exists
    if (-not $Installer) {
        Write-Host "ERROR: Installer file not found" -ForegroundColor Red
        exit 1
    }
    
    $installerFileName = "AudioProcessorAndStreamer-Setup-$Version.exe"
    $installerPath = $Installer.FullName
    $remoteHost = "${RemoteUser}@${RemoteServer}"
    
    Write-Host "Uploading files to $remoteHost..." -ForegroundColor Yellow
    
    # Build scp command based on whether password is provided
    if ($RemotePassword) {
        # Using sshpass (requires sshpass to be installed)
        # Upload autoupdate.json
        $env:SSHPASS = $RemotePassword
        sshpass -e scp $tempJsonPath "${remoteHost}:${RemotePath}"
        if ($LASTEXITCODE -ne 0) { throw "scp autoupdate.json failed" }
        Write-Host "  Uploaded: autoupdate.json" -ForegroundColor Gray
        
        # Upload installer
        sshpass -e scp $installerPath "${remoteHost}:${RemotePath}"
        if ($LASTEXITCODE -ne 0) { throw "scp installer failed" }
        Write-Host "  Uploaded: $installerFileName" -ForegroundColor Gray
        
        Remove-Item env:SSHPASS
    }
    else {
        # Using SSH key authentication (no password)
        # Upload autoupdate.json
        scp $tempJsonPath "${remoteHost}:${RemotePath}"
        if ($LASTEXITCODE -ne 0) { throw "scp autoupdate.json failed" }
        Write-Host "  Uploaded: autoupdate.json" -ForegroundColor Gray
        
        # Upload installer
        scp $installerPath "${remoteHost}:${RemotePath}"
        if ($LASTEXITCODE -ne 0) { throw "scp installer failed" }
        Write-Host "  Uploaded: $installerFileName" -ForegroundColor Gray
    }
    
    # Clean up temp file
    Remove-Item $tempJsonPath -ErrorAction SilentlyContinue
    
    Write-Host ""
    Write-Host "Remote publishing completed!" -ForegroundColor Green
}

# Final summary
if ($GitPublish -or $PublishRemote) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Publishing Summary" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Version: $Version" -ForegroundColor Green
    if ($GitPublish) {
        Write-Host "  Git tag: V$Version" -ForegroundColor Gray
    }
    if ($PublishRemote) {
        Write-Host "  Download: https://www.mahn.it/software/audioprocessorandstreamer/AudioProcessorAndStreamer-Setup-$Version.exe" -ForegroundColor Gray
    }
    Write-Host ""
}