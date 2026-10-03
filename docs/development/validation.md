# OMRINA M0 / M1 验证记录

初始记录日期：2026-09-16；M1 软件与实物验证更新：2026-09-18；Windows 启动与健康复核更新：2026-09-19。

## 环境

- 初始工作区为空 Git 仓库，无已有提交或未提交代码。
- Node.js：v25.9.0。
- 初次检查只有 .NET 运行时；用户随后安装 SDK，现已检测到 .NET SDK 10.0.401。
- 检测到 Windows SDK 目录，其中包括 10.0.22621.0。
- 前序只读 WIA 检查可连接 EPSON L4260 并读取属性；没有执行扫描。

## 本次下载结果

用户明确授权从 Microsoft 下载工作区级 .NET SDK，以及从 NuGet 恢复 M0 必要依赖。

1. 沙箱内请求 `https://dot.net/v1/dotnet-install.ps1` 被套接字访问限制拒绝。
2. 提权后该地址长期无响应，主动中止。
3. 改用官方 `https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1`，设置 30 秒超时，仍超时。

上述 SDK 下载未完成，用户随后自行安装 SDK。没有关闭浏览器或系统安全限制。

## 安装 SDK 后继续验证

- 系统 `dotnet --list-sdks` 返回 10.0.401。
- 默认恢复因沙箱无权读取用户级 NuGet 配置失败；改用工作区 `.tools/NuGet.Config`（仅官方源），依赖保存到 `.tools/nuget-packages`。
- 提权后的 NuGet 恢复成功，用时约 1.18 分钟。
- 首次构建出现 14 条 CA2252：NAPS2 的程序集带有 `RequiresPreviewFeatures` 标记，需要明确选择使用预览 API；不是 C# 调用签名错误。
- 桌面项目显式设置 `EnablePreviewFeatures=true` 后，x64 Debug 构建成功，0 警告、0 错误，没有添加 `NoWarn`。
- 通过 computer-use 启动实际 WinUI 3 窗口并点击“刷新设备”，界面返回两个设备入口：WIA 与 TWAIN 的 EPSON L4260。截图与可访问性树均确认结果。
- 用户已明确准备测试纸并授权单页测试扫描。
- 用户没有 HTTPS 测试网站，公网 HTTPS 来源的浏览器本地网络权限仍待后续验证。
- 增加真实健康服务后，首次构建发现 `App` 使用基类 `Window` 调用具体窗口方法（CS1061），修正字段类型为 `MainWindow` 后构建再次成功，0 警告、0 错误。
- 启动真实进程并运行 `node tests/integration/health.mjs`：SDK 健康响应、外部 Origin 返回 403、未知路径返回 404、POST 返回 405，四项均通过。
- `Get-NetTCPConnection` 在沙箱中访问被拒绝，使用 `netstat -ano` 确认服务仅监听 `127.0.0.1:17843`；关闭应用后进程与监听端口均消失。
- 单页扫描代码加入后，x64 Debug 构建成功，0 警告、0 错误。在 WinUI 3 界面选择 EPSON L4260 TWAIN 入口并启动扫描，A4 / 300 DPI / 平板采集成功。
- 首次设备冒烟采集得到一张 2481 × 3506 像素的 PNG，已确认文字与页面可读；该普通测试纸样本不用于 OMR 准确率结论。
- 另一次 WIA 平板采集发现图像方向上下颠倒，已记录为后续采集方向处理问题；该普通测试纸样本同样不用于 OMR 准确率结论。
- 应用清单已设置 `PerMonitorV2`。随后执行 x64 Debug `--no-restore` 构建，0 警告、0 错误。
- 使用实际 WinUI 窗口截图与可访问性树验证布局：约 1268 × 739 的窗口中操作按钮横排；约 594 × 739 的窄窗口中按钮纵向全宽且状态文字换行；约 594 × 390 的矮窗口出现垂直滚动，滚动后底部 InfoBar 完整可见。
- 上述尺寸来自当前工具窗口，不代表不同系统缩放比例、不同显示器或跨显示器切换的实测；验证期间没有更改系统缩放设置。
- 只读检查已确认 runtimeconfig 要求 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 10.0.0，系统已安装相应的 10.0.12 运行时；应用宿主、主 DLL、Windows App Runtime Bootstrap 与 NAPS2 Worker 均存在于 x64 输出目录。此前沙箱内 GUI 进程启动失败不能作为运行时缺失的证据。
- 2026-09-16 再次启动桌面应用并运行健康检查集成测试，固定响应、外部 Origin 拒绝、`Origin: null` 返回 403、未知路径 404 与 `POST /health` 返回 405 均通过。

## 2026-09-17 M1 模板 UI 最新验证

- 桌面项目已引用无第三方包依赖的 Core 模板库。使用本地恢复结果执行 x64 Debug 构建，Core 与 Desktop 均构建成功，0 个警告、0 个错误。
- 主代理前一轮在实际 WinUI 窗口确认：默认 20 题、每题 4 个选项的模板预览可生成；标题改为“M1 验证答题纸”后保存按钮禁用；重新生成后标题更新且保存按钮恢复可用；`FileSavePicker` 能实际打开。
- 保存实现已改为 `FileIO.WriteTextAsync`，并通过上述桌面编译；本轮真实写入和取消保存尚未完成。启动最新可执行文件后，Computer Use 在点击“新建答题纸模板”时返回 `coordinate input geometry is unavailable`，随后重新激活又检测到用户输入，因而没有继续点击、没有强杀进程，也没有宣称生成 UI 保存文件。
- 当时 M0 的 HTTPS / WebSocket 浏览器连接和 M1 的实体流程仍未完成；后续实物验证见本文新增章节。代码实现与运行验收继续分开记录。

## 2026-09-18 M1 代码实现同步与待确认验证

以下内容依据当前代码记录，和运行验证分开：

- `Omrina.Core` 的模板 schema 为 1。标题先规范化为 NFC，再由规范化标题、题数和选项数生成 64 位小写 SHA-256 `TemplateId`；页面打印短编号，并加入独立的顶部方向标记。Core 回归程序当前为 9 项检查，先前运行结果为 9 项通过。
- `TemplatePrintController` 已接入 Windows 系统打印流程，按 A4 纵向实际尺寸生成打印页，并在分页阶段检查纸张尺寸和可打印区域；不自动缩放到其他纸张或超出打印机边距。
- `CaptureStore` 支持 PNG/JPEG 导入和扫描结果保存，保留原始图像并写入模板关联 manifest。原图和 manifest 先写入暂存目录，完成后移动到最终采集目录；取消、校验失败或写入失败会清理暂存记录。
- 采集页的扫描入口支持 WIA/TWAIN 平板、A4、150/300/600 DPI，并只接收首张图像，再交给 `CaptureStore` 保存。

CaptureStore 回归检查已确认如下：

- 最终测试项目只链接生产代码中的 `CaptureStore` 和 `Omrina.Core`，并使用已缓存的 `Microsoft.Windows.SDK.NET.Ref` FrameworkReference；不再引用整个桌面 UI 项目，避免测试进程触发 UI 初始化挂起。
- 在无新包下载的离线恢复后，构建结果为 0 个警告、0 个错误。可复现命令为：

  ```powershell
  dotnet restore <capture-test-project> --ignore-failed-sources
  dotnet build <capture-test-project> --no-restore -p:Platform=x64
  dotnet run --project <capture-test-project> --no-build --no-restore -p:Platform=x64
  ```

