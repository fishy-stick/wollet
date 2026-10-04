# Wollet v1 服务端协议

本文描述基础协议。当前开发分支已实现 `v1.1.0` 的能力协商扩展，消息、管理 API 和状态语义见 [同步关机协议](design/shutdown-plan-protocol.md)；未协商该能力的连接继续使用本文的基础关机流程。

所有 JSON 时间均为 UTC RFC 3339。REST 错误统一为：

```json
{
  "error": {
    "code": "device_offline",
    "message": "设备当前离线"
  }
}
```

服务端不启用 CORS。管理员写操作必须携带与 `Host` 相同的 `Origin` 或 `Referer`；浏览器管理页会自动满足该要求。

## 管理员接口

### 登录与会话

- `POST /api/v1/auth/login`

  ```json
  { "username": "admin", "password": "..." }
  ```

  成功后设置 `wollet_session` HttpOnly、SameSite=Strict Cookie。登录每个来源 IP 每分钟最多尝试五次。

- `GET /api/v1/auth/session`：检查当前会话：

  ```json
  { "username": "admin", "authenticationEnabled": true }
  ```

- `POST /api/v1/auth/logout`：撤销当前会话，返回 `204`。

未设置 `WOLLET_ADMIN_PASSWORD` 时，管理员接口免认证，`authenticationEnabled` 为 `false`；同源校验仍然生效。此模式只适用于可信局域网或 VPN，服务启动时会输出警告日志。

### 绑定 Token

- `POST /api/v1/pairing-tokens`

  ```json
  {
    "token": "M7K4P-2N8QX-R6T9C-V3W5D",
    "expiresAt": "2026-08-04T00:05:00Z",
    "expiresInSeconds": 300
  }
  ```

Token 由 20 个 Crockford Base32 字符组成，破折号只用于显示。固定五分钟有效，明文只在本响应出现。服务端只保存 SHA-256；并发绑定时只有一个事务能成功消费 Token。

### 设备

设备对象：

```json
{
  "id": "0f5d92b0-7e5a-4bb8-9d95-0818b49b95ad",
  "name": "工作站",
  "macAddress": "A4:83:E7:19:2C:5A",
  "status": "online",
  "operation": "shutting_down",
  "lastSeenAt": "2026-08-04T00:00:00Z",
  "createdAt": "2026-08-03T23:50:00Z"
}
```

`status` 只表示实际 WebSocket 在线状态。可选的 `operation` 是服务端内存中的弱暂态：`waking` 最多保留 90 秒，`shutting_down` 最多保留 60 秒；设备上线或离线时会提前清除，服务重启后也不会恢复。暂态期间允许再次调用控制接口。

- `GET /api/v1/devices` → `{ "devices": [...] }`
- `POST /api/v1/devices/{id}/wake`
  - 仅离线设备可用。
  - 成功写入广播 UDP 套接字后返回 `{ "status": "sent", "operation": "waking" }`。
- `POST /api/v1/devices/{id}/shutdown`
  - 仅在线设备可用，不接受参数；Web 页面在调用接口前显示 10 秒倒计时，可取消或立即调用。
  - 服务端等待客户端五秒内确认，成功返回 `{ "status": "delivered", "commandId": "...", "operation": "shutting_down" }`。
  - 不保存、不排队，同设备同一时间只允许一个关机请求。
- `DELETE /api/v1/devices/{id}`
  - 删除凭据并立即断开当前连接，返回 `204`。

### 状态流

`GET /api/v1/events` 使用 Server-Sent Events：

- `snapshot`：连接及每次重连后的完整 `{ "devices": [...] }`。
- `device.updated`：完整设备对象。
- `device.wake_timeout`：90 秒内未检测到上线，设备对象中的弱暂态已清除。
- `device.shutdown_timeout`：60 秒内未检测到离线，设备对象中的弱暂态已清除。
- `device.removed`：`{ "id": "..." }`。
- 每 20 秒发送 SSE 注释作为 keepalive。

## Windows 客户端接口

### 绑定

`POST /api/v1/client/bind`

```json
{
  "token": "M7K4P-2N8QX-R6T9C-V3W5D",
  "deviceName": "工作站",
  "macAddress": "A4:83:E7:19:2C:5A"
}
```

成功返回：

```json
{
  "deviceId": "0f5d92b0-7e5a-4bb8-9d95-0818b49b95ad",
  "deviceSecret": "base64url-encoded-256-bit-secret"
}
```

设备密钥只返回一次。绑定接口每个来源 IP 每分钟最多尝试 30 次。

### 验证现有配置

`GET /api/v1/client/me` 使用：

```http
X-Wollet-Device-ID: <device-id>
Authorization: Bearer <device-secret>
```

成功返回当前设备对象。`401 invalid_device_credentials` 表示需要使用新 Token 重新绑定。

### 实时连接

`GET /api/v1/client/connect` 携带相同认证头并升级为 WebSocket。单条消息上限 4 KiB。

连接后十秒内客户端必须发送：

```json
{
  "type": "hello",
  "protocolVersion": 1,
  "deviceName": "工作站",
  "macAddress": "A4:83:E7:19:2C:5A"
}
```

服务端保存最新名称和 MAC，然后回应：

```json
{
  "type": "ready",
  "protocolVersion": 1,
  "heartbeatIntervalSeconds": 15,
  "offlineAfterSeconds": 45
}
```

客户端每 15 秒发送：

```json
{ "type": "heartbeat" }
```

