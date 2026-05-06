# serialPortMonitor

串口监视助手 PoC，用于通过 CommMonitor/ComDrv11x 内核驱动捕获串口通信数据，并提供控制台与 WPF 两种使用方式。

## 项目结构

- `CommMonitorPoc`：控制台版核心 PoC，负责加载配置、发现串口、启动或安装驱动、下发 IOCTL、轮询驱动数据、解码抓包内容，并将原始包、载荷和元数据保存到本地。
- `CommMonitorPoc.Wpf`：WPF 图形界面，引用 `CommMonitorPoc` 的监视核心，提供端口勾选、开始/停止抓包、实时数据表、选中报文 HEX/ASCII 查看等功能。

## CommMonitorPoc

`CommMonitorPoc` 是当前仓库的核心实现，目标框架为 `net9.0`，启用了 unsafe 代码以调用 Windows 原生 API。

主要能力：

- 从 `monitor-profile.json` 加载监视参数，包括驱动设备路径、IOCTL 编号、缓冲区大小、轮询间隔、串口列表和解码参数。
- 通过注册表 `HARDWARE\DEVICEMAP\SERIALCOMM` 发现本机串口。
- 通过 `DriverBootstrapper` 检查 `\\.\ComDrv11x` 是否可用；不可用时可自动定位 `nt5`、`nt6`、`nt10` 目录下的驱动文件，创建或更新内核驱动服务并启动。
- 使用 `CreateFile` 与 `DeviceIoControl` 调用驱动接口，依次执行启动、过滤器配置、打开端口过滤和读取抓包数据。
- 解码驱动返回的包头和载荷，识别端口名、方向、类型、声明长度、XOR key，并输出 HEX/ASCII 预览。
- 抓包时默认写入 `captures` 目录，包含 `.bin` 原始包、`.payload.bin` 载荷和 `.meta.json` 元数据。
- 写入 `capture-diagnostic.log`，记录初始化、过滤、零包读取和抓包进度等诊断信息。

常用命令：

```powershell
dotnet run --project .\CommMonitorPoc -- --ports
dotnet run --project .\CommMonitorPoc -- --init
dotnet run --project .\CommMonitorPoc -- --init-only
dotnet run --project .\CommMonitorPoc -- --read-once
dotnet run --project .\CommMonitorPoc -- --capture-ms 10000
dotnet run --project .\CommMonitorPoc -- --replay .\CommMonitorPoc\bin\Debug\net9.0\captures
```

参数说明：

- `--ports`：列出 Windows 当前识别到的串口。
- `--init`：写入默认 `monitor-profile.json` 后退出。
- `--init-only`：初始化驱动和端口过滤后退出，不进入轮询抓包。
- `--read-once`：等待一个非零数据包，超时由 `ReadOnceTimeoutMs` 控制。
- `--capture-ms <毫秒>`：按指定时长抓包。
- `--replay [目录]`：离线读取已有 `.bin` 抓包文件并按当前配置解码展示。

## CommMonitorPoc.Wpf

`CommMonitorPoc.Wpf` 是图形界面项目，目标框架为 `net9.0-windows`，使用 WPF。项目通过 `ProjectReference` 直接复用 `CommMonitorPoc` 中的 `MonitorProfile`、`PortDiscovery`、`DriverBootstrapper`、`DriverMonitor`、`CaptureRecord` 和解码/格式化逻辑。

主要能力：

- 启动时加载输出目录中的 `monitor-profile.json`。
- 刷新并显示本机串口列表，用户可勾选需要监视的端口。
- 开始抓包时保存端口选择到配置文件，确保驱动可用，初始化监视器并在后台任务中持续轮询。
- 实时显示抓到的数据包，包括时间、端口、方向、类型和 HEX 报文。
- 支持保留最近 2500 条记录，选择数据包后展示摘要、HEX 和 ASCII 内容。
- 可选择是否保存原始抓包文件，并支持跟随最新包自动选中。
- 异常会写入 `capture-error.log`，驱动和轮询诊断仍由核心逻辑写入 `capture-diagnostic.log`。

运行命令：

```powershell
dotnet run --project .\CommMonitorPoc.Wpf
```

## 运行要求

- Windows 系统。
- .NET 9 SDK 或匹配的运行时。
- 访问或安装内核驱动通常需要管理员权限。
- 驱动文件需要位于输出目录或其上级目录的 `nt5`、`nt6`、`nt10` 子目录中，或者在 `monitor-profile.json` 中显式配置 `DriverPath`。

## 构建与发布

```powershell
dotnet build .\CommMonitorPoc\CommMonitorPoc.csproj
dotnet build .\CommMonitorPoc.Wpf\CommMonitorPoc.Wpf.csproj
dotnet publish .\CommMonitorPoc.Wpf\CommMonitorPoc.Wpf.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## 注意事项

- 这是 PoC 项目，驱动协议、IOCTL 和报文结构依赖当前已恢复的 CommMonitor 驱动行为。
- `monitor-profile.json` 会复制到输出目录，运行时读取的是 `AppContext.BaseDirectory` 下的配置文件。
- 当前 WPF 源文件中的部分中文文案存在编码异常，但不影响 README 所描述的程序结构和核心流程。