- 运行结果为 `PASS: 5 capture regression tests.`。5 项覆盖模板 ID/schema 关联、PNG 原始字节与 manifest、`scan` 来源类型复用同一存储 API、非法图像/扩展名校验，以及取消后的 staging 清理。
- 这组回归检查证明 CaptureStore 的本地保存和校验行为，不等同于 WIA/TWAIN 实际设备扫描、采集窗口 UI 或打印流程验收。

## 2026-09-18 OMRINA 命名迁移与构建验证

- Core、Protocol、Scanning、Server 和 Desktop 项目已迁移到 `Omrina.*` 项目命名；旧 `AnswerSheet` 目录和模板 canonical 名称按兼容性要求保留。
- `Naps2ScannerService` 已接入采集页的扫描路径，Windows 实机目标继续使用 NAPS2/WIA/TWAIN；这证明代码接线，不等同于本轮 UI 或跨平台验收。
- 迁移前记录中的 Core 回归测试 9 项、CaptureStore 回归测试 5 项、SDK 自动测试 7 项均通过，且当时各项目的 Windows x64 与 win-x64 构建为 0 个警告、0 个错误；这些结果不能替代当前迁移后的最终结果。
- 本轮 UI 工具无法找到所需的 kernel assets 路径，因此没有新增 UI 视觉或交互验收结论；此前已有的 Windows UI 记录继续按对应章节单独解释。
- Uno 迁移正在进行，`Omrina.Platform` 与单窗口导航壳仍待最终构建和测试确认；macOS / Linux 实机与三平台测试仍暂缓。

## 2026-09-18 M1 桌面、SVG、PNG 导入与 Print to PDF 软件验证

- 真实桌面窗口使用默认 20 题、每题 4 个选项的模板，预览显示编号 `AS1-20x4-B7A4AFC5` 和顶部方向标记。
- 取消保存时界面显示“已取消保存 SVG”，保存按钮恢复可用；XML 检查确认 A4 `210mm × 297mm`、80 个填涂圆圈和 1 个方向标记。
- PNG 导入界面显示 2481 × 3506，并成功关联模板身份。
- 打印流程实际选定 Microsoft Print to PDF，预览为 1 页；界面状态为“系统已接收打印任务。打印是否完成请以系统状态为准。”，打印完成后生成、保存、打印和导入按钮均恢复可用。
- PDF 元数据确认 1 页 A4、未加密；渲染页面已目视确认定位块、方向标记、编号、标题、列标和气泡清晰，无裁切、空白或重叠。

上述结果构成当前 M1 模板 UI、SVG 输出、PNG 导入关联和 Print to PDF 的软件级验收证据；不等同于纸面实际尺寸测量或 M2 识别结果。

## 2026-09-18 M1 实物打印、填涂与本地扫描验证

- 使用 OMRINA 生成的 20 题、每题 4 个选项模板完成打印、填涂，并通过本地应用完成扫描采集。
- 采集图为 2481 × 3506 像素；目视确认页面四角定位与方向标记完整，人工复核确认填涂内容可读。
- 该验证证明生成模板可以进入“打印 → 填涂 → 本地采集”的实物流程，但没有测量纸面实际尺寸，也没有执行自动模板识别、评分或人工复核工作流。
- 纸面尺寸测量、重复样本和不同设备兼容性仍待后续安排；M2 识别能力尚未实现。

以下结果仍待确认，暂不记为通过：

- 纸面实际尺寸与打印比例测量。
- 不同 WIA/TWAIN 设备和分辨率下的重复采集。
- 自动模板识别、评分和后续复核流程（M2 尚未实现）。

## 2026-09-19 Windows 标准入口启动与健康集成复核

- 使用正常标准入口启动 `Omrina.Desktop` 成功；窗口标题为 `OMRINA 本地服务 - M0`。
- 应用在 `127.0.0.1:17843` 提供健康检查，响应为 HTTP 200，服务名为 `omrina-local`，协议版本为 1，状态为 `ready`。
- 运行 `node tests/integration/health.mjs` 通过 SDK ready 响应、外部 Origin HTTP 403、未知路径 HTTP 404 和 `POST /health` HTTP 405 四项检查。
- 执行 `dotnet build apps/desktop/Omrina.Desktop.csproj -p:Platform=x64 --no-restore`，0 个警告、0 个错误；使用已有缓存，未下载新依赖。
- `WindowsAppSDKSelfContained=true` 使 Windows App SDK runtime payload 随部署输出提供，用于修复启动时的 COM `0x80040154`；该设置不是整个 .NET 应用的 self-contained 发布，目标环境仍需要兼容的 .NET runtime。
- 临时的 `Program` / `App` 日志钩子已移除，测试进程已停止。本轮没有执行实体扫描或打印。
- 本轮 UI 工具仍不可用，没有新增视觉或交互验收结论；窗口标题和 health 响应只证明标准入口启动及本地诊断路径，不等同于 UI 验收。

## 2026-09-19 单窗口导航与 Uno 迁移草稿（历史记录）

- 当前 Windows 导航壳使用单窗口 NavigationView、自定义 TitleBar，以及“状态”“模板”“采集”“设置”“关于”页面；模板页与采集页在主窗口内缓存。
- 旧版 MainWindow“扫描测试纸”入口已退役。图像导入和扫描统一由采集页调用 `CaptureStore`，不再维护独立的旧测试入口。
- 本节只记录当时的代码结构，不记录通过结论；迁移后的最终代码级构建和 CaptureStore 结果见下节。平台能力实际以最终代码为准。
- 本节不记录 UI 视觉/交互或设备运行验收结论。
- 三平台测试继续暂缓。本轮没有加载 `design.png`，也不以它作为 UI 验收证据。

## 2026-09-19 Uno 桌面迁移代码级验证

本节只记录本轮迁移后的代码级检查；此前 Windows 实机扫描、打印和 UI 记录仍按各自章节解释，不能当作本轮 macOS/Linux 验收。

- 根目录 `global.json` 固定 .NET SDK 10.0.401 与 Uno.Sdk 6.7.30。使用工作区 `.tools/NuGet.Config` 和 `.tools/nuget-packages` 从官方 NuGet 源恢复 Uno、NAPS2 1.3.0、Skia 和必要传递依赖成功。
- `dotnet build apps/desktop/Omrina.Desktop.csproj -f net10.0-desktop --no-restore` 通过，0 个错误；输出有 2 个 NU1900（NuGet 漏洞审计无法访问 nuget.org），不影响已缓存依赖解析。Windows `net10.0-windows10.0.26100.0` 代码与 XAML 使用隔离输出目录构建通过，0 个错误，同样只有 2 个 NU1900；普通输出目录当时被 UI 验收进程锁定，因此没有结束该进程。
- `tests/capture/Omrina.Capture.Tests.csproj` 构建通过，0 个错误（2 个 NU1900 仅为漏洞审计网络失败）；运行结果为 6 组通过。覆盖 Core 模板 ID/schema、3×2 PNG 的完整解码和原始字节复制、3×2 JPEG 完整解码、scan 来源、非法/不支持扩展、截断 PNG/JPEG、16001×1 超限返回 `IMAGE_DIMENSIONS_TOO_LARGE`、取消后的 staging 清理。
- `CaptureStore` 仍使用 `%LocalAppData%/AnswerSheet` 下的 `captures` 记录、atomic staging、复制后二次验证和取消清理；图像限制仍为 100 MB、宽高各 16000、总像素 100M。Skia JPEG 走原始尺寸 scanline；当前 PNG native codec 不支持 scanline 时走受这些上限约束的 exact-size decode。
- Windows 文件 picker 使用 HWND 初始化；`net10.0-desktop` 使用 Uno `FileOpenPicker` / `FileSavePicker`，不调用 Windows HWND。Windows 打印控制器保留系统打印流程；macOS/Linux 控制器明确返回“未提供系统打印适配器”，不会伪报成功，模板仍可保存 SVG。
- NAPS2 默认驱动在 Windows 为 WIA、macOS 为 Apple/ImageCaptureCore（ICA）、Linux 为 SANE；扫描入口固定 A4 平板首张和 150/300/600 DPI。没有虚构设备，也没有执行物理扫描或打印。

