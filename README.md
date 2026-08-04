# Wollet

Wollet 是一个纯内网使用的 Windows 远程开关机工具。本仓库当前实现 Linux 服务端、内嵌管理页面和用于联调的模拟客户端。

名字 **Wollet** 由 **WOL**（Wake-on-LAN）和后缀 **-let** 组合而来，表达“小巧、专注的 Wake-on-LAN 工具”。

## 当前能力

- 单管理员登录与内存会话
- 五分钟有效、只能成功消费一次的绑定 Token
- 独立设备身份与凭据
- WebSocket 上线、15 秒心跳、45 秒离线判定和单连接替换
- 在线设备即时关机指令与客户端确认
- 离线设备 Wake-on-LAN
- SSE 实时管理页、多设备管理和凭据撤销
- SQLite 持久化、Docker Compose 和联调模拟器

## 安全提示

首版按已确认范围只提供 HTTP。管理员密码、绑定 Token 和设备凭据会以明文经过网络。只能在可信局域网或 VPN 中使用，禁止直接暴露到公网。

## Docker Compose 部署

要求 64 位 Linux、Docker 和 Compose 插件。WoL 依赖 Linux host network，因此该部署方式不面向 Docker Desktop。

```bash
cp .env.example .env
mkdir -p data
docker compose up -d --build
```

编辑 `.env`，至少设置：

- `WOLLET_ADMIN_USERNAME`
- `WOLLET_ADMIN_PASSWORD`：至少 12 字符
- `WOLLET_UID`、`WOLLET_GID`：需要能写入 `./data`

默认访问地址为 `http://<Linux 服务端 IP>:8080/`。数据库保存在 `./data/wollet.db`，备份前应停止容器或使用 SQLite 在线备份工具。

## 本地开发

```bash
export WOLLET_ADMIN_USERNAME=admin
export WOLLET_ADMIN_PASSWORD=development-password
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

## 配置

| 环境变量 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `WOLLET_ADMIN_USERNAME` | 是 | — | 单管理员用户名 |
| `WOLLET_ADMIN_PASSWORD` | 是 | — | 至少 12 字符 |
| `WOLLET_WOL_BROADCAST` | 否 | `255.255.255.255` | IPv4 广播地址；多网卡或特殊路由环境可设置为定向广播地址，例如 `192.168.1.255` |
| `WOLLET_LISTEN_ADDR` | 否 | `0.0.0.0:8080` | HTTP 监听地址 |
| `WOLLET_DB_PATH` | 否 | `/data/wollet.db` | SQLite 文件路径 |
| `WOLLET_WOL_PORT` | 否 | `9` | WoL UDP 端口 |
| `WOLLET_LOG_LEVEL` | 否 | `info` | `debug`、`info`、`warn` 或 `error` |

协议详见 [docs/protocol.md](docs/protocol.md)。

## 验证

```bash
go test ./...
go test -race ./...
go vet ./...
```