关机指令：

```json
{ "type": "shutdown", "commandId": "uuid" }
```

客户端确认收到固定指令后立即回应，再执行系统关机：

```json
{ "type": "shutdown_ack", "commandId": "uuid" }
```

未知消息、错误协议版本、无效设备信息或无对应命令的确认会以 WebSocket policy violation 关闭。新连接替换同设备旧连接，旧连接关闭不能把新连接标记为离线。

## 在线状态

- 完成认证、hello、设备信息更新和 ready 后标记在线。
- WebSocket 断开或连续 45 秒没有心跳时标记离线。
- 在线状态只存在内存中；服务重启后设备先显示离线。
- `lastSeenAt` 在连接和心跳时持久化。

### 版本与功能兼容信息（v1.1.0）

`hello` 增加可选 `clientVersion`，表示当前后台服务的产品版本。`ready` 增加可选 `serverVersion` 和 `supportedCapabilities`，后者表示服务端完整支持能力；已有 `capabilities` 仍只表示本次连接协商结果。协议版本保持 1，旧端可忽略新增字段。

设备 REST 响应和 SSE 设备快照增加 `serverVersion`、`serverCapabilities`、`compatibility`。`compatibility` 包含 `kind`、`label`、两端显示版本、`missing`、`detail`、`historical`；每个缺失功能包含 `id`、`name`、`component` 和可选的目录目标 `target`。页面据此展示轻量标签，不根据产品版本切换关机协议。

`GET /api/v1/server-info` 沿用管理接口认证，返回 `{version, capabilities, known}`，用于没有设备时显示服务端版本。离线设备的版本标为历史信息，不根据离线的空能力集合推断需要升级。未提供有效版本时保留未知状态，已确认能力仍可参与功能判断。

功能目录与维护规则见 [版本信息与兼容提示](design/version-compatibility-hints.md)。

### 连接与客户端状态（v1.2.0 开发版）

本扩展已实现，协议版本仍为 1。连接 IP 和当前在线时长由服务端采集，不依赖客户端状态能力。`device-status.v1` 表示系统运行时长上报，与 `shutdown-plan.v1` 独立协商：客户端在 `hello.capabilities` 中声明，服务端在 `ready.capabilities` 中返回实际交集。`supportedCapabilities` 仅描述服务端支持，不能代替本次协商结果；未协商时客户端发送普通心跳。

协商成功后，在 ready 和可选的关机计划初始同步完成后立即上报一次状态，随后按 ready 指定心跳周期（默认 15 秒）上报：

```json
{
  "type":"heartbeat",
  "deviceStatus": {
    "systemUptimeMs":123456789
  }
}
```

`deviceStatus` 为完整快照，`systemUptimeMs` 可为 null；0 是有效时长。缺少或 null 的整个对象不更新样本；对象存在但字段缺失／null 则清除运行时长。时长必须为非负整数且不超过 9007199254740991。无效可选字段仅使该字段未知，不中断心跳和控制；错误日志限频。非法 JSON 和超出 4 KiB 限制仍按原规则处理。服务端忽略未协商的状态对象，仅当前连接可更新样本。客户端不发送 IP，额外的 `localIpAddress` 等未知字段被忽略，不能修改连接 IP。

所有设备视图（列表、`GET /api/v1/client/me`、SSE snapshot 及设备事件）使用同一结构，新增：

```json
{
  "serverTime":"2026-10-04T12:00:05Z",
  "connection": {
    "id":"本次连接的 UUID",
    "connectedAt":"2026-10-04T12:00:00Z",
    "durationMs":5000,
    "remoteIpAddress":"192.168.123.20"
  },
  "clientStatus": {
    "systemUptimeMs":123456789,
    "observedAt":"2026-10-04T12:00:00Z",
    "ageMs":5000,
    "fresh":true,
    "validForMs":40000
  }
}
```

`connection.id` 与关机计划 sessionId 独立。连接起点和样本保留单调时钟用于计算 durationMs／ageMs，墙钟仅用于 connectedAt／observedAt。所有在线客户端都有 connection；旧客户端或首样本尚未到达时 clientStatus 为 null。每次客户端重连生成新 connection，不因网页刷新或 SSE 重连而重置；离线时两对象均为 null，不持久化到数据库。

`connection.remoteIpAddress` 从已认证的 WebSocket 请求 `RemoteAddr` 读取并去除端口、规范化 IPv4／IPv6（IPv4 mapped 地址转为 IPv4）；无法解析、未指定或多播地址返回 null。局域网直连时这是设备连接所用 IP；NAT／反向代理下它表示服务端实际看到的对端，不保证是设备局域网地址。不采用 `X-Forwarded-For`、`X-Real-IP` 或客户端申报地址。旧客户端同样可用，运行时长样本过期不改变连接 IP；IPv6 zone 是服务端连接所在的作用域，不保证可由浏览器直接使用。

样本有效期沿用 offlineAfter（默认 45 秒）。普通心跳和计划状态不刷新样本年龄；过期时 fresh 为 false、validForMs 为 0。页面在样本剩余有效期内基于本地单调时间外推，过期后停止并标记待更新；收到已经过期的样本时显示原始采样值。SSE 中断、浏览器离线事件、后台切换或计时异常后暂停，恢复时以新快照校准，旧 HTTP 响应不能覆盖更新事件。

详细口径、兼容矩阵及验收记录见 [客户端网络状态规划](design/device-network-status.md)。
