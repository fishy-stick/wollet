# v1.1.0 同步关机协议设计

状态：扩展已实现，正在联合验收。日期：2026-09-19。

本文定义客户端主导执行的关机计划，供服务端、管理页面和 Windows 客户端共同实现。现行协议见 [protocol.md](../protocol.md)，版本范围见 [开发计划](../development-plan.md)，弹窗设计及实施步骤见 [同步关机开发规划](shutdown-countdown-implementation.md)。

## 1. 目标与范围

已确定的交互原则：

- 管理页面发起关机后，客户端自动开始倒计时并显示提示，无需本地用户点击确认。
- 客户端后台服务负责计时和到期执行，浏览器关闭不影响已接受的计划。
- 本地用户可以取消或立即关机。操作在客户端后台服务内裁决，不依赖服务端在线。
- 协议回执由程序自动发送，用于确认指令是否生效，不增加人工确认步骤。
- 将来若支持调整时间，客户端应自动应用修改，无需人工确认。
- 锁屏或无人登录时照常执行；有可交互桌面时自动显示提示并提供本地取消入口。
- 客户端后台服务重启或升级时取消尚未执行的计划，不在恢复后补关机。

`v1.1.0` 实现固定 10 秒倒计时、管理页面及本地弹窗的取消与立即执行、状态同步及旧版兼容。动态调整时长、自定义长时预约和休眠不属于本版本交付范围。

## 2. 职责与状态归属

| 组件 | 职责 |
| --- | --- |
| 管理页面 | 提交操作、显示已确认计划及请求进度，通过 SSE 跟踪变化 |
| 服务端 | 鉴权、串行转发同设备请求、记录请求和客户端快照、向页面广播状态 |
| 客户端后台服务 | 接受计划、维护唯一有效状态、计时、裁决取消与执行、调用 Windows 关机接口 |
| 客户端桌面提示进程 | 展示后台服务提供的剩余时间，将本地取消或立即执行操作交给后台服务 |

客户端是关机计划的最终状态来源。服务端的快照是副本，不能仅因自己的计时归零就发送第二条执行指令，也不能根据设备离线推断关机成功。

WebSocket 会话与计划生命周期分离。普通断线不会取消已经接受的计划；服务端离线期间，本地倒计时及取消继续工作。重新连接仅同步状态，不重新开始计时。

## 3. 兼容与能力协商

保持 `protocolVersion: 1`，在 `hello` 和 `ready` 增加可选 `capabilities`。旧协议的 `shutdown` / `shutdown_ack` 仍表示立即关机，不改变语义。

新版客户端发送：

```json
{
  "type": "hello",
  "protocolVersion": 1,
  "deviceName": "工作站",
  "macAddress": "A4:83:E7:19:2C:5A",
  "capabilities": ["shutdown-plan.v1"]
}
```

新版服务端返回双方支持的能力交集；协商成功时额外返回每次连接新生成的 `sessionId`：

```json
{
  "type": "ready",
  "protocolVersion": 1,
  "heartbeatIntervalSeconds": 15,
  "offlineAfterSeconds": 45,
  "capabilities": ["shutdown-plan.v1"],
  "sessionId": "connection-uuid"
}
```

- 字段缺失等价于不支持扩展。未知能力名称忽略；未协商的消息类型仍按现有规则拒绝。
- 仅双方协商成功才发送下文的新消息。新客户端连接旧服务端时，继续使用原有心跳和立即关机流程。
- 服务端设备视图增加 `capabilities`，供新版页面选择流程；离线时不据历史能力宣称当前可操作。
- 新客户端完成状态同步后，新服务端才开放该设备的计划操作。
- 旧客户端继续使用网页本地 10 秒倒计时及原关机 API，不显示客户端同步提示。
- 对已协商扩展的连接，禁止绕过计划流程发送旧 `shutdown`。旧页面调用旧关机 API 时返回 `409 shutdown_plan_required`，提示刷新页面。

旧服务端目前通过结构体读取握手字段，需用兼容测试确认增加可选字段不会导致旧版连接失败。

