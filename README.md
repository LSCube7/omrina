# OMRINA

**Optical Mark Recognition Integration Agent** 是计划运行在本机的跨平台 Local Integration Agent。当前代码提供 Windows WinUI 3 原型、平台无关的模板与扫描契约，以及只读的本地健康检查；设备发现和单页采集目前只在 Windows 桌面原型中实现。完整的浏览器连接、任务、本地授权、状态事件和 WebSocket 能力仍按后续里程碑推进。

公开仓库：[LSCube7/omrina](https://github.com/LSCube7/omrina)

OMRINA 不是面向终端用户的完整答题应用。桌面 UI 主要用于配置、状态查看和需要用户确认的操作；模板、识别、评分与复核能力由本地引擎逐步提供。

## 当前状态

- 桌面目标为 Windows、macOS 和 Linux；不考虑 Android / iOS。
- Windows 仍是主要开发平台和主要实机扫描平台。
- 跨平台 UI 最终选择 Uno Platform：Windows target 使用 Windows App SDK / WinUI 3，macOS 与 Linux 采用 Uno Skia Desktop target。Uno 迁移尚未完成。
- 当前桌面实现仍是 Windows WinUI 3；`Omrina.Platform` 适配层和 Uno UI 尚未落地。
- 三平台 CI 当前暂缓，不能把 macOS / Linux 编译、发布或实机验收写成已通过。
- M0 的 HTTPS、WebSocket、配对授权和授权撤销仍待验证；当前健康检查只证明本地诊断路径。

## 架构

代码按职责拆分，平台相关 API 保持在明确的边界内：

- `Omrina.Core`：当前提供模板数据模型、A4 几何和 SVG 输出；扫描任务、状态和配置仍属规划范围；只包含平台无关的 .NET 逻辑。
- `Omrina.Protocol`：当前只定义 M0 健康检查的协议常量和响应模型；完整的 HTTP、WebSocket、授权、设备和任务协议仍属规划范围。
- `Omrina.Scanning`：统一扫描抽象与结果模型；具体驱动由扫描适配层处理。
- `Omrina.Server`：当前提供仅监听回环地址的健康检查 HTTP 服务和来源校验；事件与任务调度仍属规划范围。
- `Omrina.Platform`：规划中的文件选择、启动、托盘和系统权限等平台适配层，当前尚未完成。
- `Omrina.Desktop`：当前 Windows WinUI 3 窗口和桌面入口；Uno UI 是后续目标。

`Microsoft.UI.*`、`Windows.*` 和 Win32 类型不得进入 Core、Protocol、Scanning 抽象或 Server。Windows 的原生能力通过 Platform 层或明确的 Windows 实现提供。详细的品牌、资源和平台边界见 [OMRINA 品牌与资源约定](docs/brand.md)。

Windows 桌面扫描路径通过 `Naps2ScannerService` 实现 `IScannerService`，由 NAPS2 封装 WIA/TWAIN；跨平台扫描适配器尚未实现。`Omrina.Core`、`Omrina.Protocol`、`Omrina.Scanning` 和 `Omrina.Server` 不直接依赖 WIA、TWAIN、SANE 或 ImageCaptureCore。当前主要设备目标包括 EPSON L4260。

## 开发与本地诊断

SDK、依赖与构建输出不提交。开发工具下载到 `.tools`；首次构建需要已授权的 NuGet 网络访问。SDK 位于 `packages/sdk`，桌面构建和运行说明见 [桌面 README](apps/desktop/README.md)。

桌面应用启动并监听回环地址后，可以运行：

```powershell
node .\tests\integration\health.mjs
```

该脚本只检查固定的 `/health` 响应、外部 `Origin` 拒绝、未知路径 404 和 `POST /health` 405。它不会启动应用、扫描端口或访问扫描设备，也不验证真实浏览器的 CORS / Local Network Access 行为。健康检查不提供设备、扫描、文件或 WebSocket 访问权限。

## M1：模板、打印与采集

`Omrina.Core` 负责单页 A4 的毫米几何和 SVG 输出。模板 schema 为 1；标题先规范化为 NFC，再由规范化标题、题数和选项数生成完整小写 SHA-256 `TemplateId`。页面同时打印短编号，并在顶部放置独立的方向标记，便于后续模板匹配和方向判断。Core 回归程序覆盖容量、边界、确定性、SVG 安全和模板身份。

桌面流程已接入模板预览、系统打印和图像采集。打印流程按 A4 纵向实际尺寸输出并检查可打印区域，不自动把模板缩放到其他纸张或被打印机边距裁切。PNG/JPEG 导入和 WIA/TWAIN 平板首张扫描都通过同一保存流程校验图像，并把原图与模板 manifest 一起保存。

2026-09-18 已完成一张生成模板的打印、填涂和本地应用扫描。采集图为 2481 × 3506 像素，目视确认页面四角定位与方向标记完整，人工复核确认填涂内容可读。纸面实际尺寸尚未测量，因此不能把打印比例写成已验收；M2 的自动识别、评分和人工复核功能尚未实现。

软件级验证已覆盖模板预览、SVG 保存与取消、PNG 导入关联、Print to PDF 输出，以及 CaptureStore 的回归检查。验证记录与未完成项见 [M0 / M1 验证记录](docs/development/validation.md) 和 [M1 计划](docs/architecture/m1.md)。文档不包含本机用户路径，也不上传真实扫描样本。

## 后续里程碑

1. M0：完成 HTTPS 浏览器访问、WebSocket、配对授权和授权撤销验证。
2. M1：补充纸面尺寸测量，并固化打印、填涂、扫描的可重复验收记录。
3. M2：实现模板定位、填涂识别、评分、人工复核和导出。
4. M3：完成跨平台 SDK 业务接入。
5. M4：确定安装分发、兼容性和使用文档；恢复三平台 CI 后再安排 macOS / Linux 实机验收。
