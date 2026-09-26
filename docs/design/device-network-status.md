# 客户端 IP、连接时长与系统运行时长规划

状态：设计草案，2026-09-26；尚未实现。目标版本为 `v1.2.0`，在新版本发布，不纳入 `v1.1.0`。

## 已确认范围与展示

用户已确认：显示客户端连接网卡地址，同时展示本次连接时长和系统开机时长。

| 字段 | 定义 | 数据来源 | 重置条件 |
| --- | --- | --- | --- |
| 连接 IP | 当前 WebSocket 实际使用的客户端本地 IP，不是公网出口或服务端看到的代理地址 | 客户端连接套接字 | 每次新连接重新采集 |
| 本次在线 | 当前这一次有效 WebSocket 连接持续时间 | 服务端连接注册时的单调时钟 | 断线重连、服务端或客户端服务重启 |
| 系统运行 | 自 Windows 本次内核启动起的累计时长 | 客户端 Windows 原生计时 API | 完整系统重启；不因服务重启而清零 |

页面建议在 MAC 下方增加轻量信息行，例如 `IP 192.168.123.20`、`本次在线 2 小时 13 分 · 系统运行 1 天 6 小时`。标签明确区分两种计时；详情解释计时口径。只展示连接所用地址，不列举全部网卡；IPv4/IPv6 不双列，没有使用的地址不猜测。IP 是展示信息，不改变设备凭据、MAC 或 Wake-on-LAN 的目标逻辑。

离线后不再显示“当前 IP”或继续累加时长。首版不保存历史 IP/累计在线统计，沿用最后在线时间；未知字段显示“未知”或隐藏，不以 0 冒充未知。旧客户端仍可获得“本次在线”。

## 计时口径（建议采用，实施前验收）

系统运行时长包括睡眠和休眠期间，不代表实际工作时间。Windows 快速启动可能延续内核会话，因此“关机再开机”不一定归零；界面使用“系统运行”并注明口径，避免承诺“从按电源键开始计时”。完整重启应归零。

建议 P/Invoke 原生 `GetTickCount64`，不要直接采用 .NET 10 的 `Environment.TickCount64`：后者在 Windows 上排除了非唤醒时间。不能用服务进程启动时间，也不采用客户端当前时间减启动日期的方式，以免受校时影响。睡眠、休眠和快速启动行为需在目标 Windows 环境验收。