## 4. 计划与请求标识

| 字段 | 含义 |
| --- | --- |
| `operationId` | 一次关机计划的 UUID，由页面创建并作为创建请求的幂等键；同一设备重试必须复用 |
| `commandId` | 一次创建、取消或立即执行指令的 UUID，由服务端生成并记录，重试不更换 |
| `requestId` | 页面提交取消或立即执行时生成的 UUID，供 REST 重试去重 |
| `sessionId` | 指令所针对的当前 WebSocket 会话，避免跨连接重放 |
| `revision` | 客户端快照修订号，从 1 开始，每次状态变化递增 |
| `expectedRevision` | 取消或立即执行所依据的快照版本，用于检测竞争 |
| `sequence` | 当前连接内客户端快照的递增序号，防止同一修订号下旧的剩余时间覆盖新观察值 |

每台设备最多有一个非终态计划。已有计划时，新创建请求返回冲突，不覆盖或叠加倒计时。相同 ID、相同内容返回既有结果；相同 ID、不同内容返回 `idempotency_conflict`。

客户端按计划串行处理网络指令、本地取消、本地立即执行及计时到期。服务端不能自行增加客户端的 `revision`。收到较旧快照时可以结束对应请求的等待，但不得用其覆盖较新的状态。

客户端检查顺序为：当前会话和消息结构、已处理的指令 ID、计划状态与修订号。已处理指令直接返回原处理结果，不因计划已经进入终态而把成功重试改报为失败。`requestId` 按设备隔离，且与操作种类、计划 ID、请求内容共同校验。

UUID 字段必须是合法 UUID，示例中的描述性字符串仅用于说明。`revision`、`expectedRevision` 和 `sequence` 使用正整数并限制在 JSON 安全整数范围内；`remainingMilliseconds` 非负，本版本不超过 10000。创建时仅接受 `delaySeconds: 10`，缺失或其他值均拒绝。字段类型错误、过大的消息和未经协商的新消息不能被当作默认关机请求。

## 5. 状态模型

| 状态 | 含义 | 可用操作 |
| --- | --- | --- |
| `scheduled` | 计划已被客户端接受，正在倒计时 | 取消、立即执行 |
| `executing` | 客户端已越过取消边界，正在调用系统接口 | 查询 |
| `submitted` | Windows 已接受关机请求 | 查询；不等同于机器已经关闭 |
| `cancelled` | 计划已取消，计时器失效 | 查询 |
| `failed` | 计划已创建，但持久化或系统执行明确失败 | 查询 |
| `indeterminate` | 服务在执行边界崩溃，无法判断系统是否接受请求 | 查询，不自动重试 |

```text
创建 → scheduled → executing → submitted
            │           └──→ failed
            └──→ cancelled
```

`cancelled`、`submitted`、`failed` 和 `indeterminate` 均为终态。终态计划不能被重复指令重新激活。执行调用失败后不自动再次关机；新尝试需要新的计划。`submitted` 后本次客户端进程不再接受新关机计划，避免系统正在关机时重复提交。`indeterminate` 必须在页面明确展示，并由用户显式发起新操作，不能由重试机制自动创建计划。

取消与到期竞争时，以客户端串行处理的结果为准：取消先完成则不执行；进入 `executing` 后拒绝取消，返回 `too_late`。回执发送失败不回滚已生效的状态，也不阻塞本地取消或到期执行。

对已经 `cancelled` 的计划再次请求取消，可直接返回已取消快照，不改变修订号；其他终态拒绝变更。服务端串行化只覆盖请求登记和发送，不得在等待五秒回执期间持锁阻塞后续取消。客户端定时器必须绑定计划 ID 和修订号，失效计时器不能执行后来的计划。

## 6. 时间语义

创建指令传递 `delaySeconds: 10`。客户端接受时记录单调时钟起点，获得完整 10 秒倒计时；网络传输时间不侵占提示时间。

客户端快照包含 `remainingMilliseconds`，表示生成快照时的剩余时间。计时不依赖客户端墙上时钟，手动校时不改变计划。

