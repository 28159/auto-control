# WeChatAutomation Quick Publish Script (x64)
$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WeChatAutomation Quick Publish (x64)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Get project path
$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectPath

# Check dotnet
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[Error] dotnet not found. Please install .NET 9 SDK" -ForegroundColor Red
    Write-Host "Download: https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}

# Version info
$version = "1.1.0"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

Write-Host "`nVersion: $version" -ForegroundColor Green
Write-Host "Timestamp: $timestamp" -ForegroundColor Gray

# Output directory
$outputBase = Join-Path $projectPath "releases"
$releaseDir = Join-Path $outputBase "v$version`_$timestamp"
$targetDir = Join-Path $releaseDir "WeChatAutomation_$version`_x64"

if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

Write-Host "`nPublishing x64 version..." -ForegroundColor Yellow
Write-Host "Target: $targetDir" -ForegroundColor Gray

try {
    $publishArgs = @(
        "publish"
        "src/WeChatAutomation.App/WeChatAutomation.App.csproj"
        "-c", "Release"
        "-r", "win-x64"
        "--self-contained", "true"
        "-p:PublishSingleFile=true"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:EnableCompressionInSingleFile=true"
        "-p:IncludeAllContentForSelfExtract=true"
        "-p:DebugType=None"
        "-p:DebugSymbols=false"
        "-o", $targetDir
        "--nologo"
        "-v", "q"
    )
    
    & dotnet @publishArgs
    
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed"
    }
    
    # Create startup script
    $startBatContent = @"
@echo off
chcp 65001 >nul
title WeChatAutomation v$version (x64)
echo ========================================
echo   WeChatAutomation v$version
echo   Architecture: x64
echo ========================================
echo.
echo Starting...
start "" "%~dp0WeChatAutomation.App.exe"
"@
    
    Set-Content -Path (Join-Path $targetDir "Start.bat") -Value $startBatContent -Encoding OEM
    
    # Create README
    $readmeContent = @"
WeChatAutomation v$version (x64)

Universal desktop automation recording and playback tool.

System Requirements:
- Windows 10/11
- No .NET runtime required (built-in)

How to Use:
1. Double-click WeChatAutomation.App.exe or Start.bat
2. Press F9 to start recording
3. Perform actions in any application
4. Press F9 to stop recording
5. Press F11 to playback script

Hotkeys:
- F9: Start/Stop recording
- F10: Confirm action (step-by-step mode)
- F11: Start/Stop playback

Features:
- Two recording modes: continuous, step-by-step
- Multiple action types: click, input, keys, copy, paste, etc.
- Script management: create, save, load, rename, delete
- Parameterized scripts support
- HTTP API, MQTT, MCP Server extensions

Published: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@
    
    Set-Content -Path (Join-Path $targetDir "README.txt") -Value $readmeContent -Encoding UTF8
    
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  Creating ZIP package" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    
    $zipPath = Join-Path $releaseDir "WeChatAutomation_$version`_x64.zip"
    Write-Host "Compressing: WeChatAutomation_$version`_x64" -ForegroundColor Gray
    Compress-Archive -Path $targetDir -DestinationPath $zipPath -Force
    
    if (Test-Path $zipPath) {
        Write-Host "  [OK] ZIP created successfully" -ForegroundColor Green
    }
    
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  Publish completed!" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "`nOutput directory: $releaseDir" -ForegroundColor Green
    Write-Host "Files:" -ForegroundColor Gray
    
    Get-ChildItem $releaseDir -Filter "*.zip" | ForEach-Object {
        Write-Host "  - $($_.Name) ($([math]::Round($_.Length/1MB, 2)) MB)" -ForegroundColor Gray
    }
    
    Write-Host "`nPress any key to open output directory..." -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    explorer.exe $releaseDir
}
catch {
    Write-Host "`nPublish failed: $_" -ForegroundColor Red
    Read-Host "Press Enter to exit"
    exit 1
}
