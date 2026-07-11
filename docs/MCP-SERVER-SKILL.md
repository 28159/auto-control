# MCP Server 使用指南

## 概述

WeChatAutomation 内置了 MCP (Model Context Protocol) Server，允许外部工具（如 OpenClaw、Claude Code、Cursor 等）通过标准协议调用自动化脚本执行功能。

## 启用 MCP Server

### 方式一：UI 界面开启

1. 启动 WeChatAutomation 应用
2. 在底部「服务状态」面板找到 **MCP** 项
3. 点击 **开启** 按钮
4. 状态指示灯变为绿色表示已启动

### 方式二：配置文件自动启动

编辑 `appsettings.json`，将 MCP 设置为启用：

```json
{
  "Mcp": {
    "Enabled": true
  }
}
```

## MCP Server 工作原理

MCP Server 通过 **标准输入输出 (stdin/stdout)** 与外部工具通信，使用 JSON-RPC 2.0 协议。

```
┌─────────────────┐     stdin/stdout     ┌─────────────────┐
│   OpenClaw /    │ ◄──────────────────► │ WeChatAutomation│
│   Claude Code   │                      │    MCP Server   │
└─────────────────┘                      └─────────────────┘
```

## 可用工具

MCP Server 暴露以下工具供外部调用：

### 1. list_scripts

**描述**: 列出所有可用的自动化脚本

**参数**: 无

**返回**: 脚本列表，包含名称和步数

**调用示例**:
```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "list_scripts",
    "arguments": {}
  }
}
```

**返回示例**:
```
- 微信自动回复 (12 步)
- 批量发送消息 (8 步)
- 自动签到 (5 步)
```

---

### 2. get_script_info

**描述**: 获取指定脚本的详细信息

**参数**:
| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| script_name | string | 是 | 脚本名称 |

**调用示例**:
```json
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "get_script_info",
    "arguments": {
      "script_name": "微信自动回复"
    }
  }
}
```

**返回示例**:
```
脚本名称: 微信自动回复
创建时间: 2026/7/10 14:30:00
步骤数量: 12
步骤列表:
  1. 点击 微信窗口
  2. 输入 您好，我是自动回复
  3. 按键 Enter
  ...
```

---

### 3. execute_script

**描述**: 执行指定的自动化脚本

**参数**:
| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| script_name | string | 是 | 脚本名称 |
| parameters | object | 否 | 可选参数字典，用于替换脚本中的占位符 |

**调用示例**:
```json
{
  "jsonrpc": "2.0",
  "id": 3,
  "method": "tools/call",
  "params": {
    "name": "execute_script",
    "arguments": {
      "script_name": "微信自动回复",
      "parameters": {
        "message": "你好，这是一条测试消息"
      }
    }
  }
}
```

**返回示例**:
```
脚本 微信自动回复 执行成功
执行时间: 2026/7/11 10:30:00
```

---

### 4. check_script_exists

**描述**: 检查指定脚本是否存在

**参数**:
| 参数名 | 类型 | 必填 | 说明 |
|--------|------|------|------|
| script_name | string | 是 | 脚本名称 |

**调用示例**:
```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "method": "tools/call",
  "params": {
    "name": "check_script_exists",
    "arguments": {
      "script_name": "微信自动回复"
    }
  }
}
```

**返回示例**:
```
脚本 微信自动回复 存在
```

---

## 在 OpenClaw 中配置

### 方式一：直接通过命令行

```bash
# 启动 WeChatAutomation 并启用 MCP
WeChatAutomation.App.exe

# 在 OpenClaw 中配置 MCP Server
claude mcp add wechat-automation -- command: WeChatAutomation.App.exe
```

### 方式二：配置文件

在 OpenClaw 的配置文件中添加：

```json
{
  "mcpServers": {
    "wechat-automation": {
      "command": "D:\\home\\wxauto\\WeChatAutomation\\src\\WeChatAutomation.App\\bin\\Debug\\net9.0-windows\\WeChatAutomation.App.exe",
      "args": [],
      "env": {}
    }
  }
}
```

## 在 Claude Code 中配置

在 `.claude/settings.json` 或全局配置中添加：

```json
{
  "mcpServers": {
    "wechat": {
      "command": "WeChatAutomation.App.exe",
      "cwd": "D:\\home\\wxauto\\WeChatAutomation\\src\\WeChatAutomation.App\\bin\\Debug\\net9.0-windows"
    }
  }
}
```

## 使用示例

### 示例 1: 列出所有脚本

```
User: 帮我看看有哪些可用的自动化脚本

Claude: 我来调用 MCP Server 查询脚本列表。

[list_scripts 工具调用]

根据 MCP Server 返回的结果，当前有以下脚本：
1. 微信自动回复 (12 步)
2. 批量发送消息 (8 步)
3. 自动签到 (5 步)
```

### 示例 2: 执行脚本

```
User: 帮我执行"微信自动回复"脚本

Claude: 我来执行这个脚本。

[execute_script 工具调用，参数: script_name = "微信自动回复"]

脚本已成功执行完成。
```

### 示例 3: 检查脚本是否存在

```
User: 有没有"自动点赞"这个脚本？

Claude: 我来检查一下。

[check_script_exists 工具调用，参数: script_name = "自动点赞"]

检查结果显示：脚本"自动点赞"不存在。您可以先创建这个脚本，或者使用已有的脚本。
```

## 参数替换功能

脚本执行时支持参数替换，可以在脚本步骤中使用 `{参数名}` 作为占位符。

### 示例脚本步骤

```json
{
  "actions": [
    {
      "actionType": "TypeText",
      "parameter": "{message}",
      "name": "输入消息"
    },
    {
      "actionType": "SendKeys",
      "parameter": "Enter",
      "name": "发送"
    }
  ]
}
```

### 调用时传入参数

```json
{
  "name": "execute_script",
  "arguments": {
    "script_name": "发送消息",
    "parameters": {
      "message": "这是一条测试消息"
    }
  }
}
```

执行时 `{message}` 会被替换为 "这是一条测试消息"。

## 故障排除

### MCP Server 无法启动

1. 检查 `appsettings.json` 中 `Mcp.Enabled` 是否为 `true`
2. 确保应用已正确安装并可运行
3. 查看应用日志中的错误信息

### 工具调用无响应

1. 确认 MCP Server 状态指示灯为绿色
2. 检查外部工具的 MCP 配置是否正确
3. 尝试重启应用

### 脚本执行失败

1. 确保脚本文件存在于 `scripts` 目录
2. 检查脚本格式是否正确
3. 查看日志了解具体错误原因

## 技术细节

- **协议版本**: MCP 2024-11-05
- **通信方式**: stdin/stdout (JSON-RPC 2.0)
- **默认端口**: 无 (通过标准 IO 通信)
- **认证**: 无 (本地调用)

## 相关文件

- `appsettings.json` - 配置文件
- `WeChatAutomation.Core/Services/McpServerService.cs` - MCP Server 实现
- `WeChatAutomation.Core/Services/IScriptExecutor.cs` - 脚本执行接口
- `scripts/` - 脚本存储目录