服务端收到快照时，以自身时间计算用于展示的 `estimatedExecuteAt`，并在 REST/SSE 中提供 `serverTime`。页面根据两者的差值及本地单调时间显示剩余秒数；客户端桌面直接读取后台服务的剩余时间。连接正常时，每秒推送一次活动计划快照以校正显示，状态变化立即推送。

网络延迟会造成短暂显示差异，倒计时并不承诺两块屏幕每一帧完全一致。页面归零只表示预计到期，必须等待客户端状态才能显示“正在执行”或“已提交系统”。断线时页面标注状态未同步，不能把估计值当作执行结果。

## 7. WebSocket 消息

沿用已认证的连接、文本 JSON 和 4 KiB 单消息限制。所有新增消息仅在协商 `shutdown-plan.v1` 后使用。

### 7.1 创建计划

服务端发送：

```json
{
  "type": "shutdown_plan_create",
  "sessionId": "connection-uuid",
  "commandId": "command-uuid",
  "operationId": "operation-uuid",
  "delaySeconds": 10
}
```

客户端校验当前会话、固定时长及无其他活动计划后，建立计划并回执。有可交互桌面时自动展示倒计时；锁屏、无人登录或桌面提示异常不会阻止计划启动。此回执不等待用户交互或窗口创建完成。

### 7.2 取消与立即执行

```json
{
  "type": "shutdown_plan_cancel",
  "sessionId": "connection-uuid",
  "commandId": "cancel-command-uuid",
  "operationId": "operation-uuid",
  "expectedRevision": 1
}
```

立即执行使用相同结构，`type` 为 `shutdown_plan_execute`。客户端校验版本后，原子地取消计时器并进入 `executing`；定时器与立即执行不能各调用一次系统接口。

### 7.3 自动回执

```json
{
  "type": "shutdown_plan_result",
  "sessionId": "connection-uuid",
  "commandId": "command-uuid",
  "operationId": "operation-uuid",
  "accepted": true,
  "sequence": 1,
  "plan": {
    "operationId": "operation-uuid",
    "revision": 1,
    "state": "scheduled",
    "remainingMilliseconds": 10000
  }
}
```

拒绝时 `accepted: false`，增加 `code`，已知计划同时附当前快照。错误码包括 `active_plan_exists`、`revision_conflict`、`too_late`、`unknown_operation`、`invalid_delay`、`idempotency_conflict`、`storage_unavailable`。

取消接受回执必须附 `cancelled`；立即执行接受回执表示已进入 `executing`，后续系统成功或失败通过状态上报给出。接受执行不等于系统关机成功。重复回执的结果内容保持原值；附带快照的旧修订不能覆盖最新视图。

同一连接内重发缓存回执保留原 `sequence`，不能用新的序号包装旧的剩余时间。跨连接对账通过 `shutdown_plan_sync.result` 传递原结果，并另行生成当前计划快照。

普通业务拒绝不关闭连接。伪造会话、非法消息结构等协议错误沿用 policy violation 处理。首次创建被拒绝时 `plan` 可为 `null`，服务端记录拒绝结果，不虚构客户端计划。

### 7.4 状态上报

客户端自主发生的取消、到期或执行结果通过以下消息上报；活动计划每秒也发送同型快照：

```json
{
  "type": "shutdown_plan_state",
  "sessionId": "connection-uuid",
  "sequence": 2,
  "plan": {
    "operationId": "operation-uuid",
    "revision": 2,
    "state": "cancelled",
    "remainingMilliseconds": 0,
    "reason": "local_user"
  }
}
```

周期性快照不增加 `revision`。客户端串行生成并发送快照，每次新快照增加 `sequence`；同修订号仅接受更高序号的时间观察，不改变状态。`sequence` 在新会话重新从 1 开始，`revision` 随计划持久化。`reason` 包括 `local_user`、`remote_user`、`client_restarted`、`system_resumed`、`storage_error`、`system_error`、`execution_interrupted`；错误细节用于日志，避免暴露凭据。

