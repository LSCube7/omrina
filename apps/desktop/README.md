# OMRINA Desktop M0 原型

此目录是 OMRINA 本地 Integration Agent 的 Windows 桌面原型，当前以 WinUI 3 提供设备枚举、本地健康检查和显式操作的单页测试扫描。跨平台 UI 最终选择 Uno Platform，但当前尚未引入 Uno；这里也没有实现完整阅卷功能。

## 固定依赖

- .NET SDK 10
- `Microsoft.WindowsAppSDK` 2.4.0
- `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705
- `Microsoft.Windows.SDK.BuildTools.WinApp` 0.6.1
- `NAPS2.Sdk`、`NAPS2.Images.Gdi`、`NAPS2.Sdk.Worker.Win32` 1.3.0

`NAPS2.Sdk.Worker.Win32` 允许 64 位主程序通过 NAPS2 的 x86 Worker 枚举和使用常见的 32 位 TWAIN 驱动。M0 使用它验证设备枚举与单页扫描。

NAPS2 1.3.0 的 SDK、GDI 图像组件和 Win32 Worker 均在程序集级标记为 .NET 预览功能。桌面可执行项目显式设置了 `EnablePreviewFeatures=true`，以确认使用该依赖；这不是对 CA2252 的压制。后续升级 NAPS2 时应重新确认其 API 稳定性与兼容性。

## 恢复与构建

在仓库根目录运行以下 PowerShell 命令：

```powershell
dotnet restore .\apps\desktop\Omrina.Desktop.csproj
dotnet build .\apps\desktop\Omrina.Desktop.csproj --no-restore
```

当前开发环境已安装 .NET SDK 10.0.401，因此优先使用系统 `dotnet`。若后续在仓库内准备了私有 SDK，可将上述命令中的 `dotnet` 替换为 `.tools\dotnet\dotnet.exe`。

当前项目设置为 `WindowsPackageType=None`，便于 M0 先验证 Windows UI 与设备驱动；部署输出会包含 Windows App SDK runtime payload。正式安装包方式将在后续阶段单独确定。

项目设置 `WindowsAppSDKSelfContained=true`，使 Windows App SDK runtime payload 随部署输出提供，避免目标机缺少 Windows App SDK runtime 时启动失败（例如 COM `0x80040154`）。这不是整个 .NET 应用的 self-contained 发布；目标环境仍需要兼容的 .NET runtime。该设置不需要新增 NuGet 包。

## M1 模板预览与 SVG 导出

主窗口的“新建答题纸模板”入口会打开模板窗口。窗口支持标题、题数和每题选项数输入，使用 `Omrina.Core` 的同一份毫米几何生成 A4 预览；标题最多 24 个 Unicode 字符，题数为 1–44，每题选项数为 2–6。参数修改后当前预览会失效，重新生成后才可保存、打印或导入图像。

Core schema 1 会把标题规范化为 NFC，并为规范化标题、题数和选项数生成完整小写 SHA-256 `TemplateId`。SVG metadata 会记录 schema、`TemplateId`、题数和选项数；页面顶部还会绘制独立的方向标记，并打印形如 `AS1-20x4-XXXXXXXX` 的短编号。

“保存 SVG”使用 `FileSavePicker` 选择目标文件，并通过 `FileIO.WriteTextAsync` 写入 Core 导出的 SVG。预览缩放只影响屏幕显示，导出的纸张尺寸仍为 210mm × 297mm，不依赖显示器 DPI。

2026-09-18 软件级验证使用默认 20 题、每题 4 个选项的模板：真实桌面预览显示短编号 `AS1-20x4-B7A4AFC5` 和顶部方向标记；取消保存时显示“已取消保存 SVG”，保存按钮随后恢复可用。XML 检查确认 A4 `210mm × 297mm`、80 个填涂圆圈和 1 个方向标记。

## M1 系统打印

模板窗口的“打印”入口使用 Windows 系统打印流程。打印页面按 210mm × 297mm 的 A4 纵向尺寸创建，分页阶段会检查打印机报告的纸张尺寸和 `ImageableRect` 可打印区域；如果纸张不是 A4 纵向，或打印机边距会裁切模板内容，流程会报告错误并停止，不自动缩放模板。系统任务的提交、取消和失败状态会反馈到窗口。

代码实现已接入打印控制器。软件级验证实际选定 Microsoft Print to PDF，预览为 1 页，打印任务状态显示“系统已接收打印任务。打印是否完成请以系统状态为准。”，PDF 元数据确认 1 页 A4、未加密；渲染后的页面已目视确认定位块、顶部方向标记、编号、标题、列标和气泡清晰，无裁切、空白或重叠。该结果验证软件输出，不代表实体打印比例测量。

## M1 图像导入与扫描采集

“导入 PNG/JPEG”只读取用户在文件选择器中明确选择的文件。`CaptureStore` 会校验扩展名、实际 PNG/JPEG 编码、尺寸和文件大小，然后在应用本地数据目录下为每页建立独立记录。记录包含原始图像文件和 `manifest.json`，其中保存来源（导入或扫描）、模板 schema、`TemplateId`、规范化标题、题数、选项数、像素尺寸和文件大小。原图与 manifest 先写入仅由本次保存使用的暂存目录，完成后再移动到最终目录；取消、校验失败或写入失败会清理暂存记录。

“扫描并保存首张”支持 WIA 和 TWAIN 设备，固定使用平板、A4、非原生驱动界面，并提供 150、300、600 DPI 三个分辨率。扫描得到的首张图像先保存为应用拥有的临时 PNG，再通过同一份 `CaptureStore` 关联模板并保存；取消或失败不会自动重发扫描。

软件级验证从已授权测试纸导入 PNG，界面显示 2481 × 3506，并成功关联模板身份。随后已使用生成模板完成打印、填涂并通过本地应用扫描；目视确认页面四角定位与方向标记完整。纸面实际尺寸尚未测量，M2 自动识别尚未实现。

上述段落描述当前代码实现。CaptureStore 的 5 项回归检查已通过；新采集窗口的运行验证和不同分辨率/驱动的实际验证结果以 `docs/development/validation.md` 为准，不要把代码路径写成已通过的设备或 UI 验收。

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

主窗口中的旧版“扫描测试纸”入口仍用于 M0 的单页设备冒烟检查，以平板来源、A4、300 DPI 和非原生驱动界面扫描，并只保存返回的首张图像为 PNG。M1 的“扫描并保存首张”入口会把扫描结果交给 `CaptureStore`，因此还会保留原图并写入模板关联 manifest。界面会显示保存结果，但文档不记录本机用户路径。

该操作仅用于已明确放置且授权读取的测试纸。它不提供批量扫描、自动进纸、设备 HTTP API 或网页控制接口。

## 当前验证状态

Core 9 项回归测试此前已通过；CaptureStore 回归程序已通过 5 项检查。模板 UI、SVG 文件、PNG 导入关联、Print to PDF 软件路径以及一轮生成模板的打印/填涂/本地扫描流程已完成验证；纸面实际尺寸尚未测量，M2 自动识别尚未实现。项目文件、应用清单和两个 XAML 文件已通过 XML 解析；NuGet 恢复、编译和真实设备枚举的结果以仓库根目录 `docs/development/validation.md` 为准，XML 解析不代表项目可以编译。

2026-09-19 使用正常标准入口启动成功，窗口标题为 `OMRINA 本地服务 - M0`；`127.0.0.1:17843/health` 返回 `omrina-local`、协议版本 1、`ready`，健康集成检查通过。x64 构建为 0 个警告、0 个错误。本轮没有新增 UI 视觉/交互、实体扫描或打印验收；启动和 health 结果只证明应用入口与本地诊断路径。

## 资料

- [WinUI 3 官方模板与目标框架](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md)
- [Windows App SDK 2.4.0 稳定版本](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [NAPS2 SDK 使用与 x86 TWAIN Worker](https://www.naps2.com/sdk/doc/api/)
- [NAPS2 SDK 许可证说明](https://github.com/cyanfish/naps2#license)