本轮未在 macOS/Linux 上运行应用、枚举设备、打开文件选择器或连接 ICA/SANE 后端；三平台测试和 CI 继续暂缓。`design.png` 未加载、未入程序、未提交。

SkiaSharp 依赖已统一为 3.119.2，与 Uno desktop 解析的 Windows/Linux/macOS native asset patch 一致；NAPS2 1.3.0 的 ImageSharp 适配使用独立的 ImageSharp 3.1.11 依赖，没有 SkiaSharp 版本约束冲突。

## 2026-09-19 Windows 单窗口导航 UI 验收

在已完成 Windows Desktop 构建的 `Omrina.Desktop.exe` 上，使用现有 synthetic 图像素材完成了单窗口 UI 验收。截图和 SVG 证据保存在相对路径 `artifacts/ui-acceptance-20260919/`，未上传截图或原图。

- 启动后仅有一个主窗口，标题为 `OMRINA 本地服务`；自定义 TitleBar、NavigationView 和页面内容属于同一窗口。“状态”“模板”“采集”“设置”“关于”均可切换，TitleBar 页面标题同步，模板页和采集页状态可缓存。
- 模板页修改标题后，保存 SVG、系统打印和导入图像操作均禁用；重新生成预览后恢复。离开模板页再返回，标题“验收模板”和模板 ID 仍保留。
- 从模板页进入“导入图像”会回到同一主窗口的采集页，并带入模板参数和模板 ID。
- SVG 文件保存成功，生成的 `template-acceptance.svg` 可读取且包含 A4 SVG 根元素；再次打开保存对话框后取消，界面显示“已取消保存 SVG”。
- 采集页取消文件选择后显示“已取消图像导入，未留下采集记录”；选择 synthetic 3×2 PNG 后显示导入成功、像素尺寸 `3×2` 和模板关联。
- Compact 与展开的 NavigationView 均可操作；TitleBar 最大化、恢复、最小化、恢复均可操作。本轮未形成窄窗口实际尺寸的重排证据，也没有执行实体扫描或打印。

macOS/Linux 未运行应用或设备后端，三平台 CI 和设备测试继续暂缓。

## 2026-09-23 M2 本地识别与评分回归

- `dotnet run --project tests/core/Omrina.Core.Tests.csproj --no-restore`：10 组通过。除既有模板检查外，识别回归覆盖四种直角方向、尺寸/透视变化、空白、多选、浅涂、定位块缺失与伪定位块、无效透视域，以及页级质量不足但每题仍判单选的情况。
- `dotnet run --project tests/m2/Omrina.M2.Tests.csproj --no-restore`：10 组通过。覆盖完整答案键、最终/临时/不可评分状态、人工复核历史、JSON/CSV 安全导出、透明与半透明像素白底合成、普通图像解码、超大输入在打开前拒绝、后台识别与取消、页级质量警告不会因逐题复核升级为最终分。
- `Omrina.Platform` 本机构建为 0 警告、0 错误。以上测试使用本地缓存依赖；初次 M2 测试运行缺少 Skia runtime asset，使用工作区已有 NuGet 缓存离线恢复测试项目后解决，没有新增网络下载。
- `tests/capture/Omrina.Capture.Tests.csproj` 在新增最新记录读取边界后运行 13 组通过。新增项覆盖最近有效记录、manifest/template schema 不兼容、原图路径越界，以及 manifest 或原图缺失时的明确错误；测试只使用新生成的 3×2 合成图。
- 对此前已由用户打印、填涂并采集的一张 2481 × 3506 图像，在本机按采集记录的完整模板 ID 和 20 题、每题 4 选模板运行自动识别。结果为 `Accepted`、方向 0°，20 道题与此前人工读值逐题一致。最浅的一个被选选项填涂量约为 0.265；该结果仅证明此样本的当前算法输出，不代表总体准确率。
- 本次没有再次驱动扫描仪、打印或向仓库加入原图、manifest、标准答案或成绩。真实样本未提供独立标准答案，因此不将这次识别写成真实自动评分验收。

桌面 M2 的 Windows 与 Uno Desktop 目标在最终文案改动后以隔离 `OutputPath` 各构建通过，均为 0 个错误、1 条 `NU1900`（NuGet 漏洞审计源不可达）。普通输出路径被正在运行的 OMRINA 窗口占用，所以没有关闭该进程；第一次隔离参数误将 `MSBuildProjectExtensionsPath` 传播给引用项目并产生 `NETSDK1005`，改为仅隔离输出目录后通过。Windows 实际窗口已成功启动，单窗口 NavigationView 可见“识别 / 复核”入口；工具随后检测到窗口有用户输入，停止自动点击，所以识别、复核、导出的完整 UI 交互本轮未验收。macOS/Linux 实机与三平台 CI 仍暂缓。

## 2026-10-02 M3 本机服务验证

- `dotnet build src/Omrina.Server/Omrina.Server.csproj --no-restore`：0 警告、0 错误。
- `dotnet run --project tests/server/Omrina.Server.Tests.csproj --no-restore`：82 项真实回环 HTTP / WebSocket 断言通过。覆盖精确来源与凭据绑定、Host 校验、桌面批准/拒绝、错误配对码锁定、任务与上传字节幂等、跨授权隔离、JSON/上传参数大小、配对和运行任务容量、撤销时取消设备查询、未认证 WebSocket 超时且无业务事件、畸形认证消息、事件序号、撤销关闭、停机取消与重启失效。业务实现为模拟对象，不调用实体设备。
- 首轮检查暴露了 WebSocket 撤销时接收取消导致连接中止，以及查询路由未正确返回结果的问题；修正后补充了断言并通过上述最终检查。
- 既有 Core 10 组、M2 10 组回归通过；M3 没有修改识别算法或评分规则。
- CaptureStore 的 13 组回归通过。`dotnet run --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj --no-restore` 的两组检查通过：实际业务适配器调用合成图上传、模拟扫描/识别、Core 评分/复核/导出，以及真实 Skia 识别器拒绝 40×40 低质量输入并不给分。覆盖自定义模板/采集/结果的跨授权隔离、版本递增与冲突、路径限制和撤销后的拒绝访问。成功识别输出为模拟对象，不是该小图的算法识别结果。
- Windows `net10.0-windows10.0.26100.0` 与 Uno `net10.0-desktop` 分别使用隔离 `OutputPath` 进行 `--no-restore` 构建，均 0 个错误、各 1 条 `NU1900`（漏洞审计源不可用）。未覆盖正在运行的应用，未新增设备操作或三平台 CI。
- SDK 的 `npm run typecheck`、`npm run build` 通过，TypeScript 5.9.3 是本次已授权的唯一新增开发依赖，运行时零依赖。`node --test tests/sdk/*.test.mjs` 14 项通过，包含既有健康检查、配对/任务/取消/错误、事件认证与恢复、认证成功后清除超时计时器，以及真实 Node fetch 中文上传参数头的回归。
- `npm pack --dry-run` 通过，包清单包含 10 个文件，含 JavaScript 与声明文件；未发布 npm 包。首轮因用户级 npm cache 写入 `EPERM` 失败，改用任务临时 cache 后通过。`dist/`、依赖缓存和构建输出未提交。
- `tests/integration/m3-sdk-e2e.mjs` 使用构建后的 SDK，对 `tests/m3-desktop` 的实际 Server + Desktop 业务适配器回环服务执行配对、默认模板查询、自定义模板生成、中文文件名 PNG 上传、识别、评分、复核、JSON 导出、任务列表恢复与撤销授权，全链路通过。核对 10 题得 10 分，修订第 1 题后 9 分，结果版本 2→3，JSON 导出包含复核历史。图像为 40×40 合成 PNG，扫描器与成功识别输出为模拟实现；评分、复核与导出使用 Core。测试结束已撤销临时授权。
- 直接适配器测试最后补充断言时，普通及重复使用的临时输出被运行中的测试 harness 锁定，产生 `MSB3026` / `MSB3027`。停止该测试 harness 并使用新的独立输出目录后构建与测试通过；未关闭用户桌面窗口，未修改构建依赖。
- 已启动仅监听回环的开发静态页面 `127.0.0.1:17846` 和模拟服务 `127.0.0.1:17844`，SDK JavaScript 构建成功。内置浏览器和 Chrome 的自动化工具打开页面均返回 `net::ERR_BLOCKED_BY_CLIENT`，未进行页面交互、跨来源 CORS 或浏览器 WebSocket 验收。没有改变浏览器策略或绕过安全检查；测试服务随后已停止。可按 SDK README 在正常浏览器中手动复核。