快照可附 `presentation: visible | unavailable` 表示提示是否可见，该字段只是观察信息，不决定是否执行。锁屏期间接受的计划，在解锁且仍未到期时显示实际剩余时间，不重新开始十秒。

本地取消首先在后台服务内完成并关闭提示，再尝试发送快照；不等待网络回执。

本地立即执行通过 IPC 提交计划 ID 和当前修订号，由后台服务转入 `executing` 并使原计时器失效，再调用系统接口。它与远端立即执行共享同一状态转换，使用 `reason: local_user` 上报；进入执行阶段后禁用弹窗按钮，不等待服务端回执，也不直接在 UI 进程调用关机。

### 7.5 重连同步

`ready` 后，客户端先上报当前活动计划，再逐条发送保留期内尚需对账的终态和指令处理结果。统一使用 `shutdown_plan_sync`，每条最多包含一个计划和一个指令结果，遵守 4 KiB 限制。示例为没有待补发结果的活动计划同步：

```json
{
  "type": "shutdown_plan_sync",
  "sessionId": "new-connection-uuid",
  "sequence": 1,
  "plan": {
    "operationId": "operation-uuid",
    "revision": 1,
    "state": "scheduled",
    "remainingMilliseconds": 6000
  },
  "result": null,
  "complete": true
}
```

`result` 非空时包含原 `commandId`、`operationId`、`accepted`、可选 `code` 和该指令处理时的修订号。没有活动计划时，第一条的 `plan` 为 `null`；有后续记录时 `complete: false`。最后一条使用 `complete: true`。同步期间计时和本地取消照常运行，新状态与同步快照通过同一有序发送队列传输。

服务端持久化收到的计划或指令结果后，发送 `shutdown_plan_recorded`，携带当前 `sessionId`、对应的 `operationId`、`revision` 和可选 `commandId`。最后一批记录完成后发送：

```json
{
  "type": "shutdown_plan_synced",
  "sessionId": "new-connection-uuid"
}
```

客户端收到 `shutdown_plan_synced` 才结束同步阶段。终态记录和指令结果在保留期内可重复上报，不能仅因收到记录回执就删除所有对账依据。连接内正常状态上报同样适用记录回执；周期性剩余时间无需每秒持久化或确认收录。

服务端在同步完成前不下发新计划。同步用于恢复视图和解决结果未知的请求，不重发创建指令，不恢复服务端记忆中的倒计时。若客户端没有某个未决计划及记录，该请求仍为 `outcome_unknown`，不能判定为已取消或再次自动创建。未决请求不阻止对已有活动计划发起取消；执行新计划前则必须完成同步并确认没有活动计划。

客户端与旧服务端重连时，若本地仍有扩展计划，先本地取消并记录，再进入旧协议模式；不能同时运行扩展计划与旧立即关机指令。旧服务端不接收扩展状态消息。

## 8. 管理 API 与页面同步

新增接口沿用管理员认证、同源校验及统一错误格式：

| 接口 | 请求 | 行为 |
| --- | --- | --- |
| `POST /api/v1/devices/{id}/shutdown-plans` | `{ "operationId": "uuid" }` | 固定创建 10 秒计划 |
| `GET /api/v1/devices/{id}/shutdown-plans/{operationId}` | 无 | 获取请求进度和最新客户端快照 |
| `POST /api/v1/devices/{id}/shutdown-plans/{operationId}/cancel` | `{ "requestId": "uuid", "expectedRevision": 1 }` | 请求客户端取消 |
| `POST /api/v1/devices/{id}/shutdown-plans/{operationId}/execute` | 同上 | 请求立即执行 |

查询接口可携带 `?requestId=uuid` 获取指定取消或执行请求的结果；省略时获取创建请求结果及当前计划状态。所有查询校验设备与计划归属。请求已确认时的响应结构示例：

```json
{
  "serverTime": "2026-09-19T01:00:00Z",
  "request": {
    "operationId": "operation-uuid",
    "commandId": "command-uuid",
    "action": "create",
    "status": "confirmed"
  },
  "plan": {
    "operationId": "operation-uuid",
    "revision": 1,
    "state": "scheduled",
    "remainingMilliseconds": 8000,
    "observedAt": "2026-09-19T01:00:00Z",
    "estimatedExecuteAt": "2026-09-19T01:00:08Z",
    "synchronized": true
  }
}
```

