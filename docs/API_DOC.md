# wxauto4 远程服务 API 文档

> 客户端通过 HTTP 与远程服务器交互，实现**任务下发执行**和**聊天记录上报**。
> 你的后端服务需按此文档实现以下接口。

---

## 通用说明

### Base URL

```
http://<your-server>:<port>
```

### 统一响应格式

```json
{
  "success": true,
  "message": "操作成功",
  "data": { ... }
}
```

### 客户端标识

每个客户端有唯一的 `client_id`，首次启动自动生成，用于：
- 任务认领时标记执行者
- 心跳上报时标识在线状态
- 聊天记录上报时标识来源

---

## 一、健康检查

### `GET /wxauto-api/health`

客户端测试服务器连通性。

**响应示例：**

```json
{
  "success": true,
  "message": "服务运行中",
  "data": { "status": "ok" }
}
```

---

## 二、任务管理

### 2.1 获取任务列表

### `GET /wxauto-api/tasks`

客户端定期轮询此接口拉取待执行任务。

**Query 参数：**

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| status | string | 否 | 筛选状态：`pending`/`running`/`completed`/`failed` |
| client_id | string | 否 | 按客户端ID筛选 |
| limit | int | 否 | 返回数量，默认20 |

**响应示例：**

```json
{
  "success": true,
  "message": "获取成功",
  "data": {
    "total": 2,
    "items": [
      {
        "task_id": "a1b2c3d4",
        "task_type": "send_msg",
        "params": { "who": "工作群", "msg": "下午3点开会" },
        "description": "发送会议通知",
        "status": "pending",
        "created_at": "2026-07-18 10:00:00",
        "finished_at": null,
        "result": null
      }
    ]
  }
}
```

### 2.2 认领任务

### `PUT /wxauto-api/tasks/{task_id}/claim`

客户端执行任务前先认领，防止重复执行。服务端应将任务状态从 `pending` 改为 `running`。

**请求体：**

```json
{ "client_id": "client-001" }
```

**响应示例：**

```json
{
  "success": true,
  "message": "认领成功",
  "data": { "task_id": "a1b2c3d4", "status": "running" }
}
```

### 2.3 提交任务结果

### `PUT /wxauto-api/tasks/{task_id}/result`

任务执行完成后，客户端将结果回传给服务器。客户端内置3次重试机制。

**请求体：**