## 2026-10-02 M3 收尾与 M4 SDK 消费者检查

- 服务端追加回归后共 100 项断言通过，构建 0 警告、0 错误。待配对申请自然过期或错误次数耗尽后会通知刷新；有效授权的事件队列溢出及第 9 个事件连接使用 `1013`，不会误报授权撤销。真实 WebSocket 检查确认前 8 个订阅认证成功，第 9 个收到 `1013` 后同一授权仍能查询任务；队列溢出直接测试生产的 128 条缓冲区，未声称模拟了真实网络背压。
- SDK 的 14 项测试、类型检查和构建通过。已建立事件订阅在 `1013` 后保留授权、重连并查询任务恢复；握手阶段或认证后的 `1008` 清除内存凭据并停止订阅。
- `node tests/sdk-package/consumer-smoke.mjs` 通过：使用本机已安装的 TypeScript 5.9.3 编译，再实际执行本地 `npm pack`，检查 tarball 恰好包含 10 个 README/manifest/JavaScript/声明文件，在解包后的临时 `node_modules` 中通过包入口运行 Node 导入，并严格编译 TypeScript 消费者。未联网安装或发布。验证主机 Node 为 v25，不代表所有 Node 或浏览器版本验收。
- 设备身份回归通过：同一会话的 A/B 设备正反排序保持标识稳定，设备移除后旧标识不可解析，驱动参与匿名摘要；缺失身份或二重/三重重复身份全部排除。测试使用纯 .NET 映射和模拟设备，没有驱动真实扫描仪。
- 最新扫描修复的 Windows 与 Uno Desktop 目标顺序构建通过，均为 0 错误、1 条已缓存的 `NU1900`（审计源不可用）。命令使用 `--no-restore`、两个独立 `OutputPath` 与 `NuGetAudit=false`，没有恢复或下载依赖，没有覆盖正在运行的应用；关闭此次联网审计不代表漏洞审计已通过。macOS/Linux 实机仍未验收。

Windows 开发包尚未通过隐私检查，正式 Release、干净机器安装与签名尚未验收。首轮 Release publish 报 `UNOB0019`（Uno DevServer 不支持优化构建）；Debug probe 输出包含 Core 与 ASP.NET Core 10.0.0 framework 要求和 Windows App SDK runtime payload。产物中的 Uno MCP 元数据 `UnoMCPProcessorPath`，以及 Hot Design 生成的 `ApplicationPreviewsFolder` 元数据和 `ServerProcessorPathAttribute` 曾嵌入本机绝对路径；尝试独立编译缓存及已有禁用/路径属性后，仍有路径残留，不能作为可交付包。独立发布复制还曾因磁盘空间不足报 `MSB3026`；已只清理本轮创建的探测目录，未改动用户文件或依赖缓存。这些失败不计为成功包，未上传产物。当前分发计划见 [M4 架构](../architecture/m4.md)。

`scripts/package-windows-dev.ps1` 已通过 PowerShell 语法解析和只读审查。`-TestPrivacyScanner` 合成自测通过，覆盖隐藏目录路径命中、无路径时空结果与本次暂存清理；对已有本轮发布探测目录的实际审计返回退出码 1，错误为 `PRIVACY_PATH_EMBEDDED:Omrina.Desktop.dll`，未输出绝对路径、未创建最终包，暂存残留为 0。探测产物的 16 项关键运行文件齐全，runtimeconfig 要求 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 10.0.0。脚本默认整包发布到成功产物的完整路径尚未运行验证；以上只证明扫描门禁和清理行为，不能算 Windows 包验收通过。

## 2026-10-02 M4 Release 开发包后续验证

本节是上述初次分发准备之后的结果。Windows Release 构建与产物路径残留阻塞已解除；M4 的安装、签名及实机验收仍未完成。

- 确认 `UNOB0019` 根因是 Release 发布使用了 Debug 依赖图。Uno SDK 已按优化配置排除开发资产，修复不关闭优化、不修改缓存库或项目依赖。
- 脚本现在以 `CustomAfterDirectoryBuildProps` 为各项目隔离资产、中间文件和输出，用唯一的空本地源与已有 `.tools/nuget-packages` 缓存恢复 Release，再以同配置 `--no-restore` 发布。六项目资产的归属和来源、合法框架/运行标识以及开发组件导入排除检查通过；标准 Desktop `obj` 仍保留原 Debug DevServer 导入，未覆盖默认开发输出。
- 首轮检查曾错误要求库项目具有桌面运行目标、以及桌面恢复图只能包含单个目标，已按各项目实际声明修正。安全诊断随后发现恢复命令的全局 Windows `TargetFramework` 覆盖了库自身的 `net10.0`；移除该恢复参数后框架错配检查通过。发布仍明确选择 Windows 目标与 `win-x64`。这些失败均在发布前停止并清理暂存，未算为通过。
- 实际编译曾报 `WMC9999`。脱敏错误说明及只读 MSBuild item 检查确认，旧 `tmp/m2-final-check` 构建中的主题 XAML 和生成 C# 被当成 `Page`/`Compile` 输入。临时构建配置排除生成目录后，四类项目条目不再含该旧输出；没有删除或读取用户扫描数据，也没有删除旧目录。
- `pwsh -NoProfile -File scripts/package-windows-dev.ps1` 完整执行退出码 0，优化 Release 的恢复、发布、关键文件、运行框架、相对路径清单及完整产物隐私扫描全部通过。唯一包位于忽略目录 `artifacts/windows-dev/omrina-windows-x64-dev-release-87c241feccce4d27a91a804692824458/`，未上传二进制或发布版本。成功构建日志随暂存清理，无法还原警告数量，因此不声称 0 警告。
- 包内 README 的框架列表曾因数组拼接显示 `System.Object[]`；已修正生成逻辑，仅重写本轮包的说明与清单，未重复编译。最终为 418 个文件、197,790,457 字节，417 项 SHA-256 全部匹配，0 PDB、0 暂存残留。修正后的说明内容与本机路径检查通过。
- 包内 `app/Omrina.Desktop.runtimeconfig.json` 请求 `Microsoft.NETCore.App 10.0.0` 与 `Microsoft.AspNetCore.App 10.0.0`；Windows App SDK payload 随包提供，.NET 仍依赖目标机提供兼容共享框架。
- PowerShell 解析、隐私扫描合成自测、诊断脱敏合成自测和 diff 检查通过。最终追加的 `WMC9999` 回归确认有用说明保留，工作区、drive/UNC 路径及多种敏感字段的假值被遮蔽；仅重跑解析与该自测，未重复发布。没有改动业务代码，因此没有重复运行已通过的 Core、SDK 和 HTTP/WebSocket 回归。未启动该新包进行 UI/运行验收，未扫描或打印。