取消或执行的 `request` 另外包含原 `requestId`。尚无客户端快照时 `plan: null`；断线、服务端重启后尚未完成对账或快照过期时，`synchronized: false`。活动计划连续 3 秒没有新观察即标记未同步，不必等待设备心跳超时。未知请求通过 `request.status` 表达，不伪造计划状态。

请求进度独立于计划状态：`pending`、`confirmed`、`rejected`、`outcome_unknown`。服务端先保存请求，再发送指令，最多等待自动回执 5 秒：

- 创建已接受返回 `201`；取消或立即执行已接受返回 `200`，附客户端最新快照。
- 已发送但 5 秒内未获回执返回 `202`，进度为 `outcome_unknown`，页面显示“结果尚未确认”，通过查询或 SSE 等待更新。
- 发送前设备离线返回 `409 device_offline`；能力未协商返回 `409 capability_required`；状态尚未同步返回 `409 client_sync_pending`。
- 客户端业务冲突返回 `409`，参数错误返回 `400`；存储不可用返回 `503` 并保留具体错误码。
- 浏览器超时或关闭不撤销已发送请求。重试沿用原 ID，查询结果，不产生新的指令或倒计时。
- 服务端不排队等待离线设备以后执行。发送结果存在歧义时按未知处理，不按“未发送”处理。

设备对象增加 `shutdownPlan` 和 `shutdownRequest`，并通过现有 `snapshot` / `device.updated` SSE 分发。响应提供 `serverTime`；计划附服务端估算的 `estimatedExecuteAt` 和最后同步时间 `observedAt`。旧的 `operation` 弱暂态不承担计划存储，断线清理弱暂态不能删除关机计划。

页面点击关机后立即请求创建计划，以客户端快照驱动显示，不再先自行倒计时十秒。刷新或新开页面从快照恢复显示。远端取消等待自动回执才显示成功，未知结果期间不得显示“安全取消”。

## 9. 故障与生命周期策略

### 已确定的行为

| 场景 | 行为 |
| --- | --- |
| 关闭管理页面 | 已接受计划继续执行 |
| WebSocket 断线、服务端重启 | 客户端继续计时，本地取消有效；重连同步最新状态 |
| 客户端后台服务重启、升级或系统重启 | 取消尚未执行的计划，不在重启后补关机；记录 `client_restarted` |
| 未登录、锁屏 | 照常接受并执行；出现可交互桌面且计划未到期时显示剩余时间 |
| 桌面提示进程退出或暂时不可用 | 后台计划继续；当前尚不上报提示是否可见，不等同于后台服务重启 |
| 弹窗关闭操作 | 不提供关闭或隐藏按钮；原生关闭消息、Alt+F4 和 Escape 转为本地取消请求，按后台服务处理结果结束窗口 |

### 桌面与电源策略

| 场景 | 当前行为 |
| --- | --- |
| 系统睡眠后恢复 | 取消未执行计划，记录 `system_resumed`，避免恢复后突然关机 |
| 多个交互会话 | 首版只向当前解锁的活动控制台会话展示并接受取消；其他会话策略后续扩展 |

删除设备或撤销凭据不等同于取消关机。管理端应提示已接受计划可能继续；客户端检测到凭据失效时取消活动计划。若客户端离线且未收到撤销信息，服务端无法保证阻止关机。

### 持久化与恢复

客户端持久化计划 ID、修订号、状态及已处理指令结果。计划的创建与对应成功结果必须原子写入受保护目录后才回执，避免崩溃后无法判断指令是否处理过。

