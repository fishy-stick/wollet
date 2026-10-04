# 连接 IP、连接时长与系统运行时长

本文记录连接 IP、当前在线和系统运行时长的设计、协议与验证结果。发布版本和附件以 [GitHub Releases](https://github.com/fishy-stick/wollet/releases) 为准。

## 已确认范围

用户确认三项一起开发，并采用局域网直连部署。连接 IP 由服务端读取设备 WebSocket 的实际对端地址，客户端只上报系统运行时长。

| 页面字段 | 定义 | 数据来源 | 重置条件 |
| --- | --- | --- | --- |
| 连接 IP | 服务端实际看到的 WebSocket 对端 IP；局域网直连时为设备连接所用地址 | 已认证 WebSocket 请求的 `RemoteAddr` | 每次新连接重新采集 |
| 当前在线 | 当前这一次有效 WebSocket 连接持续时间 | 服务端连接注册时的单调时钟 | 断线重连、服务端或客户端服务重启 |
| 系统运行 | 自 Windows 本次内核启动起的累计时长 | 客户端原生 `GetTickCount64` | 完整系统重启；服务重启不清零 |

保留居中的 Logo、设备信息、电源按钮和底部设备切换布局。IP 与 MAC 同行；“当前在线”和“系统运行”用一行次要文字展示，信息图标展开计时口径。离线时移除 IP 和分隔线，MAC 单独居中；隐藏两种时长，改为“最后在线”或“从未上线”，与在线时长复用 `.device-timing` 的字号、颜色和间距。正常在线不再重复显示“连接正常”。版本和功能说明由“详情”入口展开，功能受限时保留文字提示。

时长不足一分钟时显示秒，达到一分钟后按天、小时、分钟简洁展示，省略零值单位；底层计时、样本有效期和刷新校准保持毫秒精度。IP 支持 IPv4／IPv6，不列举全部网卡，不改变设备凭据、MAC 或 Wake-on-LAN 目标。首版不保存历史 IP 或累计在线统计，不迁移数据库。未知显示“未知”，0 是有效运行时长。

连接 IP 与系统运行时长样本独立，旧客户端也有 IP 和当前在线。NAT／反向代理下仍展示服务端实际看到的对端，可能是出口或代理 IP；VPN 下展示相应连接地址，不保证是物理局域网网卡地址。首版不实现可信代理配置，不采用 `X-Forwarded-For`、`X-Real-IP` 或客户端申报 IP。

## 服务端地址采集

完成认证、hello 和 ready 后注册当前连接，从该 WebSocket 请求的 `RemoteAddr` 去除端口，以 `netip` 解析并规范化。IPv4 mapped 地址转为 IPv4；回环地址允许用于同机部署；未指定、多播或无法解析的地址返回未知。IPv6 zone 如存在，表示服务端连接所在作用域，不保证能从浏览器直接访问。

地址与会话绑定，其他 HTTP 请求、状态心跳或独立路由探测都不能覆盖它。重连重新采集，旧连接迟到消息不能修改新会话的 IP 或状态。REST、SSE 和客户端设备查询均从 Hub 的同一次加锁快照读取连接和样本。

Windows 客户端保留原有 `ClientWebSocket` 传输实现、直连、TLS 校验、超时和取消行为，无需专用套接字采集层。`RouteDeviceInfoProvider` 继续只负责名称与 MAC 探测，不用其探测结果推断本连接 IP。

参考：[Go net/http Request.RemoteAddr](https://pkg.go.dev/net/http#Request)。

## 系统运行时长口径

使用 Windows 原生 `GetTickCount64`，包含睡眠和休眠，不代表实际工作时间。快速启动可能延续内核会话，因此关机再开机不一定归零；完整重启应归零。电源场景的回归检查以这一计时口径为准。

不使用服务进程运行时间或客户端墙钟相减。没有采用 .NET 10 的 `Environment.TickCount64`，因为其在 Windows 上排除了非唤醒时间。采样失败返回未知并限频记录警告，不拖延心跳或中断控制连接。

参考：[GetTickCount64](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-gettickcount64)、[Environment.TickCount64](https://learn.microsoft.com/en-us/dotnet/api/system.environment.tickcount64?view=net-10.0)。

## WebSocket 协议

保持 `protocolVersion: 1`。`device-status.v1` 表示系统运行时长上报，与关机计划能力独立协商。客户端在 hello 声明，服务端在 `ready.capabilities` 返回交集。只看产品版本或 `supportedCapabilities` 不能认定已协商。

协商成功，在 ready 和可选的关机计划初始同步完成后立即发送首个状态心跳，随后按 ready 指定周期上报（默认 15 秒），不增加每秒网络推送：

```json
{
  "type":"heartbeat",
  "deviceStatus": { "systemUptimeMs":123456789 }
}
```

- 运行时长可为 null；数值必须是非负整数，且不超过 JavaScript 安全整数上限 9007199254740991。
- 缺少或 null 的整个对象表示没有新样本，保留旧样本直到过期；对象存在但字段缺失／null 表示本次未知，清除旧值。
- 无效可选字段仅使运行时长未知，保留基础心跳与控制，服务端日志限频。非法 JSON 或超出既有 4 KiB 限制仍按原规则处理。
- 不携带 IP 或客户端墙钟。服务端忽略未协商的状态对象及未知字段；客户端申报 IP 不能修改当前连接地址。
- 仅接受当前连接对象的状态；WebSocket 有序传输，不新增状态序号或复用关机计划序号。

## REST 与 SSE

设备列表、`GET /api/v1/client/me`、SSE snapshot 和设备事件共用设备视图：

```json
{
  "status":"online",
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

`connection.id` 独立于关机计划 sessionId，所有在线客户端都有 connection。连接起点和样本保留单调时间以计算 `durationMs`／`ageMs`，墙钟仅用于 `connectedAt`／`observedAt`。每次 WebSocket 重连生成新会话；页面刷新和 SSE 重连不重置连接。离线时两对象均为 null，不持久化到数据库。

旧客户端和首样本尚未到达时 `clientStatus` 为 null，但 IP 和当前在线已可用。运行时长样本 TTL 沿用 `offlineAfter`（默认 45 秒）；普通心跳和计划状态不刷新样本年龄。过期设 `fresh:false`、`validForMs:0`，不影响连接 IP。

## 页面刷新与计时

每次收到快照记录 `performance.now()` 本地锚点：当前在线外推 `connection.durationMs + 本地经过时间`；系统运行外推 `systemUptimeMs + ageMs + 本地经过时间`，只在新鲜、在线且同步时外推，并受剩余有效期限制。

每秒仅更新文字，不重建控制按钮，使用固定数字宽度避免计时抖动。SSE 中断、浏览器离线、后台切换或计时异常时冻结外推，IP 标为待同步，不擅自判设备离线。运行时长样本到期后停止外推并标为待更新；收到已经过期的样本只显示原始值和采样时间。连接 IP 不因运行时长过期而标为待更新。

返回前台或网络恢复时重建事件连接并补拉快照，重新校准。使用请求世代和设备事件计数丢弃过期 HTTP 响应，避免覆盖较新的 SSE 状态。

## 兼容与交付

| 组合 | 行为 |
| --- | --- |
| 新服务端 + 旧客户端 | IP 和当前在线可用，系统运行未知；现有控制不变 |
| 旧服务端 + 新客户端 | 未协商状态能力，不发送状态对象，沿用普通心跳 |
| 新服务端 + 新客户端 | 展示全部字段 |
| 旧网页 + 新服务端 | 忽略新增字段 |
| 新网页 + 旧服务端 | 新字段缺失时隐藏或未知，不报错 |

不修改绑定请求或凭据，不迁移数据库，不改变协议版本或已发布标签。功能目录登记独立的系统运行时长能力，IP／当前在线不依赖此能力。只维护正式版本的精确映射，不逐个登记 dev 标签。

## 验证记录（2026-10-04）

- Go 全套测试与 vet 通过，覆盖地址规范化、忽略转发头和客户端伪造 IP、首样本前及旧客户端地址、REST／SSE 一致性、无效运行时长、断线重连和旧连接隔离。
- .NET 客户端 72 项测试通过，覆盖 IPv4／IPv6／localhost、独立能力协商、旧服务端普通心跳、计划同步顺序、采样失败继续控制及原生运行时长取值。心跳不含 IP 字段。
- 9 项网页测试通过，覆盖仅更新文字、断流及离线冻结、后台切换、样本过期、未知／零值、过期响应丢弃和会话校准；旧客户端和过期样本仍展示连接 IP。
- Edge（Playwright）在 1280×900 和 390×844 下完成 12 项浏览器模拟检查，覆盖旧客户端 IP、刷新保留连接、断网及恢复、按钮焦点、取消关机、离线隐藏、重连和移动端布局。没有页面脚本错误，既有 favicon 404 保留。
- 两种 Windows 单文件发行形式已完成本机验证构建和版本资源检查。
- 模拟器运行时长由 `--system-uptime` 初始值累加，不代表宿主操作系统开机时间；IP 由服务端采集。`--device-status=false` 验证旧客户端状态，`--shutdown-plans=false` 独立验证旧关机协议。
- 该次自动化与模拟联调未覆盖真实 Windows 服务安装、服务重启、完整系统重启、睡眠／休眠／快速启动、校时及实际局域网双网卡、IPv4／IPv6 和 DNS／TLS 场景。Go race detector 因当前环境没有可用 C 编译环境而未运行。

验收要求：IP 始终对应本次连接对端；当前在线不因页面刷新重置，重连后重新计时；系统运行不因服务重启重置；离线或过期数据不持续增长；新旧组合保持基础控制可用。经过 NAT／代理时按对端语义验收，不要求还原设备局域网地址。