## 2026-10-02–03 M5-A 主观题批阅基础

本阶段先实现独立于纸面版式的人工指定区域与评分基础，未变更现有选择题模板 schema。完整 M5 仍等待模板样式、自动定位和批阅界面。

- 新增受鉴权的题目区域 PNG 接口后，`dotnet run --project tests/server/Omrina.Server.Tests.csproj --no-restore` 最终 120 项断言通过（包含原有 100 项及新增 20 项），退出码 0。覆盖 Origin/Bearer/Host、相同 Origin 的新授权隔离、非法 ID 与 query、PNG 签名与 8MiB 上限、`no-store`、错误脱敏、取消及授权撤销。
- 图片接口有独立的 32 请求并发上限；底层适配器若忽略取消，会占用请求槽直到真正结束。测试验证过载返回 429、不合作适配器不会通过取消逃脱上限。适配器取消的 504 与授权撤销的 401 已分别验证；没有等待真实 30 秒来宣称实际网络超时验收。
- Core 以 `dotnet build tests/core/Omrina.Core.Tests.csproj --no-restore -p:OutDir="artifacts/m5-core-build-20261002-01/out/"` 独立构建，0 警告、0 错误；运行生成的测试 DLL 后 11/11 组通过。新主观题回归有 67 个断言调用点，覆盖整数边界、整批原子性、分值/状态、版本冲突、历史限制与篡改拒绝。首轮三个签名/类型编译错误与两处测试断言问题已修复；审查发现的无变化伪造审计批次也已增加拒绝与回归。保留忽略目录中的本轮构建产物。
- SDK `node --test tests/sdk/*.test.mjs` 首轮 17/17 通过；`dotnet build tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj --no-restore` 0 警告、0 错误，随后 `--no-build` 运行通过。新增回归覆盖真实 PNG/JPEG 区域像素、双题批阅版本与状态、CSV 公式转义、持久化重开和存储故障。审查发现的 JPEG 位图提前释放、采集 ID 的 N/D 格式不一致、取消后 Promise 未观察拒绝及响应限额问题均已修正。
- `node tests/sdk-package/consumer-smoke.mjs` 退出码 0：TypeScript 5.9.3 构建、实际 tarball 清单、通过包入口消费 JavaScript 与严格 TypeScript 声明检查通过。只使用已有开发依赖，没有安装或发布 npm 包。
- `node --check tests/integration/m5-sdk-e2e.mjs` 通过。该脚本将程序批准限制在本轮测试 harness 的 stdin，生产服务没有自动批准路由；版本与授权失败路径要求准确错误码，超时或普通 500 不能作为隔离验证成功。
- `node tests/integration/m5-sdk-e2e.mjs tests/m3-desktop/bin/Debug/net10.0/Omrina.M3.Desktop.Tests.dll` 最终退出码 0。通过真实 Server + Desktop 适配器 + SDK 验证合成 40×40 PNG 上传、10×10/20×10 区域图、两题草稿与确认、未批完小计为空、过期版本和确认后直接改分均无副作用、退回草稿后重新确认小计为 21、JSON 完整历史与 CSV 内容。第二个同 Origin 授权读取/改分/取图获得准确拒绝码；撤销后既检查 SDK 本地清权，也用旧凭据实例确认真实服务返回 401。测试授权在 `finally` 撤销，harness 收到 `quit` 并确认子进程退出。没有验证浏览器权限、自动定位或真实纸张。
- 端到端脚本初次运行中的回调初始化、校验函数误名、超过模板限制的测试标题和 CSV 引号格式问题均已修复，最终未放宽断言。
- Windows `net10.0-windows10.0.26100.0` / `win-x64` 与 Uno `net10.0-desktop` 两目标的 Debug 隔离构建最终均退出码 0，分别 1 条 `NU1900`、0 错误。使用 `--no-restore`、独立 `OutputPath` 和临时 `CustomAfterDirectoryBuildProps`，未覆盖默认资产或正在运行的应用，未删除旧 `tmp`。Windows 编译发现的 `FileAttributes` 命名冲突已明确限定为 `System.IO.FileAttributes`。
- 首次桌面尝试出现 `WMC1509` / `WMC9999`；沿用 M4 验证过的开发工具禁用参数后消失。最终命令包含 `UnoDisableMCPSupport=true`、`UnoDisableHotDesign=true`、`UnoDisableHotDesignAgent=true`、空 `HotDesignPreviewsFolder` 与相对 `ApplicationPreviewsFolder` / `HotDesignSolutionDir`，没有关闭 XAML 编译或跳过业务错误。以上是本轮临时构建条件，不代表默认开发工具、热重载或 UI 已验收。
- `NU1900` 提示离线漏洞数据源不可用；命令未还原或安装包，并设置 `NuGetAudit=false`。该警告不计为漏洞审计通过。构建产物位于忽略的 `artifacts/m5-target-check-95029850dc084505981bd33024085e1a/`，没有重新发布 M4 开发包或上传二进制。
- 本轮业务验证只使用本地合成测试与已有缓存，未新增依赖、调用扫描设备或读取真实采集图像。SDK 和桌面改动已做最终窄审查，修复后没有新增问题；未运行真实桌面交互、打印、扫描或三平台实机验收。
- 随后收紧无效区域的错误映射并增加断言，仅重跑受影响的 `dotnet run --no-restore --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj`，最终通过；未重复已通过的其他检查。

## 2026-10-03 M5-B 本地批阅与记录重开

本阶段沿用现有 NavigationView / TitleBar，新增本地批阅页及可信本地服务，不变更选择题模板或 SDK 协议，不新增依赖。采集与批阅列表按资源 ID 分页，并非按时间排序；损坏记录占用每页额度且返回诊断，仍可继续加载下一页。

