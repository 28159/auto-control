# 动态参数系统使用指南

## 概述

动态参数系统允许你在脚本中定义可变的输入字段，执行脚本时可以传入不同的数据。这使得同一个脚本可以处理不同的输入内容。

## 核心概念

### 参数占位符

在脚本步骤的文本中使用 `{参数名}` 作为占位符，执行时会自动替换为用户输入的值。

**示例**:
```
输入内容: {message}
```
当用户输入 `message = "你好"` 时，实际执行为：
```
输入内容: 你好
```

## 使用方式

### 方式一：添加输入参数步骤

1. 在工具栏点击 **📝参数** 按钮
2. 填写参数信息：
   - **参数名称** (英文): 用于占位符，如 `message`
   - **显示名称**: 中文显示名，如 `消息内容`
   - **参数类型**: 文本/数字/密码/多行
   - **默认值**: 可选的默认值
   - **必填**: 是否必须填写
   - **说明**: 参数用途说明

3. 点击确定，参数步骤会添加到步骤列表

### 方式二：管理脚本参数

1. 点击工具栏的 **⚙ 参数** 按钮
2. 查看当前脚本的参数列表
3. 点击 **添加** 添加新参数
4. 点击 **编辑** 修改选中的参数
5. 点击 **删除** 移除选中的参数
6. 系统会自动检测脚本中使用的占位符

### 方式三：在任意文本步骤中使用占位符

在 **输入文本**、**插入文本** 等步骤的参数中直接使用 `{参数名}`：

```
输入文本: {message}
插入文本: 用户名: {username}
```

## 参数类型

| 类型 | 说明 | 输入控件 | 用途 |
|------|------|----------|------|
| Text | 普通文本 | 单行输入框 | 一般文本输入 |
| Number | 数字 | 单行输入框 | 数字输入 |
| Password | 密码 | 密码输入框 | 密码/敏感信息 |
| MultiLine | 多行文本 | 多行文本框 | 长文本/多行内容 |

## 执行流程

### UI 界面执行

1. 点击 **▶ 回放** 按钮
2. 如果脚本有参数定义，会弹出参数输入对话框
3. 填写各参数的值（带 `*` 的为必填项）
4. 点击 **确定执行**

### HTTP API 执行

```bash
curl -X POST http://localhost:5000/api/scripts/execute \
  -H "Content-Type: application/json" \
  -d '{
    "scriptName": "发送消息",
    "parameters": {
      "message": "这是一条测试消息",
      "username": "张三"
    }
  }'
```

### MQTT 执行

```bash
# 发送执行命令
mosquitto_pub -t "wechat-auto/execute/发送消息" \
  -m '{
    "scriptName": "发送消息",
    "parameters": {
      "message": "这是一条测试消息"
    }
  }'

# 订阅状态
mosquitto_sub -t "wechat-auto/status"
```

