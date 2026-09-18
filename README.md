<div align="center">

<img src="./internal/webui/assets/wollet.svg" alt="Wollet" width="96" height="96">

# Wollet

**在浏览器中唤醒与安全关闭局域网内的 Windows 电脑**

[![Release](https://github.com/fishy-stick/wollet/actions/workflows/release.yml/badge.svg)](https://github.com/fishy-stick/wollet/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/fishy-stick/wollet?display_name=tag&style=flat-square)](https://github.com/fishy-stick/wollet/releases/latest)
[![Container](https://img.shields.io/badge/container-ghcr.io%2Ffishy--stick%2Fwollet-2496ED?style=flat-square&logo=docker&logoColor=white)](https://github.com/fishy-stick/wollet/pkgs/container/wollet)

[功能](#功能) · [快速开始](#快速开始) · [配置](#配置) · [从源码开发](#从源码开发) · [通信协议](#通信协议)

</div>

Wollet 是一套面向家庭和小型局域网的 Windows 远程开关机工具。Linux 服务端提供管理页面、设备状态和 Wake-on-LAN；Windows 客户端作为后台服务保持连接并接收关机指令。设备通过五分钟有效的一次性 Token 完成绑定，并分别持有独立凭据。

```text
浏览器管理页 ── HTTP / SSE ──> Wollet 服务端（Linux、Go、SQLite）
                                  ├── UDP Magic Packet ──> 离线电脑：唤醒
                                  └── WebSocket <────────> 在线电脑：状态与关机
```

## 功能

- 在一个响应式网页中管理多台 Windows 电脑
- 使用 Wake-on-LAN 唤醒离线设备
- 关机前显示 10 秒倒计时，可取消或立即执行
- 实时显示在线、离线、开机中和关机中状态
- 使用仅显示一次、五分钟有效的 Token 添加设备
- 支持可选的管理员登录，以及登录与绑定请求限流
- 使用 SQLite 持久化，无需部署额外数据库
- 提供 `linux/amd64`、`linux/arm64` 容器镜像和 Windows x64 单文件客户端

> [!IMPORTANT]
> Wollet 服务端直接提供 HTTP 和 WebSocket，不负责 TLS 终止。它只适合可信局域网或 VPN；不要将服务端端口直接暴露到公网。跨网络访问时，请使用可信 VPN，或在服务端前配置 HTTPS 反向代理。

## 快速开始

### 准备工作

- 一台 64 位 Linux 主机，用于运行 Docker Engine 和 Docker Compose
- 与目标电脑网络可达，并允许向目标网段发送 Wake-on-LAN 广播
- 已在目标电脑的 BIOS/UEFI 和网卡设置中启用 Wake-on-LAN
- 安装 Windows 客户端时具有管理员权限

> [!NOTE]
> Wollet 可以发送 Magic Packet，但不能代替你开启电脑固件、网卡驱动或交换机中的 Wake-on-LAN 支持。

### 部署服务端

创建一个用于部署的目录：

```bash
mkdir -p wollet/data
cd wollet
```

将以下内容保存为 `compose.yaml`：

```yaml
services:
  wollet:
    image: "${WOLLET_IMAGE:-ghcr.io/fishy-stick/wollet:latest}"
    network_mode: host
    user: "${WOLLET_UID:-1000}:${WOLLET_GID:-1000}"
    environment:
      WOLLET_ADMIN_USERNAME: "${WOLLET_ADMIN_USERNAME:-admin}"
      WOLLET_ADMIN_PASSWORD: "${WOLLET_ADMIN_PASSWORD:-}"
      WOLLET_LISTEN_ADDR: "${WOLLET_LISTEN_ADDR:-0.0.0.0:8080}"
      WOLLET_DB_PATH: /data/wollet.db
      WOLLET_WOL_BROADCAST: "${WOLLET_WOL_BROADCAST:-255.255.255.255}"
      WOLLET_WOL_PORT: "${WOLLET_WOL_PORT:-9}"
      WOLLET_LOG_LEVEL: "${WOLLET_LOG_LEVEL:-info}"
    volumes:
      - ./data:/data
    restart: unless-stopped
    read_only: true
    security_opt:
      - no-new-privileges:true
    cap_drop:
      - ALL
    stop_grace_period: 15s
```

在同一目录创建 `.env`，至少设置管理员密码；如果部署用户的 UID 或 GID 不是 `1000`，也应在此修改：

```dotenv
WOLLET_ADMIN_PASSWORD='replace-with-a-long-random-password'
WOLLET_UID=1000
WOLLET_GID=1000
```

拉取并启动发布镜像：

```bash
docker compose pull
docker compose up -d
```

打开 `http://<Linux 服务端 IP>:8080/`，然后查看服务状态或日志：

```bash
docker compose ps
docker compose logs -f wollet
```

Compose 默认使用 `ghcr.io/fishy-stick/wollet:latest`，数据库保存在 `./data/wollet.db`。

> [!TIP]
> 长期运行时，建议把 `.env` 中的 `WOLLET_IMAGE` 固定到 `vX.Y.Z` 版本，避免下次部署时意外升级。修改配置后可先运行 `docker compose config` 检查最终值。

### 安装 Windows 客户端

从 [GitHub Releases](https://github.com/fishy-stick/wollet/releases/latest) 下载 Windows x64 客户端：

| 文件 | 适用场景 |
| --- | --- |
| `wollet-client-win-x64-runtime.exe` | 推荐；包含 .NET Runtime，下载后可直接运行 |
| `wollet-client-win-x64-framework-dependent.exe` | 文件更小；需要预先安装 x64 .NET 10 Desktop Runtime |

1. 在 Wollet 管理页面生成绑定 Token，并复制服务器地址。
2. 以管理员身份运行客户端，填入服务器地址和 Token。
3. 点击“安装并绑定”。客户端会安装为 Windows 后台服务并自动连接服务器。
4. 返回管理页面；设备显示在线后即可远程控制。

再次运行客户端可以检查服务状态、修复现有安装或卸载服务。客户端配置位于 `%ProgramData%\Wollet`，设备密钥由 Windows DPAPI 保护。

Windows 客户端的绑定、状态检查和后台连接均直连服务端，不使用系统代理或代理环境变量。

## 配置

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `WOLLET_IMAGE` | `ghcr.io/fishy-stick/wollet:latest` | Compose 使用的服务端镜像 |
| `WOLLET_ADMIN_USERNAME` | `admin` | 管理员用户名 |
| `WOLLET_ADMIN_PASSWORD` | 空 | 非空时启用登录；留空时禁用管理员认证 |
| `WOLLET_LISTEN_ADDR` | `0.0.0.0:8080` | HTTP 监听地址 |
| `WOLLET_DB_PATH` | `/data/wollet.db` | SQLite 数据库路径；Compose 中固定为此值 |
| `WOLLET_WOL_BROADCAST` | `255.255.255.255` | Wake-on-LAN IPv4 广播地址；多网卡环境可使用定向广播地址 |
| `WOLLET_WOL_PORT` | `9` | Wake-on-LAN UDP 端口 |
| `WOLLET_LOG_LEVEL` | `info` | `debug`、`info`、`warn` 或 `error` |
| `WOLLET_UID` | `1000` | Compose 容器用户 UID，应能写入 `./data` |
| `WOLLET_GID` | `1000` | Compose 容器用户 GID，应能写入 `./data` |

`WOLLET_ADMIN_PASSWORD` 为空时，管理页面和管理员 API 不要求登录，服务端会在启动日志中发出警告。除隔离的测试网络外，请设置独立且不易猜测的密码。

备份数据库前，建议停止容器；如需不停机备份，请使用 SQLite 在线备份工具。

## 安全设计

- 管理员会话使用 `HttpOnly`、`SameSite=Strict` Cookie，默认有效期为 12 小时。
- 管理员写操作要求同源 `Origin` 或 `Referer`，服务端不启用 CORS。
- 登录按来源 IP 限制为每分钟 5 次，设备绑定限制为每分钟 30 次。
- 一次性 Token 的明文只返回一次，服务端仅保存其 SHA-256 哈希。
- 每台设备使用独立的 256 位密钥；服务端保存哈希，Windows 客户端使用 DPAPI 保存密钥。
- 容器默认只读运行、移除全部 Linux capabilities，并启用 `no-new-privileges`。

## 从源码开发

服务端需要 Go 1.26。管理页面使用原生 HTML、CSS 和 JavaScript，并嵌入 Go 二进制，不需要单独的前端工具链。Windows 客户端使用 C#、.NET 10 和 WinForms。

### 服务端

```bash
mkdir -p data
export WOLLET_DB_PATH=./data/wollet.db
export WOLLET_ADMIN_PASSWORD=development-password

go run ./cmd/wollet serve
```

构建服务端、模拟客户端并检查就绪状态：

```bash
go build -o wollet ./cmd/wollet
go build -o wollet-sim ./cmd/wollet-sim
./wollet healthcheck --url http://127.0.0.1:8080/readyz
```

### 模拟客户端

模拟客户端可以在没有 Windows 电脑时测试绑定、在线状态和关机指令。先在管理页面生成 Token：

```bash
./wollet-sim bind \
  --server http://127.0.0.1:8080 \
  --token M7K4P-2N8QX-R6T9C-V3W5D \
  --name 工作站 \
  --mac A4:83:E7:19:2C:5A \
  --config ./data/workstation-sim.json

./wollet-sim run --config ./data/workstation-sim.json
```

模拟客户端收到关机指令后默认退出，以离线状态模拟关机，不会关闭当前操作系统。

### Windows 客户端

```bash
dotnet restore clients/windows/Wollet.Windows.slnx
dotnet build clients/windows/Wollet.Client/Wollet.Client.csproj --no-restore
dotnet test --project clients/windows/Wollet.Client.Core.Tests/Wollet.Client.Core.Tests.csproj --no-restore
```

发布包含 .NET Runtime 的单文件客户端：

```bash
dotnet publish clients/windows/Wollet.Client/Wollet.Client.csproj \
  -p:PublishProfile=win-x64
```

体积较小的 framework-dependent 版本使用 `win-x64-framework-dependent` Publish Profile。

### 测试

```bash
go test ./...
go test -race ./...
go vet ./...
dotnet test --project clients/windows/Wollet.Client.Core.Tests/Wollet.Client.Core.Tests.csproj
```

## 项目结构

| 路径 | 内容 |
| --- | --- |
| `cmd/wollet` | 服务端入口与健康检查命令 |
| `cmd/wollet-sim` | 跨平台模拟客户端 |
| `internal/server` | HTTP、SSE、WebSocket 与设备操作 |
| `internal/store` | SQLite 存储与迁移 |
| `internal/webui` | 嵌入式管理页面 |
| `clients/windows` | Windows 安装器、后台服务与客户端测试 |
| `docs/protocol.md` | REST、SSE 和 WebSocket 协议说明 |

推送 `v*` Git 标签会触发 Release workflow，发布多架构 GHCR 镜像、两个 Windows x64 客户端和 GitHub Release。

## 通信协议

REST API、SSE 事件、客户端认证及 WebSocket 消息格式见 [docs/protocol.md](docs/protocol.md)。

后续开发需求与待讨论事项见 [开发规划](docs/development-plan.md)。