```json
{
  "client_id": "client-001",
  "status": "completed",
  "duration": 2.35,
  "result": {
    "success": true,
    "message": "发送成功",
    "data": { "msg": "下午3点开会", "who": "工作群" }
  }
}
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| client_id | string | 是 | 客户端标识 |
| status | string | 是 | `completed` 或 `failed` |
| duration | float | 否 | 任务执行耗时（秒） |
| result | object | 是 | 执行结果，结构由任务类型决定 |

**响应示例：**

```json
{
  "success": true,
  "message": "更新成功",
  "data": { "task_id": "a1b2c3d4", "status": "completed", "finished_at": "2026-07-18 10:01:30" }
}
```

---

## 三、聊天记录上报

### `POST /wxauto-api/chat/records`

客户端将群聊消息上报到服务器。支持批量上报和单条上报两种格式。

**请求体（批量）：**

```json
{
  "client_id": "client-001",
  "records": [
    {
      "group_name": "工作群",
      "messages": [
        { "attr": "friend", "type": "text", "sender": "张三", "content": "收到，马上到" },
        { "attr": "self", "type": "text", "sender": "", "content": "下午3点开会" }
      ]
    }
  ]
}
```

**请求体（单条）：**

```json
{
  "client_id": "client-001",
  "group_name": "工作群",
  "messages": [
    { "attr": "friend", "type": "text", "sender": "张三", "content": "收到，马上到" }
  ]
}
```

**消息字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| attr | string | 消息来源：`self`(自己) / `friend`(他人) / `system`(系统) |
| type | string | 消息类型：见下方消息类型表 |
| sender | string | 发送者昵称（群聊中有效） |
| content | string | 消息文本内容 |
| time | string | 可选，格式 `yyyy-mm-dd HH:MM:SS` |

**消息类型 type 取值：**

| type | 说明 |
|------|------|
| text | 文本消息 |
| image | 图片消息 |
| file | 文件消息 |
| voice | 语音消息 |
| video | 视频消息 |
| link | 链接消息 |
| quote | 引用消息 |
| merge | 合并转发消息 |
| emotion | 表情消息 |
| location | 位置消息 |
| note | 笔记消息 |
| personal_card | 个人名片消息 |
| time | 时间消息 |
| other | 其他消息 |

**响应示例：**

```json
{ "success": true, "message": "上传成功，共2条记录", "data": null }
```

---

## 四、心跳

### `POST /wxauto-api/heartbeat`

客户端定期（30秒）上报心跳，告知服务器自身在线状态。

**请求体：**

```json
{
  "client_id": "client-001",
  "info": {
    "wx_online": true,
    "nickname": "我的昵称"
  }
}
```

**响应示例：**

```json
{ "success": true, "message": "心跳已记录", "data": null }
```

---

## 五、支持的任务类型（15种）

客户端可识别并执行以下任务类型，后端按需下发：

### 5.1 消息类

| task_type | 说明 | params | result.data |
|-----------|------|--------|-------------|
| `send_msg` | 发送文本消息 | `who`(发给谁), `msg`(内容), `at`(@对象，字符串或列表) | `{ "msg": "...", "who": "..." }` |
| `send_file` | 发送文件 | `who`, `filepath`(文件绝对路径) | `{ "filepath": "...", "who": "..." }` |
| `send_url_card` | 发送URL卡片 | `who`, `url`, `title`, `desc` | 无 |

### 5.2 获取类

| task_type | 说明 | params | result.data |
|-----------|------|--------|-------------|
| `get_messages` | 获取聊天消息 | `who`(可选) | `{ "who": "...", "messages": [...] }` |
| `get_history` | 获取历史消息 | `who`(可选) | `{ "who": "...", "messages": [...] }` |
| `get_chat_info` | 获取聊天窗口信息 | `who`(可选) | 聊天信息dict |
| `get_sessions` | 获取会话列表 | 无 | `{ "sessions": [...] }` |
| `get_my_info` | 获取自己的信息 | 无 | 用户信息dict |
| `get_recent_groups` | 获取最近群聊列表 | 无 | `{ "groups": [...] }` |

### 5.3 好友类

| task_type | 说明 | params | result.data |
|-----------|------|--------|-------------|
| `add_friend` | 添加好友 | `keyword`(微信号/手机号), `addmsg`(招呼语), `remark`(备注) | `{ "keyword": "..." }` |

### 5.4 群管理类

| task_type | 说明 | params | result.data |
|-----------|------|--------|-------------|
| `set_group_name` | 修改群名 | `group`(群名), `new_name`(新群名) | 无 |
| `set_group_announcement` | 设置群公告 | `group`(可选), `announcement`(内容) | 无 |
| `add_group_members` | 添加群成员 | `group`(可选), `members`(列表) | 无 |
| `create_group` | 创建群聊 | `members`(列表，≥2人) | 无 |

### 5.5 聊天记录上报类

| task_type | 说明 | params | result.data |
|-----------|------|--------|-------------|
| `upload_chat` | 获取聊天记录并自动上报服务器 | `who`(聊天对象名) | `{ "who": "...", "messages": [...], "auto_upload": true }` |

> `upload_chat` 执行后会自动调用 `POST /wxauto-api/chat/records` 将消息上报到服务器，无需后端再额外处理。

### 每种任务 result 完整示例

**send_msg 成功：**
```json
{ "success": true, "message": "发送成功", "data": { "msg": "下午3点开会", "who": "工作群" } }
```

**send_msg 失败：**
```json
{ "success": false, "message": "消息内容为空" }
```

**get_messages 成功：**
```json
{
  "success": true,
  "message": "获取成功",
  "data": {
    "who": "工作群",
    "messages": [
      { "attr": "friend", "type": "text", "sender": "张三", "content": "收到" },
      { "attr": "self", "type": "text", "sender": "", "content": "下午3点开会" },
      { "attr": "friend", "type": "image", "sender": "李四", "content": "[图片]" }
    ]
  }
}
```

**get_chat_info 成功：**
```json
{
  "success": true,
  "message": "获取成功",
  "data": { "chat_type": "group", "chat_name": "工作群", "group_member_count": 500 }
}
```

---

## 六、交互流程

### 任务执行流程

```
服务器                              客户端(wxauto4)
  |                                   |
  |<------- GET /wxauto-api/tasks ----------|  1. 轮询拉取pending任务(间隔可配)
  |-------- 返回任务列表 ----------->|
  |                                   |
  |<-- PUT /wxauto-api/tasks/{id}/claim ----|  2. 认领任务(状态→running)
  |-------- 认领成功 --------------->|
  |                                   |  3. 执行任务(操作微信)
  |<-- PUT /wxauto-api/tasks/{id}/result ---|  4. 提交结果(含duration，状态→completed/failed)
  |-------- 更新成功 --------------->|     (失败自动重试3次)
