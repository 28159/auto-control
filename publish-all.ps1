# WeChatAutomation 全架构发布脚本
$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WeChatAutomation 全架构发布工具" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 获取项目路径
$projectPath = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $projectPath

# 检查 dotnet
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[错误] 未找到 dotnet，请先安装 .NET 9 SDK" -ForegroundColor Red
    Write-Host "下载地址: https://dotnet.microsoft.com/download/dotnet/9.0" -ForegroundColor Yellow
    Read-Host "按回车退出"
    exit 1
}

# 版本信息
$version = "1.1.0"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

Write-Host "`n版本: $version" -ForegroundColor Green
Write-Host "时间戳: $timestamp" -ForegroundColor Gray

# 输出目录
$outputBase = Join-Path $projectPath "releases"
$releaseDir = Join-Path $outputBase "v$version`_$timestamp"

if (-not (Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
}

# 显示菜单
Write-Host "`n选择发布模式:" -ForegroundColor Yellow
Write-Host "  1. 发布所有架构版本 (x64 + x86 + arm64) - 推荐"
Write-Host "  2. 仅发布 x64 版本 (最常用)"
Write-Host "  3. 仅发布 x86 版本 (32位系统)"
Write-Host "  4. 仅发布 ARM64 版本 (Surface Pro X等)"
Write-Host "  5. 退出"
Write-Host ""

$choice = Read-Host "请输入选择 (1-5)"

function Publish-Version {
    param(
        [string]$RuntimeId,
        [string]$Name
    )
    
    $targetDir = Join-Path $releaseDir "WeChatAutomation_$version`_$Name"
    
    Write-Host "`n正在发布 $Name 版本..." -ForegroundColor Yellow
    Write-Host "目标: $targetDir" -ForegroundColor Gray
    
    try {
        $publishArgs = @(
            "publish"
            "src/WeChatAutomation.App/WeChatAutomation.App.csproj"
            "-c", "Release"
            "-r", $RuntimeId
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
            throw "发布失败"
        }
        
        # 创建启动脚本
        $startBat = @"
@echo off
chcp 65001 >nul
title WeChatAutomation v$version ($Name)
echo ========================================
echo   WeChatAutomation v$version
echo   架构: $Name
echo ========================================
echo.
echo 正在启动...
start "" "%~dp0WeChatAutomation.App.exe"
"@
        
        Set-Content -Path (Join-Path $targetDir "启动.bat") -Value $startBat -Encoding OEM
        
        # 创建README
        $readme = @"
WeChatAutomation v$version ($Name)

通用桌面自动化录制与回放工具

系统要求:
- Windows 10/11
- 无需安装 .NET 运行时（已内置）

使用方法:
1. 双击 WeChatAutomation.App.exe 或 启动.bat 启动
2. 按 F9 开始录制
3. 在任意应用中进行操作
4. 按 F9 停止录制
5. 按 F11 回放脚本

快捷键:
- F9: 开始/停止录制
- F10: 确认操作（一步一步模式）
- F11: 开始/停止回放

功能特性:
- 两种录制模式：连续录制、一步一步确认
- 多种操作类型：点击、输入、按键、复制、粘贴等
- 脚本管理：新建、保存、加载、重命名、删除
- 支持参数化脚本
- HTTP API、MQTT、MCP Server 扩展

发布时间: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@
        
        Set-Content -Path (Join-Path $targetDir "README.txt") -Value $readme -Encoding UTF8
        
        Write-Host "  ✓ $Name 版本发布成功" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  ✗ $Name 版本发布失败: $_" -ForegroundColor Red
        return $false
    }
}

# 执行发布
$success = $false

switch ($choice) {
    "1" {
        Write-Host "`n开始发布所有架构版本..." -ForegroundColor Cyan
        $r1 = Publish-Version -RuntimeId "win-x64" -Name "x64"
        $r2 = Publish-Version -RuntimeId "win-x86" -Name "x86"
        $r3 = Publish-Version -RuntimeId "win-arm64" -Name "arm64"
        $success = $r1 -or $r2 -or $r3
    }
    "2" {
        $success = Publish-Version -RuntimeId "win-x64" -Name "x64"
    }
    "3" {
        $success = Publish-Version -RuntimeId "win-x86" -Name "x86"
    }
    "4" {
        $success = Publish-Version -RuntimeId "win-arm64" -Name "arm64"
    }
    "5" {
        Write-Host "退出" -ForegroundColor Gray
        exit 0
    }
    default {
        Write-Host "[错误] 无效选择" -ForegroundColor Red
        Read-Host "按回车退出"
        exit 1
    }
}

if ($success) {
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  正在创建压缩包" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    
    # 创建ZIP文件
    $zipFiles = @()
    Get-ChildItem $releaseDir -Directory | Where-Object { $_.Name -like "WeChatAutomation_*" } | ForEach-Object {
        $zipPath = Join-Path $releaseDir "$($_.Name).zip"
        Write-Host "正在压缩: $($_.Name)" -ForegroundColor Gray
        Compress-Archive -Path $_.FullName -DestinationPath $zipPath -Force
        if (Test-Path $zipPath) {
            Write-Host "  ✓ $($_.Name).zip 创建成功" -ForegroundColor Green
            $zipFiles += $zipPath
        }
    }
    
    # 创建主README
    $mainReadme = @"
# WeChatAutomation v$version

通用桌面自动化录制与回放工具，支持任意 Windows 应用。

## 下载

根据您的系统选择对应的版本：

| 版本 | 架构 | 适用系统 |
|------|------|----------|
| WeChatAutomation_$version`_x64.zip | 64位 | 大多数现代PC (Intel/AMD) |
| WeChatAutomation_$version`_x86.zip | 32位 | 老旧32位系统 |
| WeChatAutomation_$version`_arm64.zip | ARM64 | Surface Pro X, Snapdragon等 |

## 快速开始

1. 下载并解压对应版本
2. 双击 `WeChatAutomation.App.exe` 或 `启动.bat` 启动
3. 按 **F9** 开始录制
4. 在任意应用中进行操作
5. 按 **F9** 停止录制
6. 按 **F11** 回放脚本

## 快捷键

- **F9**: 开始/停止录制
- **F10**: 确认操作（一步一步模式）
- **F11**: 开始/停止回放

## 功能特性

- 两种录制模式：连续录制、一步一步确认
- 多种操作类型：点击、输入、按键、复制、粘贴等
- 脚本管理：新建、保存、加载、重命名、删除
- 支持参数化脚本
- HTTP API、MQTT、MCP Server 扩展

## 系统要求

- Windows 10/11
- 无需安装 .NET 运行时（预编译版本已内置）

---
发布日期: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@
    
    Set-Content -Path (Join-Path $releaseDir "README.md") -Value $mainReadme -Encoding UTF8
    
    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "  发布完成！" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "`n输出目录: $releaseDir" -ForegroundColor Green
    Write-Host "包含以下文件:" -ForegroundColor Gray
    
    Get-ChildItem $releaseDir -Filter "*.zip" | ForEach-Object {
        Write-Host "  - $($_.Name) ($([math]::Round($_.Length/1MB, 2)) MB)" -ForegroundColor Gray
    }
    
    Write-Host "`n按任意键打开输出目录..." -ForegroundColor Yellow
    $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    explorer.exe $releaseDir
}
else {
    Write-Host "`n发布失败" -ForegroundColor Red
    Read-Host "按回车退出"
    exit 1
}
