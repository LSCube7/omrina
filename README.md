# OMRINA

**Optical Mark Recognition Integration Agent** 是运行在本机的跨平台 Local Integration Agent。当前代码提供 Windows、macOS 和 Linux 桌面目标、模板生成、图像采集、本地识别与评分复核，以及通过配对授权访问这些能力的本机 HTTP / WebSocket 服务和无 UI 的 TypeScript SDK。

公开仓库：[LSCube7/omrina](https://github.com/LSCube7/omrina)

OMRINA 不是面向终端用户的完整答题应用。桌面 UI 主要用于配置、状态查看和需要用户确认的操作；模板、识别、评分与复核能力由本地引擎提供。

## 当前状态

- 桌面目标为 Windows、macOS 和 Linux；不考虑 Android / iOS。
- Windows 仍是主要开发平台和主要实机扫描平台。
- Uno 桌面迁移代码已落地：Windows target 使用 Windows App SDK / WinUI 3，macOS 与 Linux 使用 Uno Skia Desktop target；界面使用单窗口 NavigationView 与 TitleBar。
- Windows 使用 `WindowsAppSDKSelfContained=true` 随输出提供 Windows App SDK runtime payload；macOS/Linux 使用 Skia desktop entrypoint。
- Windows 构建与 CaptureStore 回归已在本机验证；macOS/Linux 编译、运行、设备发现和文件选择器实机验收暂缓，不能写成已验收。
- 三平台 CI 当前暂缓，不能把 macOS / Linux 编译、发布或实机验收写成已通过。
- 公网 HTTPS 来源的浏览器权限、完整桌面授权交互与实物设备验收仍需补充；代码级和浏览器验证分别记录在验证文档中。

## 架构

代码按职责拆分，平台相关 API 保持在明确的边界内：

- `Omrina.Core`：提供模板数据模型、A4 几何、SVG 输出、定位与填涂识别、答案键、评分、复核及 JSON/CSV 导出；只包含平台无关的 .NET 逻辑。
- `Omrina.Protocol`：定义健康检查、配对、授权、任务快照和状态事件的传输模型。
- `Omrina.Scanning`：统一扫描抽象与结果模型；具体驱动由扫描适配层处理。
- `Omrina.Server`：仅监听回环地址，处理精确来源配对、凭据验证、授权撤销、异步任务、幂等创建、取消及 WebSocket 事件，通过业务接口调用本地能力。
- `Omrina.Platform`：平台无关的输入图像文件契约、Skia PNG/JPEG 完整解码校验、灰度图解码与本地文件实现。
- `Omrina.Desktop`：Uno 桌面入口与平台适配边界；Windows、macOS/Linux 分别选择文件、扫描和打印实现。页面包含状态、模板、采集、识别/复核、设置和关于，业务页面在主窗口内缓存。

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

2026-09-18 已完成一张生成模板的打印、填涂和本地应用扫描。采集图为 2481 × 3506 像素，目视确认页面四角定位与方向标记完整，人工复核确认填涂内容可读。纸面实际尺寸尚未测量，因此不能把打印比例写成已验收。该样本随后用于 M2 本机识别检查；这不等于跨设备准确率验收。

迁移前 Windows 原型的模板、打印和采集记录仍保留在验证文档中；它们不替代本轮的代码级构建与 CaptureStore 回归。当前验证结果、平台未实测项和能力限制见 [M0 / M1 验证记录](docs/development/validation.md) 和 [M1 计划](docs/architecture/m1.md)。文档不包含本机用户路径，也不上传真实扫描样本。

## M2：本地识别、评分与复核

采集记录中的模板参数、schema 和完整 `TemplateId` 经校验后，已有的 SkiaSharp 图像层把原图交给 Core。Core 检测四角定位块和方向标记，建立纸面到像素的变换，返回各题填涂状态、质量指标和诊断。评分必须由用户提供完整的标准答案；拒绝的识别结果不给分，尚未解决的待复核问题只能产生临时分数。人工修订及其原因可连同原识别结果导出为 JSON/CSV。桌面使用同一 NavigationView 中的“识别 / 复核”页，不通过健康检查接口开放设备或结果。

Core 与平台回归可用以下现有项目运行；详细流程和边界见 [M2 架构](docs/architecture/m2.md) 与 [验证记录](docs/development/validation.md)：

```powershell
dotnet run --project .\tests\core\Omrina.Core.Tests.csproj --no-restore
dotnet run --project .\tests\m2\Omrina.M2.Tests.csproj --no-restore
dotnet run --project .\tests\capture\Omrina.Capture.Tests.csproj --no-restore
```

## M3：本机服务与无 UI 的 SDK

网站调用 SDK 发起配对，在桌面“设置”页由用户查看完整来源并允许。用户将本地显示的一次性配对码交给 SDK，交换仅绑定该来源的凭据。业务调用同时验证来源和凭据；环境变量允许读取健康检查不代表获得业务授权。

SDK 可以生成模板、上传关联的 PNG/JPEG、选择设备扫描，并通过任务调用识别、评分、复核和 JSON/CSV 导出。模板结果包含 SVG；任务、采集与结果按授权隔离，不接收网页传入的本地文件路径。创建任务使用幂等键，复核带结果版本，WebSocket 提供状态通知；断线后查询任务恢复状态。SDK 不自动重试扫描等有副作用的操作。

当前授权与业务索引保存在内存，应用重启后需要重新配对；已保存的采集文件保留在本机。SDK 使用方式见 [SDK README](packages/sdk/README.md)，参数、限额与授权规则见 [M3 契约](docs/architecture/m3-contract.md) 和 [M3 架构](docs/architecture/m3.md)。该 npm 包尚未发布。

## M4：分发准备

已加入实际 npm tarball 的消费者检查，验证包入口的 JavaScript 导入和严格 TypeScript 声明消费。Windows x64 的优化 Release 开发验证包已通过离线恢复、发布、运行文件、隐私及哈希检查；安装器、签名和干净机器运行尚未验收。范围与验收矩阵见 [M4 计划](docs/architecture/m4.md)，实际通过项与剩余限制见 [验证记录](docs/development/validation.md)。

```powershell
node .\tests\sdk-package\consumer-smoke.mjs
```

该检查复用已安装的开发依赖，不联网安装，也不发布 npm 包。

## M5-A：主观题人工批阅基础

本机服务与无 UI SDK 已接入对采集原图人工指定题目区域、读取 PNG、保存草稿、确认评分与 JSON/CSV 导出。评分按预期版本整批提交，记录修改历史并原子保存到本机；未全部确认时最终主观题小计为空。新授权不自动获得旧记录。

区域暂用原图整数像素坐标，不改动现有选择题模板。混合题型模板、自动定位、正式批阅界面、综合成绩及真实纸张验收仍待后续实现；模板样式与生成方式等待用户补充。详细契约与限制见 [M5 架构](docs/architecture/m5.md)，实际检查见 [验证记录](docs/development/validation.md)。OCR/AI 尚未接入。

## 后续里程碑

完整目标与新增主观题规划见 [开发路线图](docs/architecture/roadmap.md)。

1. M0 / M3：补充公网 HTTPS 浏览器访问与本地授权界面交互验收。
2. M1：补充纸面尺寸测量，并固化打印、填涂、扫描的可重复验收记录。
3. M2：补充更多实际纸张、扫描方向、分辨率和设备的识别验证。
4. M3：补充真实网站与设备端到端联调。
5. M4：确定安装分发、兼容性和使用文档；恢复三平台 CI 后再安排 macOS / Linux 实机验收。
6. 模板设计调整：等待用户补充答题纸样式和生成方式，统一设计选择题与主观题区域。
7. M5：已有人工指定区域与 SDK 批阅基础，继续混合模板、自动定位、批阅界面、综合成绩及实机验收。
8. M6（新增计划）：可选 OCR（GLM-OCR 等候选）与 AI 辅助批阅，先提供待人工确认的评分建议；模型与运行方式尚未选定。
