# OMRINA Desktop

此目录是 OMRINA 本地 Integration Agent 的 Uno 桌面入口。项目只包含 Windows、macOS 和 Linux 桌面目标，不包含 Android / iOS；Windows 使用 WinUI 3 / Windows App SDK，macOS/Linux 使用 Uno Skia Desktop。这里提供单窗口导航、本地健康检查、模板操作、图像导入和平台扫描适配，不包含完整阅卷、识别或评分功能。

当前已验证 Windows 单窗口导航 UI、Windows 与 Skia desktop 的代码级构建，以及 CaptureStore 回归；macOS/Linux 实机运行、设备发现、文件选择器和扫描尚未验收，不能把编译通过写成三平台验收。

## 固定依赖

- .NET SDK 10
- `Uno.Sdk` 6.7.30（由根目录 `global.json` 固定）
- `Microsoft.WindowsAppSDK` 2.4.0
- `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705
- `Microsoft.Windows.SDK.BuildTools.WinApp` 0.6.1
- `NAPS2.Sdk`、`NAPS2.Images.Gdi`、`NAPS2.Images.ImageSharp`、`NAPS2.Sdk.Worker.Win32` 1.3.0
- `SkiaSharp` 3.119.2（与 Uno desktop native asset 对齐，用于图像 codec 校验）

`NAPS2.Sdk.Worker.Win32` 允许 64 位主程序通过 NAPS2 的 x86 Worker 枚举和使用常见的 32 位 TWAIN 驱动。M0 使用它验证设备枚举与单页扫描。

NAPS2 1.3.0 的默认驱动在 Windows 为 WIA，在 macOS 为 Apple/ImageCaptureCore（ICA），在 Linux 为 SANE；macOS 使用系统 ImageCaptureCore，Linux 需要系统安装并配置 SANE 后端。本项目没有用虚拟设备填充空列表，也没有在本轮进行 macOS/Linux 物理设备测试。

NAPS2 1.3.0 的 SDK、GDI 图像组件和 Win32 Worker 均在程序集级标记为 .NET 预览功能。桌面可执行项目显式设置了 `EnablePreviewFeatures=true`，以确认使用该依赖；这不是对 CA2252 的压制。后续升级 NAPS2 时应重新确认其 API 稳定性与兼容性。

## 恢复与构建

在仓库根目录运行以下 PowerShell 命令：

```powershell
dotnet restore .\apps\desktop\Omrina.Desktop.csproj --configfile .tools\NuGet.Config --packages .tools\nuget-packages
dotnet build .\apps\desktop\Omrina.Desktop.csproj -f net10.0-windows10.0.26100.0 --no-restore
dotnet build .\apps\desktop\Omrina.Desktop.csproj -f net10.0-desktop --no-restore
```

当前开发环境已安装 .NET SDK 10.0.401，因此优先使用系统 `dotnet`。若后续在仓库内准备了私有 SDK，可将上述命令中的 `dotnet` 替换为 `.tools\dotnet\dotnet.exe`。

当前项目设置为 `WindowsPackageType=None`，便于先验证 Windows UI 与设备驱动；Windows 部署输出会包含 Windows App SDK runtime payload。正式安装包方式将在后续阶段单独确定。`net10.0-desktop` 的入口位于 `Platforms/Desktop/Program.cs`，使用 Uno 的 X11、Linux framebuffer、macOS 和 Win32 host 适配器。

项目设置 `WindowsAppSDKSelfContained=true`，使 Windows App SDK runtime payload 随部署输出提供，避免目标机缺少 Windows App SDK runtime 时启动失败（例如 COM `0x80040154`）。这不是整个 .NET 应用的 self-contained 发布；目标环境仍需要兼容的 .NET runtime。该设置不需要新增 NuGet 包。

## M1 模板预览与 SVG 导出

单窗口导航中的“模板”页支持标题、题数和每题选项数输入，使用 `Omrina.Core` 的同一份毫米几何生成 A4 预览；标题最多 24 个 Unicode 字符，题数为 1–44，每题选项数为 2–6。参数修改后当前预览会失效，重新生成后才可保存、打印或导入图像。主窗口同时提供“状态”“模板”“采集”“设置”“关于”页面，并缓存模板页和采集页实例。

Core schema 1 会把标题规范化为 NFC，并为规范化标题、题数和选项数生成完整小写 SHA-256 `TemplateId`。SVG metadata 会记录 schema、`TemplateId`、题数和选项数；页面顶部还会绘制独立的方向标记，并打印形如 `AS1-20x4-XXXXXXXX` 的短编号。

“保存 SVG”使用 `FileSavePicker` 选择目标文件，并通过 `FileIO.WriteTextAsync` 写入 Core 导出的 SVG。预览缩放只影响屏幕显示，导出的纸张尺寸仍为 210mm × 297mm，不依赖显示器 DPI。

2026-09-18 软件级验证使用默认 20 题、每题 4 个选项的模板：真实桌面预览显示短编号 `AS1-20x4-B7A4AFC5` 和顶部方向标记；取消保存时显示“已取消保存 SVG”，保存按钮随后恢复可用。XML 检查确认 A4 `210mm × 297mm`、80 个填涂圆圈和 1 个方向标记。

## M1 系统打印

Windows 目标的“系统打印”入口使用 Windows 系统打印流程。打印页面按 210mm × 297mm 的 A4 纵向尺寸创建，分页阶段会检查打印机报告的纸张尺寸和 `ImageableRect` 可打印区域；如果纸张不是 A4 纵向，或打印机边距会裁切模板内容，流程会报告错误并停止，不自动缩放模板。系统任务的提交、取消和失败状态会反馈到模板页。macOS/Linux 当前由明确的能力适配器报告系统打印未提供，模板仍可保存为 SVG，不会伪称提交成功。

代码实现已接入打印控制器。软件级验证实际选定 Microsoft Print to PDF，预览为 1 页，打印任务状态显示“系统已接收打印任务。打印是否完成请以系统状态为准。”，PDF 元数据确认 1 页 A4、未加密；渲染后的页面已目视确认定位块、顶部方向标记、编号、标题、列标和气泡清晰，无裁切、空白或重叠。该结果验证软件输出，不代表实体打印比例测量。

## M1 图像导入与扫描采集

“采集”页的“导入 PNG/JPEG”只读取用户在文件选择器中明确选择的文件。`CaptureStore` 会校验扩展名、实际 PNG/JPEG 编码、尺寸和文件大小，然后在应用本地数据目录下为每页建立独立记录。文件限制为 100 MB、宽高各不超过 16000、总像素不超过 100M；Skia 先读 codec 头部，再完整遍历 JPEG scanline，PNG scanline 不可用时执行受同样上限约束的 exact-size 解码。记录包含原始图像文件和 `manifest.json`，其中保存来源（导入或扫描）、模板 schema、`TemplateId`、规范化标题、题数、选项数、像素尺寸和文件大小。原图与 manifest 先写入仅由本次保存使用的暂存目录，完成后再移动到最终目录；复制后再次校验，取消、校验失败或写入失败会清理暂存记录。

“扫描并保存首张”在 Windows 使用 WIA/TWAIN，在 macOS 使用 Apple/ImageCaptureCore（ICA），在 Linux 使用 SANE；固定使用平板、A4、非原生驱动界面，并提供 150、300、600 DPI 三个分辨率。扫描得到的首张图像先保存为应用拥有的临时 PNG，再通过同一份 `CaptureStore` 关联模板并保存；取消或失败不会自动重发扫描。macOS/Linux 设备运行尚未验收。

迁移前的软件级验证从已授权测试纸导入 PNG，界面显示 2481 × 3506，并成功关联模板身份；本轮没有执行实体扫描或打印。纸面实际尺寸尚未测量，M2 自动识别尚未实现。

上述段落描述当前代码路径。Windows 与 `net10.0-desktop` 代码级构建已通过；macOS/Linux 实机运行、设备发现、文件选择器和扫描仍未验收，不要把这些代码路径写成已通过的设备或 UI 验收。

## 2026-09-19 Windows 单窗口导航 UI 验收

在已完成 Windows Desktop 构建的 `Omrina.Desktop.exe` 上，使用现有 synthetic 图像素材完成了单窗口 UI 验收。截图和 SVG 证据保存在相对路径 `artifacts/ui-acceptance-20260919/`，未上传截图或原图。

- 启动后仅有一个主窗口，标题为 `OMRINA 本地服务`；自定义 TitleBar、NavigationView 和页面内容属于同一窗口。“状态”“模板”“采集”“设置”“关于”均可切换，TitleBar 页面标题同步，模板页和采集页状态可缓存。
- 模板页修改标题后，保存 SVG、系统打印和导入图像操作均禁用；重新生成预览后恢复。离开模板页再返回，标题“验收模板”和模板 ID 仍保留。
- 从模板页进入“导入图像”会回到同一主窗口的采集页，并带入模板参数和模板 ID。
- SVG 文件保存成功，生成的 `template-acceptance.svg` 可读取且包含 A4 SVG 根元素；再次打开保存对话框后取消，界面显示“已取消保存 SVG”。
- 采集页取消文件选择后显示“已取消图像导入，未留下采集记录”；选择 synthetic 3×2 PNG 后显示导入成功、像素尺寸 `3×2` 和模板关联。
- Compact 与展开的 NavigationView 均可操作；TitleBar 最大化、恢复、最小化、恢复均可操作。本轮未形成窄窗口实际尺寸的重排证据，也没有执行实体扫描或打印。

macOS/Linux 未运行应用或设备后端，三平台 CI 和设备测试继续暂缓。

## 本地健康检查

启动应用后，本地服务只在 `127.0.0.1:17843` 监听，且只提供 `GET /health`：

```json
{"service":"omrina-local","protocolVersion":1,"status":"ready"}
```

可在另一个 PowerShell 窗口运行以下命令验证；启动应用使用系统 `dotnet`：

```powershell
dotnet run --project .\apps\desktop\Omrina.Desktop.csproj -p:Platform=x64
Invoke-RestMethod http://127.0.0.1:17843/health
```

此端点只用于本机诊断，不是配对或授权接口，也不提供设备、扫描、文件或 WebSocket API。没有 `Origin` 请求头的本机诊断请求可以访问。若浏览器需要读取响应，请在启动应用的进程环境中设置以逗号分隔的精确 HTTP/HTTPS 来源，例如：

```powershell
$env:OMRINA_ALLOWED_ORIGINS = 'https://app.example.com,http://localhost:3000'
dotnet run --project .\apps\desktop\Omrina.Desktop.csproj -p:Platform=x64
```

`OMRINA_ALLOWED_ORIGINS` 是当前配置项名称；主机仍兼容旧的 `ANSWERSHEET_ALLOWED_ORIGINS` 环境变量。该配置不改变 OMRINA 的品牌或 `Omrina.*` 代码命名，只控制来源校验，不授予设备访问权限。

未配置来源时，所有带 `Origin` 的请求都会被拒绝。配置项中每个值必须是没有路径、查询、片段或用户信息的完整 HTTP/HTTPS Origin；无效配置会让本地服务启动失败，并在界面中显示错误。服务不会使用通配符 CORS、HTTPS 证书或遥测。

## 测试扫描

旧版 MainWindow“扫描测试纸”入口已退役。采集页统一提供扫描入口，以平板来源、A4、300 DPI 和非原生驱动界面扫描，并把返回的首张图像交给 `CaptureStore`，保留原图并写入模板关联 manifest。界面会显示保存结果，但文档不记录本机用户路径。

该操作仅用于已明确放置且授权读取的测试纸。它不提供批量扫描、自动进纸、设备 HTTP API 或网页控制接口。

## 当前验证状态

迁移前 Windows 原型已有 Core/Capture 回归、模板 UI、SVG 文件、PNG 导入关联、Print to PDF 软件路径以及一轮生成模板的打印/填涂/本地扫描记录；这些记录不等同于本轮跨平台实机验收。当前 Windows 与 Skia desktop 构建已通过，CaptureStore 回归已通过；macOS/Linux 实机、设备发现和三平台测试继续暂缓，纸面实际尺寸尚未测量，M2 自动识别尚未实现。

2026-09-19 Uno 迁移代码级验证：

- 使用官方 NuGet 源和工作区 `.tools/NuGet.Config` 恢复 Uno.Sdk 6.7.30、NAPS2 1.3.0 和 Skia 运行依赖成功。
- `net10.0-desktop` 构建 0 个错误；输出有 2 个 NU1900（NuGet 漏洞审计无法访问 nuget.org）。Windows `net10.0-windows10.0.26100.0` 使用隔离输出目录构建 0 个错误，同样只有 2 个 NU1900。Windows 自包含 runtime 配置保留。
- CaptureStore 测试构建 0 个错误（2 个 NU1900 仅为漏洞审计网络失败）；运行 6 组回归全部通过，覆盖 Core 模板 ID、3×2 PNG/JPEG 完整解码和原始字节复制、scan 来源、非法/不支持扩展、截断 PNG/JPEG、16001×1 尺寸超限错误码以及取消 staging 清理。
- Windows 文件 picker 使用 HWND 初始化；Skia desktop 使用 Uno `FileOpenPicker` / `FileSavePicker`，不依赖 Windows HWND。Windows 打印使用系统打印适配器；macOS/Linux 明确返回未提供系统打印适配器并保留 SVG 保存。
- 没有执行物理扫描或打印；没有在 macOS/Linux 上运行应用或连接 ICA/SANE 设备，也没有新增三平台 CI。

2026-09-19 使用正常标准入口启动成功，窗口标题为 `OMRINA 本地服务 - M0`；`127.0.0.1:17843/health` 返回 `omrina-local`、协议版本 1、`ready`，健康集成检查通过。x64 构建为 0 个警告、0 个错误。该次启动与 health 复核本身未进行 UI 视觉/交互、实体扫描或打印验收；单窗口 UI 验收见上节。

## 资料

- [WinUI 3 官方模板与目标框架](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md)
- [Windows App SDK 2.4.0 稳定版本](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [NAPS2 SDK 使用与 x86 TWAIN Worker](https://www.naps2.com/sdk/doc/api/)
- [NAPS2 SDK 许可证说明](https://github.com/cyanfish/naps2#license)
