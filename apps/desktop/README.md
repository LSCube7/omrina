# Desktop M0 原型

此目录是答题纸本地应用的最小 WinUI 3 原型，提供设备枚举、本地健康检查和显式操作的单页测试扫描。尚未实现完整阅卷功能。

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
dotnet restore .\apps\desktop\AnswerSheet.Desktop.csproj
dotnet build .\apps\desktop\AnswerSheet.Desktop.csproj --no-restore
```

当前开发环境已安装 .NET SDK 10.0.401，因此优先使用系统 `dotnet`。若后续在仓库内准备了私有 SDK，可将上述命令中的 `dotnet` 替换为 `.tools\dotnet\dotnet.exe`。

当前项目设置为 `WindowsPackageType=None`，便于 M0 先验证 WinUI 与设备驱动。它运行时需要 Windows App Runtime；正式安装包方式将在后续阶段单独确定。

## M1 模板预览与 SVG 导出

主窗口的“新建答题纸模板”入口会打开模板窗口。窗口支持标题、题数和每题选项数输入，使用 `AnswerSheet.Core` 的同一份毫米几何生成 A4 预览；标题最多 24 个 Unicode 字符，题数为 1–44，每题选项数为 2–6。参数修改后当前预览会失效，重新生成后才可保存。

“保存 SVG”使用 `FileSavePicker` 选择目标文件，并通过 `FileIO.WriteTextAsync` 写入 Core 导出的 SVG。预览缩放只影响屏幕显示，导出的纸张尺寸仍为 210mm × 297mm，不依赖显示器 DPI。

当前验证已确认默认 20 题、每题 4 个选项的预览、参数变化后的保存按钮状态、重新生成后的更新状态和文件保存对话框可以打开；Core 与 Desktop x64 `--no-restore` 构建均为 0 警告、0 错误。真实写入和取消保存尚未完成，打印、图像导入和采集闭环也尚未实现。

## 本地健康检查

启动应用后，本地服务只在 `127.0.0.1:17843` 监听，且只提供 `GET /health`：

```json
{"service":"answersheet-local","protocolVersion":1,"status":"ready"}
```

可在另一个 PowerShell 窗口运行以下命令验证；启动应用使用系统 `dotnet`：

```powershell
dotnet run --project .\apps\desktop\AnswerSheet.Desktop.csproj -p:Platform=x64
Invoke-RestMethod http://127.0.0.1:17843/health
```

此端点只用于本机诊断，不是配对或授权接口，也不提供设备、扫描、文件或 WebSocket API。没有 `Origin` 请求头的本机诊断请求可以访问。若浏览器需要读取响应，请在启动应用的进程环境中设置以逗号分隔的精确 HTTP/HTTPS 来源，例如：

```powershell
$env:ANSWERSHEET_ALLOWED_ORIGINS = 'https://app.example.com,http://localhost:3000'
dotnet run --project .\apps\desktop\AnswerSheet.Desktop.csproj -p:Platform=x64
```

未配置来源时，所有带 `Origin` 的请求都会被拒绝。配置项中每个值必须是没有路径、查询、片段或用户信息的完整 HTTP/HTTPS Origin；无效配置会让本地服务启动失败，并在界面中显示错误。服务不会使用通配符 CORS、HTTPS 证书或遥测。

## 测试扫描

在设备列表中选择一个已发现的设备后，`扫描测试纸` 会以平板来源、A4、300 DPI 和非原生驱动界面扫描，并且只保存返回的首张图像为 PNG。开发时若从仓库根目录启动，文件会写入 `artifacts/scans`；若未能找到仓库根目录，则写入应用目录下的 `test-scans`。界面会显示保存后的绝对路径。

该操作仅用于已明确放置且授权读取的测试纸。它不提供批量扫描、自动进纸、设备 HTTP API 或网页控制接口。

## 当前验证状态

项目文件、应用清单和两个 XAML 文件已通过 XML 解析。NuGet 恢复、编译和真实设备枚举的结果以仓库根目录 `docs/development/validation.md` 为准；XML 解析不代表项目可以编译。

## 资料

- [WinUI 3 官方模板与目标框架](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/Templates/Dotnet/README.md)
- [Windows App SDK 2.4.0 稳定版本](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads)
- [NAPS2 SDK 使用与 x86 TWAIN Worker](https://www.naps2.com/sdk/doc/api/)
- [NAPS2 SDK 许可证说明](https://github.com/cyanfish/naps2#license)
