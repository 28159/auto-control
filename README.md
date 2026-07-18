# 通用录制工具

基于 .NET 9 + WPF 的通用桌面自动化录制与回放工具，支持任意 Windows 应用。

## 功能特性

- **两种录制模式**：一口气自动录制 / 一步一步手动确认
- **全局快捷键**：F9 录制、F10 确认、F11 回放
- **多种操作类型**：点击、输入、按键、复制、粘贴、插入文本、等待、截图、打开应用、等待应用启动、阅读窗口内容、滚动
- **脚本管理**：新建/保存/加载/重命名/删除多个脚本
- **自动排除自身**：录制工具自身的操作不会被记录
- **窗口选择**：阅读功能支持选择目标窗口
- **跨架构支持**：支持 x64、x86、ARM64 三种架构

## 下载安装

### 预编译版本（推荐）

从 [Releases](https://github.com/your-username/WeChatAutomation/releases) 页面下载最新版本：

| 版本 | 架构 | 适用系统 | 下载 |
|------|------|----------|------|
| WeChatAutomation_x64.zip | 64位 | 大多数现代PC (Intel/AMD) | [下载](https://github.com/your-username/WeChatAutomation/releases/latest/download/WeChatAutomation_x64.zip) |
| WeChatAutomation_x86.zip | 32位 | 老旧32位系统 | [下载](https://github.com/your-username/WeChatAutomation/releases/latest/download/WeChatAutomation_x86.zip) |
| WeChatAutomation_arm64.zip | ARM64 | Surface Pro X, Snapdragon等 | [下载](https://github.com/your-username/WeChatAutomation/releases/latest/download/WeChatAutomation_arm64.zip) |

**无需安装 .NET 运行时**，下载解压即可使用！

### 从源码编译

#### 环境要求

- Windows 10/11
- .NET 9 SDK
- Visual Studio 2022 或 `dotnet` CLI

#### 编译运行

```bash
cd WeChatAutomation
dotnet build
dotnet run --project src\WeChatAutomation.App
```

#### 自行发布

```bash
# 发布所有架构版本
.\publish.bat

# 或使用 PowerShell 脚本（更详细）
.\WeChatAutomation\publish\publish-all.ps1
```

## 使用方法

1. 双击 `WeChatAutomation.exe` 启动（或从源码运行）
2. 点击 **⏺ 录制** 或按 **F9** 开始录制
3. 在任意应用中进行操作（点击、输入等会自动记录）
4. 按 **F9** 停止录制
5. 在步骤列表中编辑/调整步骤
6. 点击 **💾 保存** 保存为脚本
7. 点击 **▶ 回放** 或按 **F11** 执行脚本

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| F9 | 开始/停止录制 |
| F10 | 确认操作（一步一步模式） |
| F11 | 开始/停止回放 |

## 扩展服务

WeChatAutomation 支持以下扩展服务，可在界面右下角开启/关闭：

- **HTTP API**: 通过 REST API 控制录制和回放
- **MQTT**: 通过 MQTT 协议远程控制
- **MCP Server**: 支持 MCP 协议，可与 AI 助手集成

## 系统要求

- Windows 10/11
- 无需安装 .NET 运行时（预编译版本已内置）