```

### 聊天记录上报流程

```
服务器                              客户端(wxauto4)
  |                                   |
  |                                   |  方式1: 手动点击"上报服务器"
  |                                   |  方式2: 监听时勾选"自动上报"
  |                                   |  方式3: 服务器下发upload_chat任务
  |<-- POST /wxauto-api/chat/records -------|  批量上报聊天记录
  |-------- 上传成功 --------------->|     (失败本地缓存，60秒自动重试)
```

### 心跳流程

```
服务器                              客户端(wxauto4)
  |                                   |
  |<--- POST /wxauto-api/heartbeat ---------|  每30秒心跳(连接微信后自动启动)
  |-------- 记录成功 --------------->|
```

---

## 七、错误码

| HTTP状态码 | 说明 |
|-----------|------|
| 200 | 成功 |
| 201 | 创建成功 |
| 400 | 请求参数错误 |
| 404 | 资源不存在(如任务ID不存在) |
| 500 | 服务器内部错误 |

---

## 八、客户端容错机制

| 场景 | 客户端行为 |
|------|-----------|
| HTTP请求失败 | 自动重试3次，递增延迟(1s/2s/3s) |
| 任务结果反馈失败 | 自动重试3次，递增延迟(2s/4s/6s) |
| 聊天记录上报失败 | 本地缓存(最多50条)，60秒后自动重传 |
| 服务器不可达 | 轮询/心跳继续运行，不中断客户端 |
| 微信断开 | 停止所有后台线程(监听/轮询/心跳/自动刷新) |
| 任务认领失败 | 跳过该任务，不执行 |

---

## 九、客户端本地管理 API

客户端自身也提供 HTTP API（默认端口 5000），用于本地管理和监控任务轮询服务。

### 9.1 查看轮询服务状态

### `GET /api/task-polling/status`

**响应示例：**

```json
{
  "enabled": true,
  "running": true,
  "executing": false,
  "client_id": "wxauto-a1b2c3d4e5f6",
  "server_url": "http://localhost:8080",
  "current_task": null,
  "cached_results": 0
}
```

| 字段 | 类型 | 说明 |
|------|------|------|
| enabled | bool | 服务是否已注册 |
| running | bool | 轮询服务是否运行中 |
| executing | bool | 是否正在执行任务 |
| client_id | string | 客户端唯一标识 |
| server_url | string | 远程服务器地址 |
| current_task | object/null | 当前执行中的任务（task_id, task_type, description） |
| cached_results | int | 缓存的未回传结果数 |

### 9.2 启动轮询服务

### `POST /api/task-polling/start`

**响应示例：**

```json
{ "message": "任务轮询服务已启动" }
```

### 9.3 停止轮询服务

### `POST /api/task-polling/stop`

**响应示例：**

```json
{ "message": "任务轮询服务已停止" }
```

### 9.4 手动触发一次轮询

### `POST /api/task-polling/trigger`

立即执行一次"拉取 → 认领 → 执行 → 回传"周期，无需等待轮询间隔。

**响应示例（有任务）：**

```json
{
  "success": true,
  "message": "执行完成",
  "duration": 2.35
}
```

**响应示例（无任务）：**

```json
{ "message": "当前无可执行任务" }
```

### 9.5 测试远程服务器连通性

### `GET /api/task-polling/health-check`

**响应示例：**

```json
{
  "server_reachable": true,
  "server_url": "http://localhost:8080"
}
```

### 9.6 查看缓存的未回传结果

### `GET /api/task-polling/cached-results`

**响应示例：**

```json
{
  "count": 1,
  "items": [
    {
      "taskId": "a1b2c3d4",
      "success": true,
      "message": "执行完成",
      "cachedAt": "2026-07-21T10:30:00",
      "retryCount": 2
    }
  ]
}
```

### 9.7 手动重传缓存结果

### `POST /api/task-polling/flush-cache`

尝试将所有缓存的结果回传到远程服务器。

**响应示例：**

```json
{
  "message": "已回传 1 条缓存结果",
  "flushed": 1
}
```

---

## 十、任务轮询配置

在 `appsettings.json` 中配置：

```json
{
  "TaskPolling": {
    "Enabled": false,
    "ServerUrl": "http://localhost:8080",
    "PollIntervalSeconds": 10,
    "HeartbeatIntervalSeconds": 30,
    "ClientId": ""
  }
}
```

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| Enabled | bool | false | 是否自动启动轮询服务 |
| ServerUrl | string | http://localhost:8080 | 远程服务器地址 |
| PollIntervalSeconds | int | 10 | 轮询间隔（秒） |
| HeartbeatIntervalSeconds | int | 30 | 心跳间隔（秒） |
| ClientId | string | (自动生成) | 客户端标识，留空自动生成并持久化到 client_id.txt |

> ClientId 首次启动自动生成格式 `wxauto-{12位hex}`，保存在程序目录下的 `client_id.txt` 文件中。
