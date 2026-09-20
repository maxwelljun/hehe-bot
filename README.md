# ScreenWatch · Windows 通用画面监控

选择屏幕上的固定区域，保存参考画面，匹配后发出本地提醒。支持中文界面、系统托盘运行、截图留档和配置持久化。

## 快速使用

1. 将 `ScreenWatch-1.0.0-win-x64.zip` 复制到 Windows 电脑并解压，双击 `ScreenWatch.exe`。适用于受 .NET 10 支持的 Windows 10/11 x64；无需安装 .NET、Python、Java 或浏览器驱动，无需管理员权限。
2. 在 Chrome 或其他应用中打开目标画面，让它保持可见。首次试用可用 Chrome 打开压缩包内的 `demo/index.html`，无需联网。
3. 点击“框选区域”，拖动选择要监控的矩形；按 `Esc` 或右键取消。多显示器可选任意一块，但单个区域不能横跨显示器。
4. 当希望识别的画面出现时，点击“保存当前画面为参考”。程序会暂时隐藏主窗口，再取样。也可导入与区域像素尺寸完全相同的 PNG、JPEG 或 BMP。
5. 点击“测试一次匹配”，查看相似度与最近取样。测试不会触发提醒。建议分别测试目标画面和非目标画面，再确定阈值。
6. 点击“开始监控”。主窗口自动隐藏到系统托盘；匹配连续达到阈值后，发出托盘气泡、提示音，并按设置保存截图。
7. 双击托盘图标打开主窗口。为避免主窗口遮挡目标，打开主窗口会停止监控；点击“开始监控”重新开始。右键托盘也可停止或退出。窗口右上角关闭按钮只隐藏窗口。

此工具用于通用画面变化提醒，不包含网站账号、登录逻辑或业务操作。

针对 `www.yaxin868.com` 的接口分析、后台监控架构、六连判定和分阶段下单方案见 [`docs/yaxin-integration-plan.md`](docs/yaxin-integration-plan.md)。该方案默认采用只读监控和模拟下单，真实下单需在完成数据对照与幂等验证后单独启用。

仓库同时包含独立的 Windows 程序 `YaxinMonitor`：通过 Chrome CDP 读取全部百家乐桌台，支持配置触发走势、连续次数、顺/反方向和 `1–10` 档金额序列；默认规则为“最新六连反向、10 → 20 → 40”。操作步骤见 [`docs/yaxin-user-guide.md`](docs/yaxin-user-guide.md)。

## 提醒规则

| 设置 | 默认值 | 作用 |
| --- | --- | --- |
| 截图间隔 | 1000 毫秒 | 250–60000 毫秒；上一轮未结束时跳过，不堆积任务 |
| 相似度阈值 | 95% | 50–100%；数值越高越严格，不是识别准确率或概率 |
| 连续命中次数 | 2 | 过滤单帧闪烁；默认通常在目标稳定出现后约 1–2 秒确认 |
| 消失确认次数 | 2 | 连续不匹配达到此次数后，才允许识别下一次出现 |
| 提醒冷却 | 30 秒 | 两次提醒的最短间隔；持续显示同一画面只提醒一次 |
| 提示音 / 保存截图 | 开启 | 均可关闭 |

如果新画面在冷却期间出现，会等到冷却结束且画面仍然匹配时再提醒；冷却结束前已消失的画面不会补发提醒。每次手动开始视为新监控会话，命中状态和冷却重新计时。

## 后台运行的边界