- `dotnet build --no-restore tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj`：0 警告、0 错误。
- `dotnet run --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj --no-restore`：通过。新增覆盖跨存储实例的本地/网页版本竞争只允许一方成功、保留原授权归属、本地归属与网页隔离、写入失败与提交前取消不改版本、重启恢复历史和裁图、缺失原图仍读取/导出评分、损坏记录有界分页与续页、非法游标及路径拒绝；同时运行输入解析回归。
- `node tests/integration/m5-sdk-e2e.mjs tests/m3-desktop/bin/Debug/net10.0/Omrina.M3.Desktop.Tests.dll`：新 DLL 的 18 个阶段全部通过，覆盖配对、合成图上传、草稿/确认、版本冲突、PNG 尺寸、JSON/CSV、授权隔离和撤销。自动批准仅用于测试 harness，不增加生产自动授权入口。
- 只读审查发现损坏记录未占分页额度，可能导致一次读取过多记录；已修复并补充临时目录内损坏采集清单与批阅记录回归。每页最多检查 50 条候选，纯诊断页也可返回续页游标。
- 界面审查发现重新加载可能覆盖请求期间的新输入、保存与重新加载并发可能显示旧版本，以及关闭窗口取消各页的时机过晚；已分别改为应用响应时保留最新输入、读写互斥和同时取消各页，窄复查通过。复查还发现创建与评分共用取消源，已补充按钮和操作入口双向互斥，并确认评分入口检查创建状态。
- 首次 Windows 隔离构建发现存储分页 DTO 与新 UI 页在同一命名空间重名；将内部 DTO 更名为 `SubjectiveReviewStorePage`，不改动 SDK 协议。更名后重新执行 M3 `build --no-restore`（0 警告、0 错误）和 `run --no-build`，回归通过；纯内部类型更名未重复网络 E2E。
- Uno Desktop 首轮编译提示 `Uno0001`：`DataWriter.DetachStream()` 尚未实现。改为在 `BitmapImage.SetSourceAsync` 完成前保持写入器与内存流存活，并移除该调用；最终两个目标不再出现该警告。
- Windows `net10.0-windows10.0.26100.0` / `win-x64` 与 Uno `net10.0-desktop` 的 Debug 隔离构建最终均退出码 0，分别 1 条 `NU1900`、0 错误。使用 `--no-restore`、独立输出、临时 `CustomAfterDirectoryBuildProps` 和 M4 已使用的 HotDesign/MCP 禁用参数，排除旧 `tmp` 生成文件，不修改默认资产或删除旧输出。产物位于忽略目录 `artifacts/m5b-ui-c48ab728c6e04b4fbddd5f598089850e/{windows,uno}`。`NU1900` 是缓存中的离线漏洞数据源警告，不计为漏洞审计通过；默认开发工具与热重载未验收。
- 最终 `git diff --check` 通过，仅 Git 行尾转换提示；UI 源码的本机路径与凭据检查未发现问题。
- 本轮只使用临时合成数据和缓存依赖，未读取默认用户数据目录、扫描设备或用户 `design.png`；没有重新打包或上传二进制。真实 UI 操作、系统缩放和三平台实机测试不计为通过。

## 2026-10-03 M5-C 生成时定义题区

本阶段按用户要求把区域定义移到答题纸生成流程：纸面毫米题区参与模板身份，批阅创建仅引用采集记录。保持旧选择题 schema1 的身份和读取路径，新增混合模板 schema2；不修改真实采集或已有评分数据。

- `dotnet build src/Omrina.Core/Omrina.Core.csproj --no-restore`：0 警告、0 错误。
- `dotnet run --project tests/core/Omrina.Core.Tests.csproj --no-restore`：最终 12 组通过。新增覆盖旧 schema1 身份与 SVG 保持、schema2 严格 JSON 往返和篡改拒绝、题号与区域冲突、描边相碰、四方向及透视映射、选择题需复核但页面定位独立合格、缺失定位块拒绝、取消，以及定位结果与原图尺寸绑定。
- SDK 首轮 `typecheck` 与 `node --test tests/sdk/m3.test.mjs`：9/9 通过；模板题区使用 `rectangleMm`，题号范围与 Core 的 Int32 一致，批阅创建拒绝旧像素题区参数。最终产物与端到端结果见下文。
- `dotnet build src/Omrina.Platform/Omrina.Platform.csproj --no-restore`：四角透视裁图最终构建 0 警告、0 错误。`SubjectivePerspectiveRegression.RunAsync()` 已挂接并在 M3 runner 中通过，覆盖 PNG/JPEG 四方向透视像素、非法区域、取消和解码槽释放。
- 跨模块只读审查确认模板与原图同 staging 提交、旧 schema1 身份兼容和评分关联不可变；发现映射重读需要拒绝非有限变换角点，并核对校正尺寸与原四角推导一致。已补充有限值、投影分母同号且非零、凸性、边界框与 Core 推导尺寸校验，窄复查通过。创建先定位和映射再保存，定位失败不创建记录；本地和网页新题图均使用四角校正，旧无映射文档保留像素裁图路径。
- 首次整链 M3 编译因测试项目未链接新增 `SubjectiveCaptureTemplateMapper.cs` 出现 8 个错误、0 警告；补充链接后 `dotnet build --no-restore tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj` 0 警告、0 错误，`dotnet run --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj --no-restore` 通过，覆盖 schema2 采集快照、页面定位映射、旧像素评分记录读/改/导出、本地与网页授权归属、版本竞争、持久化及 UI 输入解析。没有删除功能或放宽类型检查。
- Windows `net10.0-windows10.0.26100.0` / `win-x64` 与 Uno `net10.0-desktop` 的离线隔离构建均退出码 0、0 错误，各有 2 条 `NU1900`。使用缓存资产和每项目独立 intermediate/output，产物在忽略目录 `artifacts/m5b-ui-final-5aeb1f8aaef94a1987567ae52fa8ee40/output/Omrina.Desktop/`；没有启动应用。该警告不计为漏洞审计通过，真实 UI、高 DPI、打印与设备测试仍未运行。
- SDK `npm --prefix packages/sdk run typecheck`、`build` 与 `node --test tests/sdk/*.test.mjs` 最终通过，17/17。
- `node tests/integration/m5-sdk-e2e.mjs tests/m3-desktop/bin/Debug/net10.0/Omrina.M3.Desktop.Tests.dll` 完整通过。使用 schema2 第 11/12 题区域和 420×594 合成定位页，经真实 HTTP 验证模板生成、上传、仅传 `captureId` 创建、区域坐标与校正 PNG 尺寸、草稿/确认/版本冲突/修订后重确认、JSON/CSV、跨授权题图 404 与批阅读取/改分拒绝、撤销后 401。首跑脚本遗漏数组校验 helper，随后错误要求稳定 Guid 的 RFC 版本位；已补 helper 并按 Core 的普通 Guid D 格式验证后通过，没有放宽授权与评分断言。
- 最后 UI 窄审发现创建期间可重新读取旧记录，以及采集参数变更会清空主观区域来源。已补创建/重读互斥，并保留最后模板布局、只标记参数过期；重新确认前仍禁止导入/扫描。修复后仅重跑受影响桌面构建。
- 上述两处 UI 修复窄复查通过。最终 Windows/Uno 构建在第二个唯一目录 `artifacts/m5b-ui-final-90fce54292384377a4bc4327d7af05e7/` 再次退出码 0、0 错误，仍各有 2 条 `NU1900`；未重复与纯 UI 状态修复无关的后端测试。
- `node tests/sdk-package/consumer-smoke.mjs` 通过：实际 npm tarball 文件清单、严格 TypeScript 声明消费和 Node 实际导入均通过；产物在忽略目录 `artifacts/sdk-package-consumer-H7lG1O/`。未联网安装或发布包。
- M3 回归明确拒绝旧无主观题模板采集的新建请求（`SUBJECTIVE_TEMPLATE_REQUIRED`）和客户端自带像素题区（`INVALID_PARAMETERS`）；已有像素评分文档仍可读、改、导出。此轮只使用合成图和临时数据，没有操作真实采集或用户 `design.png`，未重新打包桌面分发产物。

## 尚未验证

- 取消中的驱动行为、多页和不同设备兼容性。
- HTTPS 浏览器到真实本地服务的 HTTP / WebSocket 连接。
- 纸面打印比例、不同设备兼容性和重复采集。
- 窄窗口尺寸、系统缩放和多显示器切换下的 UI 重排。
- 配对授权界面的完整交互、真实网站与扫描设备的业务联调。
- macOS / Linux 实机 UI 与扫描验收；三平台 CI 当前暂缓。

