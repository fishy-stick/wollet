# Wollet

[![Release](https://github.com/fishy-stick/wollet/actions/workflows/release.yml/badge.svg)](https://github.com/fishy-stick/wollet/actions/workflows/release.yml)
[![Latest release](https://img.shields.io/github/v/release/fishy-stick/wollet?display_name=tag)](https://github.com/fishy-stick/wollet/releases/latest)
[![Container](https://img.shields.io/badge/container-ghcr.io%2Ffishy--stick%2Fwollet-2496ED?logo=docker&logoColor=white)](https://github.com/fishy-stick/wollet/pkgs/container/wollet)

Wollet 是一套面向家庭和小型局域网的 Windows 远程开关机工具。你可以在浏览器中查看电脑状态、通过 Wake-on-LAN 唤醒电脑，或者让在线电脑安全关机。

Linux 服务端提供管理页面、设备状态和 Wake-on-LAN；Windows 客户端安装为后台服务，与服务端保持连接并接收关机指令。设备通过一次性 Token 完成绑定，各自持有独立凭据。

## 功能

- 在一个网页中管理多台 Windows 电脑
- 使用 Wake-on-LAN 唤醒离线设备
- 关机前显示 10 秒倒计时，可取消或立即关机
- 实时显示在线、离线、开机中和关机中状态
- 使用五分钟有效的一次性 Token 添加设备
- 可选的管理员登录；未设置密码时也可以在隔离网络中使用
- SQLite 持久化，无需额外数据库
- 提供 `linux/amd64`、`linux/arm64` 容器镜像和 Windows x64 单文件客户端

## 安全说明

Wollet 为可信局域网和 VPN 环境设计，服务端直接提供 HTTP 和 WebSocket，不包含 TLS 终止。不要将服务端端口直接暴露到公网；跨网络访问时，请使用可信 VPN 或在前方配置 HTTPS 反向代理。

`WOLLET_ADMIN_PASSWORD` 留空时，管理页面不要求登录，服务端会在启动日志中给出警告。除隔离的测试网络外，建议设置一个至少 12 个字符的密码。

## 快速开始

### 部署服务端

服务端需要运行在 64 位 Linux 上，并使用 host network 发送 Wake-on-LAN 广播包。准备好 Docker 和 Compose 插件后：

```bash
git clone https://github.com/fishy-stick/wollet.git
cd wollet
cp .env.example .env
mkdir -p data

# 编辑 .env，至少设置 WOLLET_ADMIN_PASSWORD
docker compose pull
docker compose up -d
```

打开 `http://<Linux 服务端 IP>:8080/` 即可进入管理页面。查看运行日志：

```bash
docker compose logs -f wollet
```

Compose 默认使用 `ghcr.io/fishy-stick/wollet:latest`。长期运行时，建议在 `.env` 中将 `WOLLET_IMAGE` 固定到具体版本，例如：

```dotenv
WOLLET_IMAGE=ghcr.io/fishy-stick/wollet:v1.0.1
```

如果希望从当前源码构建镜像：

```bash
docker compose up -d --build
```

数据库保存在 `./data/wollet.db`。备份前请停止容器，或使用 SQLite 在线备份工具。

### 安装 Windows 客户端

从 [GitHub Releases](https://github.com/fishy-stick/wollet/releases/latest) 下载 Windows x64 客户端：

| 文件 | 适用场景 |
| --- | --- |
| `wollet-client-win-x64-runtime.exe` | 已包含 .NET Runtime，下载后可直接运行；不知道选哪个时选这个 |
| `wollet-client-win-x64-framework-dependent.exe` | 文件更小，需要电脑已安装 x64 .NET 10 Desktop Runtime |

安装步骤：

1. 在 Wollet 管理页面生成绑定 Token，并复制页面提供的服务器地址。
2. 以管理员身份运行客户端，填入服务器地址和 Token。
3. 点击“安装并绑定”。绑定完成后，客户端会安装为 Windows 后台服务并自动连接服务器。
4. 返回管理页面，设备显示在线后即可远程控制。

重新运行客户端可以查看服务状态、修复配置或卸载服务。客户端配置保存在 `%ProgramData%\Wollet`，设备凭据通过 DPAPI 保护。

## 配置

| 环境变量 | 默认值 | 说明 |
| --- | --- | --- |
| `WOLLET_IMAGE` | `ghcr.io/fishy-stick/wollet:latest` | Compose 使用的服务端镜像 |
| `WOLLET_ADMIN_USERNAME` | `admin` | 管理员用户名 |
| `WOLLET_ADMIN_PASSWORD` | 空 | 至少 12 个字符；留空时关闭登录认证 |
| `WOLLET_LISTEN_ADDR` | `0.0.0.0:8080` | HTTP 监听地址 |
| `WOLLET_DB_PATH` | `/data/wollet.db` | SQLite 数据库路径；Compose 中固定为此值 |
| `WOLLET_WOL_BROADCAST` | `255.255.255.255` | Wake-on-LAN IPv4 广播地址；多网卡环境可改为定向广播地址 |
| `WOLLET_WOL_PORT` | `9` | Wake-on-LAN UDP 端口 |
| `WOLLET_LOG_LEVEL` | `info` | `debug`、`info`、`warn` 或 `error` |
| `WOLLET_UID` | `1000` | Compose 容器用户 UID，应与 `./data` 所有者一致 |
| `WOLLET_GID` | `1000` | Compose 容器用户 GID，应与 `./data` 所有者一致 |

修改配置后，可用 `docker compose config` 检查最终值，再运行 `docker compose up -d` 应用变更。

## 从源码开发

服务端使用 Go 1.26，管理页面是随二进制嵌入的原生 HTML、CSS 和 JavaScript；Windows 客户端使用 C#、.NET 10 和 WinForms。

### 服务端

```bash
mkdir -p data
export WOLLET_DB_PATH=./data/wollet.db
export WOLLET_ADMIN_PASSWORD=development-password

go run ./cmd/wollet serve
```

构建服务端和模拟客户端，并检查本地服务是否就绪：

```bash
go build -o wollet ./cmd/wollet
go build -o wollet-sim ./cmd/wollet-sim
./wollet healthcheck --url http://127.0.0.1:8080/readyz
```

管理页面源码位于 `internal/webui`，由 Go 在构建时直接嵌入，不需要单独安装前端工具链。

### Windows 客户端

Windows 客户端源码位于 `clients/windows`：

- `Wollet.Client.Core`：HTTP、WebSocket、心跳、重连和网卡选择
- `Wollet.Client`：WinForms 安装界面、Windows Service、凭据存储和系统关机
- `Wollet.Client.Core.Tests`：客户端核心逻辑测试

在仓库根目录使用 .NET 10 SDK 构建和测试：

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

发布体积较小的 framework-dependent 客户端：

```bash
dotnet publish clients/windows/Wollet.Client/Wollet.Client.csproj \
  -p:PublishProfile=win-x64-framework-dependent
```

两个版本分别输出到 `clients/windows/Wollet.Client/bin/Release/net10.0-windows/win-x64/publish/` 和它的 `framework-dependent/` 子目录。

### 模拟客户端

模拟客户端可以在不使用 Windows 电脑的情况下测试设备绑定、在线状态和关机指令。先从管理页面生成 Token：

```bash
./wollet-sim bind \
  --server http://127.0.0.1:8080 \
  --token M7K4P-2N8QX-R6T9C-V3W5D \
  --name 工作站 \
  --mac A4:83:E7:19:2C:5A \
  --config ./data/workstation-sim.json

./wollet-sim run --config ./data/workstation-sim.json
```

模拟客户端收到关机指令后会退出，用离线状态模拟关机，不会关闭当前操作系统。

### 测试

```bash
go test ./...
go test -race ./...
go vet ./...
dotnet test --project clients/windows/Wollet.Client.Core.Tests/Wollet.Client.Core.Tests.csproj
```

## 发布

推送符合 `v*` 格式的 Git 标签会触发 [Release workflow](https://github.com/fishy-stick/wollet/actions/workflows/release.yml)：

```bash
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions 会发布：

- `linux/amd64` 和 `linux/arm64` 的 GHCR 镜像
- 包含 .NET Runtime 的 Windows x64 客户端
- framework-dependent Windows x64 客户端
- 带自动生成变更记录的 GitHub Release

通信协议和接口说明见 [docs/protocol.md](docs/protocol.md)。