- 写入失败时不接受新计划、不调用关机。到期时无法记录 `executing`，则在内存中转为 `failed` 并停止计时，报告 `storage_error`，不自动重试。
- 本地或远端取消始终优先停止本地计时。若取消记录写入失败，仍在当前进程保持取消且不能再次触发；启动恢复策略保证旧的 `scheduled` 记录不会被补执行。可回报已取消，同时附存储故障信息供维护。
- `executing` 写入后才调用系统接口。重启读取到 `executing` 时转为 `indeterminate`，原因为 `execution_interrupted`，绝不再次调用系统接口。
- 系统接口明确返回失败则记录 `failed`；明确接受则记录 `submitted`。调用结果在崩溃时丢失属于结果未知，不保证能够证明实际关机结果。
- 恢复到 `cancelled` 或 `indeterminate` 时增加修订号，并在下次连接同步。无法读取计划存储时暂不接受新计划，报告存储故障。

服务端持久化请求及最后已确认状态。在发送前记录请求，发送后收到回执才记录结果；不能把套接字写入成功当作客户端已接受。服务器在记录发送进度附近崩溃时，将未决请求标记为结果未知，依靠客户端同步对账，不自动重新发送。

客户端和服务端的终态幂等记录至少保留 24 小时。活动计划、未解决的执行结果及与其关联的请求不能因为达到保留期限而删除。客户端与页面均不能因回执超时生成新的操作 ID。服务端 REST 重试只查询原请求，不重新发指令；一条指令在 WebSocket 上最多主动发送一次。

客户端收到相同指令时仍须去重，作为边界保障。终态记录过期后不承诺旧请求幂等，调用方必须停止自动重试；查询已过期且可识别的请求返回 `410 result_expired`，无记录且无法识别的返回 `404`，两者均不表示已取消或执行成功。

本地提示进程通过受访问控制的 IPC 向后台服务请求取消或立即执行，不读取设备密钥，也不直接获得关机权限。后台服务验证真实调用身份、会话、计划 ID 和当前状态。IPC 传输单独设计，窗口采用开发规划中已确认的居中圆环倒计时布局。

## 10. 动态调整时间的扩展方向

未来可协商独立能力 `shutdown-plan.reschedule.v1`，增加 `shutdown_plan_reschedule`，携带 `operationId`、`commandId`、`expectedRevision` 和新的 `delaySeconds`。

建议将时长定义为“客户端接受修改后还剩多少秒”。修改由客户端串行应用并增加修订号，自动刷新倒计时并回执；进入执行阶段后拒绝修改。服务端或页面不能在回执前把原计划标记为已延长。断线时未送达的修改不影响原计划。

该能力和 REST 修改接口不在 `v1.1.0` 中实现或公布为可用能力；本版本遇到此类消息仍拒绝处理。

## 11. 实施与验收

实施顺序：先落实本节之前的状态及生命周期规则，编写协议契约测试；随后实现客户端计划引擎与桌面 IPC、服务端请求协调与状态存储，最后接入管理页面及模拟客户端。

必须覆盖：

- 新旧客户端与服务端组合，未协商时没有扩展消息，旧立即关机语义不变。
- 接受计划即自动显示，无人工确认；完整 10 秒倒计时仅发生一次。
- 页面关闭后继续执行；断线本地取消，重连后页面恢复为已取消。
- 创建、取消及立即执行重复发送不重复计时或关机；相同 ID 不同请求拒绝。
- 本地取消、远端取消、立即执行与到期竞争，系统接口最多调用一次。
- 自动回执丢失、HTTP 超时、服务端重启后不误报成功，不重新创建计划。
- 系统校时不改变剩余时长；高网络延迟仅影响展示，不缩短客户端提示时间。
- 客户端重启、系统睡眠恢复、锁屏、无桌面提示、存储失败按最终确定的策略处理。
- 锁屏和无人登录时照常执行；在倒计时结束前解锁，显示剩余时间而不是新的十秒。
- 桌面提示退出不取消计划；后台服务重启取消计划，二者不能混淆。
- 终态、旧修订及旧会话消息不会重新激活计划。
- `submitted` 与设备离线分别展示，不把网络断开当作系统关机成功。

涉及执行的自动化测试使用模拟关机控制器；真实关机仅在专用测试电脑完成最终验收。