SDK 单元测试使用模拟响应，只证明客户端行为；M3 的真实 HTTP / WebSocket 与浏览器检查另列记录，不替代上述实机验收。

## 已执行静态检查

桌面项目 `.csproj`、应用清单和两个 XAML 文件已通过 XML 解析。该检查只证明 XML 格式有效，不能替代 XAML 类型检查、C# 编译或界面运行。

## M0 SDK 自动测试（历史记录）

执行 `node --test tests/sdk/*.test.mjs`，7 项通过。覆盖回环端点限制、固定健康检查路径、重定向与凭据策略、请求超时、读取响应体超时、调用方取消、HTTP / 协议错误及定时器上限。

当时使用 Node.js 直接擦除 TypeScript 类型运行测试，未执行 TypeScript 编译器类型检查或 npm 产物构建。M3 新增正式编译，结果按后续记录解释。

## 2026-10-03 Windows 开发包启动修复

- 用户报告的旧开发包启动后无窗口或闪退。对应的 Application Error 1000 记录指向 `Microsoft.UI.Xaml.dll` 3.2.3.0，异常码 `0xc000027b`；没有取得托管异常堆栈。代码检查发现两个 XAML 初始化期事件可能在后续控件连接前访问控件：主观题评分输入的 `TextChanged`，以及模板题数的 `ValueChanged`。
- 在 `SubjectiveReviewPage` 与 `TemplateWindow` 中让初始化期事件处理器等待 `InitializeComponent()` 完成，再显式刷新状态。未吞掉异常，也未加入诊断日志。
- `pwsh -NoProfile -File .\scripts\package-windows-dev.ps1` 退出码 0，生成 `artifacts/windows-dev/omrina-windows-x64-dev-release-fab3a81e2cc64a7ab72357f3242f75f2/`。启动其中的 `app/Omrina.Desktop.exe` 后，进程至少运行 13 秒并创建了窗口句柄，随后通过关闭主窗口正常退出；该次启动没有新的匹配崩溃事件，标准输出与错误输出均为空。这支持初始化重入修复方向，但未捕获旧异常的精确堆栈。
- 对包内 `SHA256SUMS.txt` 的 417 项逐一重新计算并全部匹配；包共 418 个文件、无 PDB。runtimeconfig 要求 `Microsoft.NETCore.App` 和 `Microsoft.AspNetCore.App` 10.0.0，因此此包是 framework-dependent，需要安装相应 .NET 10 运行时；本机已有 10.0.12。打包脚本未保留本次构建日志，警告数量无法确认。
- 这只是启动存活检查，不代表完整 UI 交互验收。未操作扫描、打印或网络功能，也未查看采集图像或采集内容。

## 2026-10-03 学校版式与考试工作流

实现范围：schema3 学校答题纸、A4 纵向与 A3 横向两栏/三栏、多页与可选反面身份区、考试二维码、Code128 考号/数字涂卡、圆形/矩形及框内外印字、逐题主观区域高度与满栏宽度。预览、SVG、打印和裁切共用版式；打印请求使用实际毫米，A4 双面长边、A3 双面短边翻转。桌面加入考试入口、不可覆盖版式版本、共享答案、按考试筛选答卷与批阅、未保存输入保护；移除右上小标题，侧栏设为 Left，Windows 支持时启用 Mica。

已执行：

- ZXing.Net 0.16.11 显式依赖及恢复已获用户授权；恢复配置清除在线源，仅使用本地缓存。
- Core 13 组回归通过，含新增学校专项：分页/全栏宽/逐题高度、schema3 严格复算与篡改拒绝、2 mm 合法及 2.01 mm 拒绝、圆/矩形与字母/数字内外位置、空白/填涂、四方向与透视、考号空白/多涂/不确定、二维码错考试/错页拒绝、Code128 多考号歧义、真实题号与每题分值。字形采用保守合成占位，这不证明真实字体或实纸准确率。
- Capture 13/13、M2 10/10、Server 120 项通过。Capture 首跑一项将已支持 schema2 误当作未知版本，改用 99，并要求 `CAPTURE_TEMPLATE_UNSUPPORTED`；未删除或跳过断言。
- M3 聚合回归及学校集成组通过：A3 Skia 合成图、考试码/考号、错误考试拒绝、主观题映射、纯主观题保存、不可覆盖版式与共享答案。新增身份回归确认：未知考号经识别、评分、全部选择题人工确认和 JSON 导出后仍为 `RequireAssociation` / `Provisional`。
- 最后补充结果容量边界：64 页合法版式超出 1 MiB 任务结果上限时，在注册之前返回 `TEMPLATE_DOCUMENT_TOO_LARGE`，失败后的页面 ID 仍为 `TEMPLATE_NOT_FOUND`，不遗留可见模板。模拟扫描确认请求 A3，并返回考试元数据、考号和身份状态；没有连接真实扫描设备。
- SDK 类型检查、构建及 19 项测试通过；真实 HTTP 学校流程覆盖配对、生成、上传 A3、考号读取、非连续题号复核、错考试拒绝、目录授权隔离及撤销。旧 M5 HTTP 流程重新通过，保留 schema2 上传、题图、原子评分、版本冲突、导出和授权隔离。
- npm 实际 tarball 消费者检查通过：仅 README/manifest/dist、严格 TypeScript 声明消费与实际 JavaScript 导入；未发布 npm 包。
- 最终 Windows `net10.0-windows10.0.26100.0` 和 Uno `net10.0-desktop` 构建均为 0 警告、0 错误。首轮出现 StackPanel 无 `IsEnabled` 和 `FileAttributes` 歧义，已分别改为控件容器与明确命名空间后修复。
- `scripts/package-windows-dev.ps1` 最终退出 0，生成 `artifacts/windows-dev/omrina-windows-x64-dev-release-a2cd0c0983e94779a82d03961ab26118/`；417 项哈希重新计算全部匹配，包共 418 个文件，无 PDB。打包脚本已检查运行文件和隐私路径，未保留最终 publish 日志，因此不报告其警告计数。该包需要 .NET 10 与 ASP.NET Core 10 运行时。
- 相关 XAML 解析、事件处理器检查、diff 检查及上下文静态复查通过。修复了跨考试/版式/版本残留旧识别结果、旧异步结果回写、未保存答案重置，以及答案读取失败后不能重试的问题。

本轮限制：

- 用户要求不使用 computer-use，已停止工具初始化与窗口枚举后续操作；未启动或操作新版界面。启动存活、真实导航、侧栏挤压、Mica 回退、窄窗口和高 DPI 仍待人工验收。
- 未操作真实扫描仪或打印机。A3 软件请求不代表当前设备支持 A3，实际比例、小填涂框与二维码仍需实纸测试。
- 跨页人工考生关联、漏页/重复页汇总、整卷综合成绩、图片正文、跨页长题及 OCR/AI 尚未实现。无可靠身份页面保持待关联，页面选择题分数不代表学生最终总成绩。
- macOS/Linux 实机及三平台 CI 继续暂缓；未修改用户 LOGO 文件，也未发布版本。


## 2026-10-03 填涂尺寸与精度初步接入

