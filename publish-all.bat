@echo off
chcp 65001 >nul
title WeChatAutomation 全架构发布工具

echo ========================================
echo   WeChatAutomation 全架构发布工具
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

:: 获取版本号
set VERSION=1.1.0
set TIMESTAMP=%date:~0,4%%date:~5,2%%date:~8,2%_%time:~0,2%%time:~3,2%%time:~6,2%
set TIMESTAMP=%TIMESTAMP: =0%

echo 版本: %VERSION%
echo 时间戳: %TIMESTAMP%
echo.

:: 创建输出目录
set OUTPUT_BASE=releases\v%VERSION%_%TIMESTAMP%
if not exist "%OUTPUT_BASE%" mkdir "%OUTPUT_BASE%"

echo 选择发布模式:
echo   1. 发布所有架构版本 (x64 + x86 + arm64) - 推荐
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
echo ========================================
echo   开始发布所有架构版本
echo ========================================
call :publish_single "win-x64" "x64"
call :publish_single "win-x86" "x86"
call :publish_single "win-arm64" "arm64"
goto create_packages

:publish_x64
echo.
echo 正在发布 x64 版本...
call :publish_single "win-x64" "x64"
goto create_packages

:publish_x86
echo.
echo 正在发布 x86 版本...
call :publish_single "win-x86" "x86"
goto create_packages

:publish_arm64
echo.
echo 正在发布 ARM64 版本...
call :publish_single "win-arm64" "arm64"
goto create_packages

:publish_single
set RID=%~1
set NAME=%~2
set TARGET_DIR=%OUTPUT_BASE%\WeChatAutomation_%VERSION%_%NAME%

echo.
echo 正在发布 %NAME% 版本...
echo 目标: %TARGET_DIR%

:: 创建目标目录
if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%"

:: 发布
dotnet publish src\WeChatAutomation.App\WeChatAutomation.App.csproj -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%TARGET_DIR%" --nologo -v q

if %errorlevel% neq 0 (
    echo [错误] %NAME% 版本发布失败
    exit /b 1
)

:: 创建启动脚本
echo @echo off > "%TARGET_DIR%\启动.bat"
echo chcp 65001 ^>nul >> "%TARGET_DIR%\启动.bat"
echo title WeChatAutomation v%VERSION% (%NAME%) >> "%TARGET_DIR%\启动.bat"
echo echo ======================================== >> "%TARGET_DIR%\启动.bat"
echo echo   WeChatAutomation v%VERSION% >> "%TARGET_DIR%\启动.bat"
echo echo   架构: %NAME% >> "%TARGET_DIR%\启动.bat"
echo echo ======================================== >> "%TARGET_DIR%\启动.bat"
echo echo. >> "%TARGET_DIR%\启动.bat"
echo echo 正在启动... >> "%TARGET_DIR%\启动.bat"
echo start "" "%%~dp0WeChatAutomation.App.exe" >> "%TARGET_DIR%\启动.bat"

:: 创建README
(
echo WeChatAutomation v%VERSION% (%NAME%)
echo.
echo 通用桌面自动化录制与回放工具
echo.
echo 系统要求:
echo - Windows 10/11
echo - 无需安装 .NET 运行时（已内置）
echo.
echo 使用方法:
echo 1. 双击 WeChatAutomation.App.exe 或 启动.bat 启动
echo 2. 按 F9 开始录制
echo 3. 在任意应用中进行操作
echo 4. 按 F9 停止录制
echo 5. 按 F11 回放脚本
echo.
echo 快捷键:
echo - F9: 开始/停止录制
echo - F10: 确认操作（一步一步模式）
echo - F11: 开始/停止回放
echo.
echo 功能特性:
echo - 两种录制模式：连续录制、一步一步确认
echo - 多种操作类型：点击、输入、按键、复制、粘贴等
echo - 脚本管理：新建、保存、加载、重命名、删除
echo - 支持参数化脚本
echo - HTTP API、MQTT、MCP Server 扩展
echo.
echo 发布时间: %date% %time%
) > "%TARGET_DIR%\README.txt"

echo   ✓ %NAME% 版本发布成功
exit /b 0

:create_packages
echo.
echo ========================================
echo   正在创建压缩包
echo ========================================

:: 创建ZIP文件
cd /d "%OUTPUT_BASE%"
for /d %%D in (WeChatAutomation_*) do (
    echo 正在压缩: %%D
    powershell -Command "Compress-Archive -Path '%%D' -DestinationPath '%%D.zip' -Force"
    if %errorlevel% equ 0 (
        echo   ✓ %%D.zip 创建成功
    ) else (
        echo   ✗ %%D.zip 创建失败
    )
)
cd /d "%~dp0WeChatAutomation"

:: 创建主README
(
echo # WeChatAutomation v%VERSION%
echo.
echo 通用桌面自动化录制与回放工具，支持任意 Windows 应用。
echo.
echo ## 下载
echo.
echo 根据您的系统选择对应的版本：
echo.
echo ^| 版本 ^| 架构 ^| 适用系统 ^|
echo ^|------^|------^|----------^|
echo ^| WeChatAutomation_%VERSION%_x64.zip ^| 64位 ^| 大多数现代PC (Intel/AMD) ^|
echo ^| WeChatAutomation_%VERSION%_x86.zip ^| 32位 ^| 老旧32位系统 ^|
echo ^| WeChatAutomation_%VERSION%_arm64.zip ^| ARM64 ^| Surface Pro X, Snapdragon等 ^|
echo.
echo ## 快速开始
echo.
echo 1. 下载并解压对应版本
echo 2. 双击 `WeChatAutomation.App.exe` 或 `启动.bat` 启动
echo 3. 按 **F9** 开始录制
echo 4. 在任意应用中进行操作
echo 5. 按 **F9** 停止录制
echo 6. 按 **F11** 回放脚本
echo.
echo ## 快捷键
echo.
echo - **F9**: 开始/停止录制
echo - **F10**: 确认操作（一步一步模式）
echo - **F11**: 开始/停止回放
echo.
echo ## 功能特性
echo.
echo - 两种录制模式：连续录制、一步一步确认
echo - 多种操作类型：点击、输入、按键、复制、粘贴等
echo - 脚本管理：新建、保存、加载、重命名、删除
echo - 支持参数化脚本
echo - HTTP API、MQTT、MCP Server 扩展
echo.
echo ## 系统要求
echo.
echo - Windows 10/11
echo - 无需安装 .NET 运行时（预编译版本已内置）
echo.
echo ---^| 发布时间: %date% %time%
) > "%OUTPUT_BASE%\README.md"

echo.
echo ========================================
echo   发布完成！
echo ========================================
echo.
echo 输出目录: %OUTPUT_BASE%
echo.
echo 包含以下文件:
dir /b "%OUTPUT_BASE%\*.zip" 2>nul
echo.

echo 按任意键打开输出目录...
pause >nul
explorer.exe "%OUTPUT_BASE%"
goto exit

:invalid_choice
echo [错误] 无效选择
pause
exit /b 1

:exit
exit /b 0
