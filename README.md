# OMRINA

**Optical Mark Recognition Integration Agent** 是运行在本机的跨平台 Local Integration Agent。当前代码提供 Windows、macOS 和 Linux 桌面目标、平台无关的模板与扫描契约、图像导入保存和只读的本地健康检查；完整的浏览器连接、任务、本地授权、状态事件和 WebSocket 能力仍按后续里程碑推进。

公开仓库：[LSCube7/omrina](https://github.com/LSCube7/omrina)

OMRINA 不是面向终端用户的完整答题应用。桌面 UI 主要用于配置、状态查看和需要用户确认的操作；模板、识别、评分与复核能力由本地引擎逐步提供。

## 当前状态

- 桌面目标为 Windows、macOS 和 Linux；不考虑 Android / iOS。
- Windows 仍是主要开发平台和主要实机扫描平台。
- Uno 桌面迁移代码已落地：Windows target 使用 Windows App SDK / WinUI 3，macOS 与 Linux 使用 Uno Skia Desktop target；单窗口导航壳由另一代理负责维护。
- Windows 使用 `WindowsAppSDKSelfContained=true` 随输出提供 Windows App SDK runtime payload；macOS/Linux 使用 Skia desktop entrypoint。
- Windows 构建与 CaptureStore 回归已在本机验证；macOS/Linux 编译、运行、设备发现和文件选择器实机验收暂缓，不能写成已验收。
- 三平台 CI 当前暂缓，不能把 macOS / Linux 编译、发布或实机验收写成已通过。
- M0 的 HTTPS、WebSocket、配对授权和授权撤销仍待验证；当前健康检查只证明本地诊断路径。

## 架构

代码按职责拆分，平台相关 API 保持在明确的边界内：

- `Omrina.Core`：当前提供模板数据模型、A4 几何和 SVG 输出；扫描任务、状态和配置仍属规划范围；只包含平台无关的 .NET 逻辑。
- `Omrina.Protocol`：当前只定义 M0 健康检查的协议常量和响应模型；完整的 HTTP、WebSocket、授权、设备和任务协议仍属规划范围。
- `Omrina.Scanning`：统一扫描抽象与结果模型；具体驱动由扫描适配层处理。
- `Omrina.Server`：当前提供仅监听回环地址的健康检查 HTTP 服务和来源校验；事件与任务调度仍属规划范围。
- `Omrina.Platform`：平台无关的输入图像文件契约、Skia PNG/JPEG 完整解码校验和本地文件实现。
- `Omrina.Desktop`：Uno 桌面入口与平台适配边界；Windows、macOS/Linux 分别选择文件、扫描和打印实现。页面包含状态、模板、采集、设置和关于，模板页与采集页在主窗口内缓存。

`Microsoft.UI.*`、`Windows.*` 和 Win32 类型不得进入 Core、Protocol、Scanning 抽象或 Server。Windows 的原生能力通过 Platform 层或明确的 Windows 实现提供。详细的品牌、资源和平台边界见 [OMRINA 品牌与资源约定](docs/brand.md)。

Windows 桌面扫描路径通过 `Naps2ScannerService` 实现 `IScannerService`，由 NAPS2 1.3.0 使用 WIA/TWAIN；macOS 与 Linux 使用 NAPS2 的默认驱动，分别映射 Apple/ImageCaptureCore（ICA）与 SANE。扫描契约固定 A4 平板首张和 150/300/600 DPI；没有设备时不会创建虚假设备。`Omrina.Core`、`Omrina.Protocol`、`Omrina.Scanning` 和 `Omrina.Server` 不直接依赖这些原生驱动。三平台设备运行验收暂缓。

## 开发与本地诊断

SDK、依赖与构建输出不提交。开发工具下载到 `.tools`；首次构建需要已授权的 NuGet 网络访问。根目录 `global.json` 固定 .NET SDK 10.0.401 与 Uno.Sdk 6.7.30，桌面构建和运行说明见 [桌面 README](apps/desktop/README.md)。

桌面项目有两个目标：Windows WinAppSDK 和 `net10.0-desktop` Skia。使用工作区 NuGet 配置恢复并分别编译：

```powershell
dotnet restore .\apps\desktop\Omrina.Desktop.csproj --configfile .tools\NuGet.Config --packages .tools\nuget-packages
dotnet build .\apps\desktop\Omrina.Desktop.csproj -f net10.0-windows10.0.26100.0 --no-restore
dotnet build .\apps\desktop\Omrina.Desktop.csproj -f net10.0-desktop --no-restore
```

桌面应用启动并监听回环地址后，可以运行：

```powershell
node .\tests\integration\health.mjs
```

该脚本只检查固定的 `/health` 响应、外部 `Origin` 拒绝、未知路径 404 和 `POST /health` 405。它不会启动应用、扫描端口或访问扫描设备，也不验证真实浏览器的 CORS / Local Network Access 行为。健康检查不提供设备、扫描、文件或 WebSocket 访问权限。

## M1：模板、打印与采集

`Omrina.Core` 负责单页 A4 的毫米几何和 SVG 输出。模板 schema 为 1；标题先规范化为 NFC，再由规范化标题、题数和选项数生成完整小写 SHA-256 `TemplateId`。页面同时打印短编号，并在顶部放置独立的方向标记，便于后续模板匹配和方向判断。Core 回归程序覆盖容量、边界、确定性、SVG 安全和模板身份。

桌面流程已接入模板预览、SVG 保存与取消、PNG/JPEG 导入关联、Windows 系统打印和 CaptureStore 回归检查。当前单窗口导航由状态、模板、采集、设置和关于页面组成，模板页与采集页保持缓存；旧版 MainWindow“扫描测试纸”入口已退役，PNG/JPEG 导入和三平台扫描适配统一由采集页交给 `CaptureStore` 保存。打印流程按 A4 纵向实际尺寸输出并检查可打印区域，不自动把模板缩放到其他纸张或被打印机边距裁切；macOS/Linux 当前明确报告系统打印适配器未提供，并保留 SVG 保存。

`CaptureStore` 保留 100 MB 文件、16000×16000 尺寸和 100Mpx 总像素限制，先检查 codec 头部，再完整遍历 JPEG scanline 或在 PNG scanline 不可用时执行受上述限制约束的 exact-size 解码；复制后再次校验并使用 atomic staging，取消和失败会清理暂存目录。默认数据目录仍是 `%LocalAppData%\AnswerSheet`（其他系统为对应的 LocalApplicationData 下 `AnswerSheet`）。

2026-09-18 已完成一张生成模板的打印、填涂和本地应用扫描。采集图为 2481 × 3506 像素，目视确认页面四角定位与方向标记完整，人工复核确认填涂内容可读。纸面实际尺寸尚未测量，因此不能把打印比例写成已验收；M2 的自动识别、评分和人工复核功能尚未实现。

迁移前 Windows 原型的模板、打印和采集记录仍保留在验证文档中；它们不替代本轮的代码级构建与 CaptureStore 回归。当前验证结果、平台未实测项和能力限制见 [M0 / M1 验证记录](docs/development/validation.md) 和 [M1 计划](docs/architecture/m1.md)。文档不包含本机用户路径，也不上传真实扫描样本。

## 后续里程碑

1. M0：完成 HTTPS 浏览器访问、WebSocket、配对授权和授权撤销验证。
2. M1：补充纸面尺寸测量，并固化打印、填涂、扫描的可重复验收记录。
3. M2：实现模板定位、填涂识别、评分、人工复核和导出。
4. M3：完成跨平台 SDK 业务接入。
5. M4：确定安装分发、兼容性和使用文档；恢复三平台 CI 后再安排 macOS / Linux 实机验收。