- 新建矩形高度与圆直径限制为 0.1–2 mm；矩形宽度取消统一 2 mm 上限，按实际选项、考号栏宽与示例／机读码空间校验。宽框动态调整间距，放不下时明确拒绝。Core 与 SDK 使用同一十分之一毫米网格规则；容差内浮点漂移归一，1.85 mm 等非网格输入不静默舍入。
- 保存格式仍为 schema3 毫米字段。历史快照按原尺寸与几何严格复算身份；编辑器打开旧版、重新登记既有版本及标准答案读取不套用新建限制。新增回归使用上一版 Core 生成的固定快照（宽 1.85 mm、高 3.95 mm），未使用真实考生信息或用户 LOGO。
- `dotnet run --project tests/core/Omrina.Core.Tests.csproj --no-restore -p:NuGetAudit=false`：13 组通过。学校组补充宽矩形合成识别、尺寸精度、栏宽／机读码区域冲突、历史快照身份与原几何回放。
- `dotnet run --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj --no-restore -p:NuGetAudit=false`：聚合回归与学校集成组通过，含历史考试及标准答案保存／读取。
- SDK 全部 19 项测试、`typecheck`、`build` 通过；学校请求覆盖矩形宽 2／3／4 mm、高 2 mm，以及高度超限、圆直径超限、非网格和非法数值拒绝。
- Windows `net10.0-windows10.0.26100.0` 与 Uno `net10.0-desktop` 离线 Debug 构建均为 0 警告、0 错误；未联网、未新增依赖。
- 本轮为初步代码交付，未运行 computer-use、启动程序、真实扫描或打印，也未重新生成 Windows 分发包。连续步进交互、高 DPI、打印比例及实际字体／填涂准确率仍待人工验收。题组、整组裁切、混排／分区与 Data Matrix 尚未实现；此前开发包不包含本轮改动。


## 2026-10-03 题组模型、编辑与排版

- 按新根目录 AGENTS.md，移除上一批仅为旧开发版本保留的尺寸回放、旧控件高度例外及对应历史 fixture；未删除或清理用户资料。之前的旧版兼容验证属于历史记录，不再是当前行为保证。
- 接入显式题组、Mixed/Separated 排序、同题型成员完整唯一覆盖、整组满栏几何／换栏换页、组框与内部分隔线、约 6 mm 的客观题紧凑纵向行。桌面加入组名／组顺序／归属／组内排序；新模板保存明确组定义。
- Core 学校专项及全量 13 组回归通过，含题组顺序、校验、完整换栏、满栏宽、独立主观题高度、溢出拒绝、几何回算与识别。SDK typecheck/build 及 21 项测试通过，含分组严格校验与原样保留请求顺序。
- M3 学校与聚合回归通过：显式组写入考试存储、学校模板任务返回组几何、采集／识别／逐题批阅保持原题号与授权隔离。
- Windows 与 Uno Debug 离线构建均为 0 警告、0 错误。学校真实 loopback HTTP SDK 联调通过，覆盖配对、显式组摘要、A3 合成采集、考号、识别、非连续题号复核、错误考试拒绝、目录隔离与撤销。
- HTTP 联调首跑因新增断言仍期待默认组 ID，但 harness 使用显式组而失败；修正为核对请求定义中的组 ID 后重跑通过，未放宽断言或修改业务代码以绕过。
- 本轮尚未接入整组裁切／批阅和 Data Matrix，不支持同组混合题型、跨栏／跨页题组或组内多块分列。没有 computer-use、界面启动、真实扫描或打印，交互、高 DPI 与实纸效果仍待人工验收。未新建依赖、联网或重新打包，旧分发包不含本轮功能。

## 2026-10-03 题组完整流程、Data Matrix 与离线模板

实现范围：Data Matrix 页面短码与校正读取、完整性校验的离线模板包、考试页文件导入／导出、客观题组内 1–3 列、共享整组裁图、组内逐题批阅和分组导出、本机服务与 SDK。当前开发版本不保留旧 QR 或旧学校定义兼容；无效本地资料按文件显示诊断并保留原文件。

- Core 全量回归 14 组通过；学校专项另通过。覆盖四方向、低分辨率、双码歧义、空白／填涂、固定短码、实际符号占位与留白；多列组成员顺序、组高、不重叠、正文／题型限制与超宽拒绝；模板包往返、重建完整页身份、null 页、未知／重复字段、篡改摘要与未知码拒绝。
- M3 完整回归通过，包含学校采集、模板关联、组图及本地／网页批阅。组专项使用合成 RGB 纸验证大于旧 4Mpx 限额的组图、透视下九个实际像素、同组两题独立草稿／确认／修订、版本冲突、重开和 JSON／CSV，纯客观题组也能裁切。身份专项确认 Code128 已识别考号保持 `Identified`，未知或缺失采集身份保持 `RequireAssociation`，不从分数推断考生。
- SDK 正式 typecheck／build 与 28 项单元测试通过。真实 loopback HTTP 学校流程通过：A/B 授权隔离、导出模板包后由另一授权导入、页面短码一致、hash／未知／重复字段拒绝且无登记残留、共享组图、组内分别评分和导出、撤销后 401。SDK 预先拒绝超过完整 64 KiB 请求限额的模板导入；本地文件上限仍为 1 MiB。
- `node tests/sdk-package/consumer-smoke.mjs` 通过：实际 npm tarball 清单、严格 TypeScript 声明消费及 JavaScript 入口均有效；没有发布 npm 包。
- 静态复查修复三处 UI 状态问题：重设评分后旧输入阻止切题、替换题组项引发选择事件重入、旧组图任务清除新请求 pending 标记。异步返回仍校验 generation 和当前资源，评分仍按题目 ID 提交。
- 最后补充目录回归：缺少当前字段的旧学校定义被单独诊断，文件字节保持不变，新模板仍可保存／导入；同版本路径冲突明确拒绝且不覆盖文件。统一组图 ID 与模板规则后，合法 `g.1` 组图专项通过；非法首位、路径、来源、Host、跨授权、并发与撤销检查保持有效，Server 最终 136 项全部通过。
- 最终 Windows `net10.0-windows10.0.26100.0` 与 Uno `net10.0-desktop` 离线 Debug 构建均为 0 警告、0 错误。两处首轮 UI 编译问题（缺少组几何字段、pattern 变量重名）已修复；上述状态修复及目录／ID 补查后重新编译通过。
- `pwsh -NoProfile -File scripts/package-windows-dev.ps1` 离线 Release 打包退出码 0，产物为 `artifacts/windows-dev/omrina-windows-x64-dev-release-af6ef7ec23ab4ea1a4565f7c6ace7fb6/app/Omrina.Desktop.exe`。417 项 SHA-256 重新计算全部匹配，共 418 文件、无 PDB；脚本运行文件和隐私检查通过。包需要 .NET 10 与 ASP.NET Core 10 运行时；未启动新包，不能据此声明启动或界面验收通过。脚本不保留最终 publish 日志，不报告 Release 警告计数。
- 测试首次失败包括旧合成夹具将机读码拉伸到整个占位，以及模板包断言将 `maximumScore` 误认作评分记录。分别修正为真实纸面的模块／留白几何和精确排除字段；没有降低识别、完整性或授权断言。审查发现的 null 页输入已改为明确拒绝。

本轮使用现有依赖和离线缓存，未新增依赖或下载。没有启动应用、操作界面、真实扫描或打印；未读取或处理用户 `design.png`。Data Matrix 的 0.5 mm 单格与小填涂框仍需实纸验收，合成回归不能证明跨设备准确率。三平台实机及 CI 继续暂缓；同组混合题型、跨页题组、人工跨页考生关联、整卷综合成绩、图片正文与 OCR／AI 不属于本轮已交付能力。
