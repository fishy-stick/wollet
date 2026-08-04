# Wollet

Wollet 是一个纯内网使用的 Windows 远程开关机工具。本仓库包含 Linux 服务端、内嵌管理页面、Windows 客户端和用于联调的模拟客户端。

名字 **Wollet** 由 **WOL**（Wake-on-LAN）和后缀 **-let** 组合而来，表达“小巧、专注的 Wake-on-LAN 工具”。

## 当前能力

- 可选的单管理员登录与内存会话；未设置密码时允许免认证管理并输出启动警告
- 五分钟有效、只能成功消费一次的绑定 Token
- 独立设备身份与凭据
- WebSocket 上线、15 秒心跳、45 秒离线判定和单连接替换
- 在线设备 10 秒倒计时关机、客户端确认和可重复操作的弱暂态提示
- 离线设备 Wake-on-LAN
- SSE 实时管理页、多设备管理和凭据撤销
- SQLite 持久化、Docker Compose 和联调模拟器

## 安全提示

首版按已确认范围只提供 HTTP。管理员密码、绑定 Token 和设备凭据会以明文经过网络。只能在可信局域网或 VPN 中使用，禁止直接暴露到公网。未设置 `WOLLET_ADMIN_PASSWORD` 时，局域网内任何能访问服务的人都可以控制设备。

## Docker Compose 部署

要求 64 位 Linux、Docker 和 Compose 插件。WoL 依赖 Linux host network，因此该部署方式不面向 Docker Desktop。

```bash
mkdir -p data
docker compose up -d --build
```

Compose 已为所有服务参数提供默认值，不创建 `.env` 也能启动。此时管理员认证关闭，启动日志会输出安全警告。需要启用登录或调整网络参数时：

```bash
cp .env.example .env
```

然后编辑 `.env`：

- 将 `WOLLET_ADMIN_PASSWORD` 设置为至少 12 字符的独立密码即可启用登录认证；留空则保持免认证。
- `WOLLET_ADMIN_USERNAME` 默认是 `admin`。
- `WOLLET_UID`、`WOLLET_GID` 应与 `./data` 目录的所有者一致，默认都是 `1000`。
- 多网卡环境可将 `WOLLET_WOL_BROADCAST` 改为定向广播地址，例如 `192.168.1.255`。

可用 `docker compose config` 查看插值后的最终配置，用 `docker compose logs wollet` 确认是否出现免认证警告。默认访问地址为 `http://<Linux 服务端 IP>:8080/`。数据库保存在 `./data/wollet.db`，备份前应停止容器或使用 SQLite 在线备份工具。

## Web 管理交互

- Token 弹窗可分别复制 Token 和当前浏览器访问的服务器地址；设备成功绑定后弹窗自动关闭。
- 点击关机后先显示 10 秒倒计时，期间可以取消或立即发送关机指令。
- 唤醒指令发送后最多显示 90 秒“开机中”，关机指令送达后最多显示 60 秒“关机中”。这些是弱暂态提示，按钮仍可点击；再次操作会先提示已经发送过请求。
- 最终开关机结果仍以 Windows 客户端 WebSocket 上线或离线为准。

## 本地开发

```bash
export WOLLET_ADMIN_USERNAME=admin
export WOLLET_ADMIN_PASSWORD=development-password # 删除此行即可免认证启动
export WOLLET_DB_PATH=./data/wollet.db
go run ./cmd/wollet serve
```

构建两个程序：

```bash
go build ./cmd/wollet
go build ./cmd/wollet-sim
```

健康检查：

```bash
./wollet healthcheck --url http://127.0.0.1:8080/readyz
```

## 模拟 Windows 客户端

先在管理页生成 Token，然后绑定一个模拟设备：

```bash
./wollet-sim bind \
  --server http://127.0.0.1:8080 \
  --token M7K4P-2N8QX-R6T9C-V3W5D \
  --name 工作站 \
  --mac A4:83:E7:19:2C:5A \
  --config ./data/workstation-sim.json
```

保持设备在线并接收关机指令：

```bash
./wollet-sim run --config ./data/workstation-sim.json
```

模拟器确认关机指令后默认退出，使设备状态变为离线；它不会关闭当前操作系统。

## Windows 客户端开发

Windows 客户端位于 [`clients/windows`](clients/windows/README.md)，使用 C#、.NET 10 和 WinForms。当前环境可以构建核心逻辑并交叉发布 Windows x64 单文件，Service、DPAPI、UAC 和真实关机仍需在 Windows 10/11 上验证。

## 配置

| 环境变量 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `WOLLET_ADMIN_USERNAME` | 否 | `admin` | 启用认证时使用的单管理员用户名 |
| `WOLLET_ADMIN_PASSWORD` | 否 | — | 设置至少 12 字符时启用认证；未设置则免认证并输出启动警告 |
| `WOLLET_WOL_BROADCAST` | 否 | `255.255.255.255` | IPv4 广播地址；多网卡或特殊路由环境可设置为定向广播地址，例如 `192.168.1.255` |
| `WOLLET_LISTEN_ADDR` | 否 | `0.0.0.0:8080` | HTTP 监听地址 |
| `WOLLET_DB_PATH` | 否 | `/data/wollet.db` | SQLite 文件路径 |
| `WOLLET_WOL_PORT` | 否 | `9` | WoL UDP 端口 |
| `WOLLET_LOG_LEVEL` | 否 | `info` | `debug`、`info`、`warn` 或 `error` |

Compose 还读取 `WOLLET_UID` 和 `WOLLET_GID`（默认均为 `1000`）来运行容器并写入绑定挂载的 `./data` 目录。`WOLLET_DB_PATH` 在 Compose 中固定为 `/data/wollet.db`。

协议详见 [docs/protocol.md](docs/protocol.md)。

## 验证

```bash
go test ./...
go test -race ./...
go vet ./...
```