- 程序可隐藏到托盘。被监控的 Chrome/其他应用需要保持在当前可见桌面，目标区域不能被遮挡；目标窗口最小化、切到别的标签、页面滚动后，截到的是该屏幕坐标当前显示的内容。
- 使用固定屏幕坐标，不跟随窗口移动，不搜索整屏内的目标，也不识别文字或理解图案语义。选择紧贴目标的小区域效果更可靠。
- 浏览器缩放、Windows 缩放、主题、布局或目标位置改变后，应停止监控、重新框选并保存参考画面。动态时间、动画、光标或大块空白都可能影响匹配。
- 锁屏、会话断开、休眠/恢复、显示器布局变化会停止监控，之后需要手动检查并开始。它是登录用户桌面程序，不是 Windows 后台服务。
- 截图失败会停止监控并提示原因；不会将截图失败当作一次匹配。
- Windows“请勿打扰”或系统通知设置可能隐藏气泡。命中仍可从程序日志与截图中确认；声音也受系统音量设置影响。
- 框选时建议避开屏幕右下角的系统通知区域；气泡等浮层遮挡目标也会被视为画面变化。
- 便携版未进行代码签名，因此 Windows 可能显示未知发布者提示。

## 数据位置与保留

所有运行数据位于 `%LOCALAPPDATA%\ScreenWatch\`，可点击“数据目录”打开。

```text
settings.json                 区域和提醒设置，原子替换写入
reference-<随机编号>.png       当前参考图片
captures/match-*.png          命中截图
logs/YYYY-MM-DD.log           运行日志
```

- 命中截图保留最近 7 天、最多 200 张且总量不超过 256 MiB；启动和保存命中截图时清理。日志保留 30 天，每天最多约 4 MiB，满后轮换。
- 图片和日志只保存在本机，程序没有网络通信、云上传或遥测功能。
- 配置损坏时明确报错并退出，不自动覆盖原配置。可备份后移走 `settings.json`，再重新设置。
- 卸载只需退出程序并删除程序文件；若不再需要历史记录，可同时删除上述数据目录。
- 初次运行自包含单文件时，.NET 会将内置原生组件解压到用户临时目录。这不需要安装运行时，也不需要管理员权限。

## 技术实现

- .NET 10 LTS + Windows Forms，使用框架内置 API；没有第三方 NuGet 包。
- `Graphics.CopyFromScreen` 获取可见桌面；每块显示器独立框选覆盖层，支持负坐标显示器和 PerMonitorV2 DPI 感知。
- 原始区域缩采样到 96 × 96 彩色像素。计算 RGB 均方根误差，以及最大通道差超过 35 的像素占比；取两者对应相似度的较小值。该算法比较固定区域的整体外观，不等同于 OpenCV 的滑窗模板搜索。
- 使用连续命中/消失状态机和 `Stopwatch` 单调时钟实现确认、重新触发和冷却。
- 图片采集及计算在线程池运行；UI 线程更新托盘、预览和日志。停止后丢弃仍在处理的旧帧。
- 同一 Windows 会话只允许启动一个实例。

开源方案比较见源码目录中的 `docs/open-source-research.md`；测试范围和 Windows 真机验收步骤见 `docs/verification.md`。

## 从源码构建

构建需要 .NET 10 SDK；最终用户不需要 SDK。首次构建需联网下载微软 Windows Desktop 定向包和发布运行时。

在 Windows PowerShell 中进入项目目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

输出 `artifacts/ScreenWatch-1.0.0-win-x64.zip` 及 SHA-256 文件。ARM64 设备可使用 `-Runtime win-arm64` 构建；本次交付包为 x64。

独立执行核心测试：

```text
dotnet run --project tests/ScreenWatch.Core.Tests -c Release
dotnet run --project tests/YaxinMonitor.Core.Tests -c Release
```

在 macOS/Linux 交叉编译 Windows 程序：

```text
dotnet build ScreenWatch.slnx -c Release
dotnet publish src/ScreenWatch.Windows/ScreenWatch.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish/win-x64
dotnet publish src/YaxinMonitor.Windows/YaxinMonitor.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/publish/yaxin-win-x64
```

交叉编译不能代替 Windows 桌面的运行验收。仓库包含 `.github/workflows/windows-build.yml`，推送到 GitHub 后可由 Windows runner 编译、运行核心测试并生成便携包；桌面交互仍需按清单实测。
