# Wollet v1 服务端协议

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
    "expiresAt": "2026-08-04T00:05:00Z"
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
