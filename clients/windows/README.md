# Wollet Windows 客户端开发

Windows 客户端使用 C#、.NET 10 和 WinForms。源码拆成可跨平台测试的核心项目与 Windows 宿主项目，发布结果仍是一个 `wollet-client.exe`。

## 项目

- `Wollet.Client.Core`：服务端地址、REST API、WebSocket、心跳、重连和网卡选择。
- `Wollet.Client`：WinForms、Windows Service、安装、DPAPI、ACL 和系统关机。
- `Wollet.Client.Core.Tests`：不执行 Windows 专属操作的核心测试。

## 构建与测试

需要 .NET 10 SDK。Linux 构建会自动使用 Windows targeting pack，但不能运行 WinForms 或 Windows Service。

```bash
dotnet restore Wollet.Windows.slnx
dotnet build Wollet.Client/Wollet.Client.csproj --no-restore
dotnet test --project Wollet.Client.Core.Tests/Wollet.Client.Core.Tests.csproj --no-restore
```

从仓库根目录执行时，在路径前加 `clients/windows/`。

## 发布

自包含单文件（无需预装 .NET，文件较大）：

```bash
dotnet publish Wollet.Client/Wollet.Client.csproj \
  -p:PublishProfile=win-x64
```

框架依赖单文件（文件较小，目标电脑需预装 x64 .NET 10 Desktop Runtime）：

```bash
dotnet publish Wollet.Client/Wollet.Client.csproj \
  -p:PublishProfile=win-x64-framework-dependent
```

自包含版本发布到 `Wollet.Client/bin/Release/net10.0-windows/win-x64/publish/`，框架依赖版本发布到其 `framework-dependent/` 子目录。安装逻辑接受以上两种单文件发布版本，但不会接受带 `.deps.json` 等旁路依赖的开发期 apphost。

## Windows 验证

首次在 Windows 上运行时，重点检查：

- UAC、不同缩放比例下的安装界面和错误提示。
- 首次安装、重复修复、无效凭据重新绑定。
- `%ProgramData%\Wollet\client.json` 的 DPAPI 和 ACL。
- Service 使用 `LocalService` 自动启动；安装器仅额外授予 `SeShutdownPrivilege`，并配置 5/15/30 秒失败重启策略。
- 多网卡、VPN 和无线网络下的路由网卡选择。
- Event Log、断网重连、凭据撤销和真实关机。