参考：[GetTickCount64](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount64)、[Environment.TickCount64](https://learn.microsoft.com/en-us/dotnet/api/system.environment.tickcount64?view=net-10.0)。

## 现有代码与改动边界

- `internal/devicehub/hub.go` 只记录活动连接和最后心跳，没有连接起点；新增会话元数据，使用连接对象身份防止旧连接覆盖新连接。
- `RouteDeviceInfoProvider` 已通过一次独立 TCP 探测定位网卡，但其本地地址未保留，且探测连接不保证与真正 WebSocket 的 DNS 结果、路由完全一致，不能直接称为实际连接 IP。
- IP 采集优先在真正 WebSocket 建连时获取底层 `Socket.LocalEndPoint`。可验证 `ClientWebSocket.ConnectAsync(uri, HttpMessageInvoker, token)` 配合 `SocketsHttpHandler.ConnectCallback` 的实现路径；保留直连、不继承代理的行为以及 TLS 校验、IPv4/IPv6 回退、超时和取消语义。先做小型验证，再替换现有连接建立代码。不能捕获准确端点时显示未知，不用第一块网卡地址兜底。
- 管理设备对象已经有 `serverTime`，REST/SSE 共用 `view`；扩展此结构即可，无需新查询接口。
- 旧 WebSocket 处理器拒绝未知消息类型，所以不引入未协商的新消息；使用现有能力协商与心跳扩展。

## WebSocket 协议改动

保持 `protocolVersion: 1`。新增能力 `device-status.v1`，表示支持客户端连接地址和系统运行时长快照。名字是本草案建议值。

客户端在现有 `hello.capabilities` 追加此能力。服务端在 `supportedCapabilities` 声明支持，在 `ready.capabilities` 返回实际协商交集。不能只检查产品版本，也不能仅看 `supportedCapabilities` 就认定协商成功。

```json
{"type":"hello","protocolVersion":1,"clientVersion":"<实际版本>","deviceName":"MPC","macAddress":"D8:43:AE:1F:58:2B","capabilities":["protocol.v1","shutdown-plan.v1","device-status.v1"]}
```

服务端保留现有 ready 字段，示意新增能力：

```json
{"type":"ready","protocolVersion":1,"heartbeatIntervalSeconds":15,"offlineAfterSeconds":45,"capabilities":["shutdown-plan.v1","device-status.v1"],"supportedCapabilities":["protocol.v1","shutdown-plan.v1","device-status.v1"]}
```

上例省略现有 `serverVersion`、关机计划 `sessionId` 等字段，实际必须继续携带。当前能力构造逻辑主要围绕关机计划，需改为独立协商两项能力，不能把状态上报绑定到关机计划是否启用。

协商成功且关机计划初始同步完成后，立即发送一次带状态的心跳；随后沿用 ready 指定的心跳周期（默认 15 秒），不增加每秒网络推送：

```json
{
  "type":"heartbeat",
  "deviceStatus": {
    "localIpAddress":"192.168.123.20",
    "systemUptimeMs":123456789
  }
}
```

语义与校验：

- `deviceStatus` 是一次完整快照；两个字段可为 `null`，分别代表本次无法采集。`0` 是合法时长，与缺失不同；Go 使用可空数值。
- 缺少整个对象表示本次没有新样本，保留旧样本直至过期；对象存在但字段为空则清除对应值，避免伪装成新鲜数据。
- IP 由 IP 解析器验证并规范化，支持 IPv4/IPv6，禁止携带端口、URL；IPv6 zone 如需展示须明确为客户端作用域，不推断浏览器可访问性。限制地址字符串长度（建议 64 字符）。回环地址可合法存在于同机部署，不应一概拒绝；未指定地址或多播地址不能作为有效连接 IP。
- 时长必须为非负整数且不超过 JavaScript 安全整数上限；客户端采样失败不应拖延心跳或断开控制连接。
- 合法心跳携带的无效可选状态只使相应展示字段未知，保留基础心跳与控制功能；日志限频。无效 JSON、超出既有 4 KiB 限制仍按原协议处理。
- 客户端不上报壁钟时间；服务端以接收时间记录样本，避免两端系统时间偏差。数值是近似实时展示，存在网络传输延迟，不用于关机或调度。
- WebSocket 已提供有序传输，首版不新增序号或复用关机计划序号；服务端必须只接受当前连接对象的心跳/状态，旧连接迟到的消息不能覆盖新会话。
- 断线重连重新采集实际套接字地址；连接不变时不要用另一次路由探测出的新地址覆盖当前连接 IP。

## REST 与 SSE 改动

设备对象新增两个可空对象，适用于设备列表、`GET /api/v1/client/me`、SSE snapshot 和 device.updated 等全部设备视图：

```json
{
  "status":"online",
  "serverTime":"2026-09-26T13:00:00Z",
  "connection": {
    "id":"服务端为每次连接生成的 UUID",
    "connectedAt":"2026-09-26T12:00:00Z",
    "durationMs":3600000
  },
  "clientStatus": {
    "localIpAddress":"192.168.123.20",
    "systemUptimeMs":123456789,
    "observedAt":"2026-09-26T12:59:55Z",
    "ageMs":5000,
    "fresh":true
  }
}
```

- `connection.id` 独立于关机计划 sessionId，旧客户端和不支持计划的连接同样拥有此值。
- `connectedAt` 是服务端完成 ready/注册在线时记录的 UTC 时间；`durationMs` 用服务端单调时间计算，避免运行期间校时使时长跳变。两者采样一致，服务端不能以设备记录的 `createdAt` 或最近心跳 `lastSeenAt` 代替。
- `clientStatus.systemUptimeMs` 是原始样本，`ageMs` 是服务端用单调时间计算的样本年龄；`observedAt` 用于说明采样时间。三个字段用于区分当前估计与上次采样，不混用。
- 新建连接到首样本之间 `clientStatus: null`；旧客户端也是 `null`。状态样本 TTL 建议沿用服务端 `offlineAfter`（默认 45 秒）。过期设 `fresh:false`，页面停止外推并标为“待更新”，不能因连接有其他消息而无限续期。
- 离线时 `connection: null`、`clientStatus: null`；不缓存到数据库。服务端重启后重建，客户端重连补齐。需要历史数据时另行设计，不混入本次需求。
- 当前连接状态、连接起点、样本应由 Hub 在一次加锁快照内读取，防止状态在线却返回旧会话时长。此信息复用现有设备认证/访问规则，不新增公开接口。

## 页面计时与刷新

每次收到设备快照，记录 `performance.now()` 作为本地锚点：

- 本次在线 ≈ `connection.durationMs + 本地经过时间`。
- 系统运行 ≈ `clientStatus.systemUptimeMs + clientStatus.ageMs + 本地经过时间`，只在 fresh 且连接在线时外推。
- 页面刷新、重新打开、SSE 重连均以新的服务端快照校准，不从网页打开时间重新计数。
- 每秒仅更新计时文字，使用固定数字宽度；不要每秒调用整个 `render()` 或重建关机按钮，避免干扰悬停动画和焦点。
- SSE 中断时标记“状态待同步”并暂停本地外推，不擅自把设备判离线。样本 TTL 到期、页面从后台恢复、电脑休眠唤醒后同样等待最新快照，不无限相信浏览器计时器。
- 返回前台时补拉设备快照；防止补拉的旧响应覆盖更新的 SSE 事件，可用请求世代/接收事件计数丢弃过期响应。网络恢复后重置锚点。

## 兼容、存储与交付顺序

| 组合 | 行为 |
| --- | --- |
| 新服务端 + 旧客户端 | 本次在线可用；IP/系统运行未知；现有控制不变 |
| 旧服务端 + 新客户端 | 未协商状态能力，不发送状态对象，沿用普通心跳 |
| 新服务端 + 新客户端 | 展示全部字段 |
| 旧网页 + 新服务端 | 忽略新增字段 |
| 新网页 + 旧服务端 | 新字段缺失时隐藏或未知，不报错 |

不修改绑定请求或凭据结构，不迁移数据库，不改变 protocolVersion，不修改已发布标签。能力目录增加此功能与当前实现 profile，但仍只维护已发布正式版本的精确映射，不逐个添加 dev 标签，也不预登记尚未发布的正式版。

实施分四步：

1. 服务端 Hub 记录连接起点、会话 ID、单调时长，扩展共用设备视图；先让旧客户端也具备准确本次在线时长。
2. 完成实际 WebSocket 本地端点和原生系统运行计时验证；Go/C# 定义可空状态结构、独立能力协商和首个/周期心跳样本。
3. 接入管理页面，完成未知/离线/过期状态、IPv6 排版、仅文本刷新和断流恢复。
4. 联调新旧组合，更新正式协议说明和发布验收记录；通过 `v1.2.0-dev.N` 构建验证，验收完成后随 `v1.2.0` 正式发布。

验收至少覆盖：页面刷新、多标签页、SSE 重连、WebSocket 重连、旧连接迟到消息、服务端/客户端服务重启、电脑完整重启、睡眠/休眠/快速启动、系统校时、双网卡、VPN、NAT/反向代理、IPv4/IPv6、DNS 多地址、采集失败/非法字段、旧客户端及旧服务端。连接 IP 必须与实际套接字一致；时长不倒退、不因页面刷新重置、不在离线后继续增长。

本草案未修改运行代码；系统运行时长的 Windows 电源行为及连接传输层实现仍需原型验证。
