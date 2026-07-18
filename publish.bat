@echo off
chcp 65001 >nul
title WeChatAutomation 发布工具

echo ========================================
echo   WeChatAutomation 发布工具
echo ========================================
echo.

cd /d "%~dp0WeChatAutomation"

:: 检查 dotnet
where dotnet >nul 2>nul
if %errorlevel% neq 0 (
    echo [错误] 未找到 dotnet，请先安装 .NET 9 SDK
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet/9.0
    pause
    exit /b 1
)

echo 选择发布模式:
echo   1. 发布所有架构版本 (x64 + x86 + arm64)
echo   2. 仅发布 x64 版本 (最常用)
echo   3. 仅发布 x86 版本 (32位系统)
echo   4. 仅发布 ARM64 版本 (Surface Pro X等)
echo   5. 退出
echo.
set /p choice=请输入选择 (1-5): 

if "%choice%"=="1" goto publish_all
if "%choice%"=="2" goto publish_x64
if "%choice%"=="3" goto publish_x86
if "%choice%"=="4" goto publish_arm64
if "%choice%"=="5" goto exit
goto invalid_choice

:publish_all
echo.
echo 正在发布所有架构版本...
powershell -ExecutionPolicy Bypass -File publish\publish-all.ps1
goto done

:publish_x64
echo.
echo 正在发布 x64 版本...
call :publish_single "win-x64" "x64"
goto done

:publish_x86
echo.
echo 正在发布 x86 版本...
call :publish_single "win-x86" "x86"
goto done

:publish_arm64
echo.
echo 正在发布 ARM64 版本...
call :publish_single "win-arm64" "arm64"
goto done

:publish_single
set RID=%~1
set NAME=%~2
set OUTPUT_DIR=releases\%NAME%

if not exist "%OUTPUT_DIR%" mkdir "%OUTPUT_DIR%"

echo 正在发布 %NAME% 版本...
dotnet publish src\WeChatAutomation.App\WeChatAutomation.App.csproj -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%OUTPUT_DIR%" --nologo -v q

if %errorlevel% neq 0 (
    echo [错误] 发布失败
    pause
    exit /b 1
)

:: 创建启动脚本
echo @echo off > "%OUTPUT_DIR%\启动.bat"
echo chcp 65001 ^>nul >> "%OUTPUT_DIR%\启动.bat"
echo title WeChatAutomation >> "%OUTPUT_DIR%\启动.bat"
echo echo 正在启动 WeChatAutomation... >> "%OUTPUT_DIR%\启动.bat"
echo start "" "%%~dp0WeChatAutomation.exe" >> "%OUTPUT_DIR%\启动.bat"

echo ✓ %NAME% 版本发布成功: %OUTPUT_DIR%
exit /b 0

:invalid_choice
echo [错误] 无效选择
pause
exit /b 1

:done
echo.
echo ========================================
echo   发布完成！
echo ========================================
echo.
echo 输出目录: releases\
echo.
dir /b releases\*.zip 2>nul
echo.
pause

:exit
exit /b 0