### MCP 工具执行

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "execute_script",
    "arguments": {
      "script_name": "发送消息",
      "parameters": {
        "message": "这是一条测试消息"
      }
    }
  }
}
```

## 完整示例

### 示例 1: 自动回复消息

**脚本定义** (save as `发送消息.json`):
```json
{
  "Name": "发送消息",
  "Description": "在微信中发送指定消息",
  "Parameters": [
    {
      "Name": "message",
      "DisplayName": "消息内容",
      "DefaultValue": "",
      "IsRequired": true,
      "Type": "Text",
      "Description": "要发送的消息文本"
    }
  ],
  "Actions": [
    {
      "NodeId": "step1",
      "Order": 1,
      "ActionType": "InputParam",
      "Name": "参数: 消息内容",
      "ParameterName": "message",
      "Parameter": "{message}",
      "DefaultValue": "",
      "IsRequired": true,
      "DelayMs": 300,
      "IsEnabled": true
    },
    {
      "NodeId": "step2",
      "Order": 2,
      "ActionType": "Click",
      "Name": "点击输入框",
      "ElementName": "输入框",
      "DelayMs": 500,
      "IsEnabled": true
    },
    {
      "NodeId": "step3",
      "Order": 3,
      "ActionType": "TypeText",
      "Name": "输入消息",
      "Parameter": "{message}",
      "DelayMs": 300,
      "IsEnabled": true
    },
    {
      "NodeId": "step4",
      "Order": 4,
      "ActionType": "SendKeys",
      "Name": "发送",
      "Parameter": "Enter",
      "DelayMs": 300,
      "IsEnabled": true
    }
  ]
}
```

### 示例 2: 表单填写

```json
{
  "Name": "填写表单",
  "Parameters": [
    {
      "Name": "username",
      "DisplayName": "用户名",
      "IsRequired": true,
      "Type": "Text"
    },
    {
      "Name": "phone",
      "DisplayName": "手机号",
      "IsRequired": true,
      "Type": "Text"
    },
    {
      "Name": "address",
      "DisplayName": "地址",
      "IsRequired": false,
      "Type": "MultiLine",
      "Description": "详细地址，可多行"
    }
  ],
  "Actions": [
    {
      "ActionType": "TypeText",
      "Name": "输入用户名",
      "Parameter": "{username}"
    },
    {
      "ActionType": "SendKeys",
      "Parameter": "Tab"
    },
    {
      "ActionType": "TypeText",
      "Name": "输入手机号",
      "Parameter": "{phone}"
    },
    {
      "ActionType": "SendKeys",
      "Parameter": "Tab"
    },
    {
      "ActionType": "TypeText",
      "Name": "输入地址",
      "Parameter": "{address}"
    }
  ]
}
```

### 示例 3: 批量发送不同内容

```json
{
  "Name": "发送通知",
  "Parameters": [
    {
      "Name": "title",
      "DisplayName": "标题",
      "IsRequired": true,
      "Type": "Text"
    },
    {
      "Name": "content",
      "DisplayName": "内容",
      "IsRequired": true,
      "Type": "MultiLine"
    },
    {
      "Name": "password",
      "DisplayName": "密码",
      "IsRequired": false,
      "Type": "Password",
      "Description": "可选的密码参数"
    }
  ],
  "Actions": [
    {
      "ActionType": "TypeText",
      "Parameter": "标题: {title}"
    },
    {
      "ActionType": "Wait",
      "Parameter": "500"
    },
    {
      "ActionType": "TypeText",
      "Parameter": "{content}"
    }
  ]
}
```

## 脚本 JSON 格式说明

```json
{
  "Name": "脚本名称",
  "Description": "脚本描述",
  "CreatedAt": "2026-07-11T10:00:00",
  "Parameters": [
    {
      "Name": "param1",           // 参数名称（英文，用于占位符）
      "DisplayName": "参数1",     // 显示名称（中文）
      "DefaultValue": "默认值",   // 默认值
      "Description": "说明",      // 参数说明
      "IsRequired": true,         // 是否必填
      "Type": "Text"              // 参数类型：Text/Number/Password/MultiLine
    }
  ],
  "Actions": [
    {
      "NodeId": "abc12345",
      "Order": 1,
      "ActionType": "TypeText",
      "Name": "输入步骤",
      "Parameter": "{param1}",    // 使用 {参数名} 占位符
      "DelayMs": 300,
      "IsEnabled": true
    }
  ]
}
```

## RecordedAction 新增属性

| 属性 | 类型 | 说明 |
|------|------|------|
| ParameterName | string | 参数名称，用于 InputParam 类型 |
| DefaultValue | string | 参数默认值 |
| IsRequired | bool | 是否必填 |

## 注意事项

1. **参数名称** 只能使用英文字母、数字和下划线，且不能以数字开头
2. **占位符格式** 必须是 `{参数名}`，花括号不能省略
3. **必填参数** 如果用户未填写，会提示错误并阻止执行
4. **参数替换** 会同时替换 Parameter 和 Name 字段
5. **向后兼容** 没有参数的脚本照常执行
6. **密码类型** 输入时会显示为 `***`，但实际值会正确传递

## 常见问题

### Q: 如何在不修改脚本的情况下发送不同内容？

A: 使用参数系统，在脚本中用 `{参数名}` 标记可变部分，执行时传入不同值。

### Q: 参数可以嵌套使用吗？

A: 目前不支持嵌套，如 `{a_{b}}` 无法解析。

### Q: 从 HTTP/MQTT 传入的参数会覆盖 UI 输入吗？

A: 是的，外部传入的参数会替换脚本中的占位符。

### Q: 如何删除已添加的参数？

A: 点击 **⚙ 参数** 按钮，在参数管理对话框中选择要删除的参数，点击 **删除**。

### Q: 参数名称可以用中文吗？

A: 不建议，参数名称应该只使用英文字母、数字和下划线。

### Q: 密码类型的参数在 HTTP API 中安全吗？

A: HTTP API 使用明文传输，建议在受信任的网络中使用，或配合 HTTPS 使用。
