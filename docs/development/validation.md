# M0 / M1 验证记录

初始记录日期：2026-09-16；M1 软件验证更新：2026-09-18。

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
- 输出 PNG 为 2481 × 3506 像素，849,055 字节。已打开图像确认文字与页面可读。副本保存于 `artifacts/scans/test-scan-20260915-155124174.png`，被 Git 忽略。该样本是普通测试纸，不是 OMR 准确率样本。
- 使用 EPSON L4260 WIA 入口完成相同的平板 A4 / 300 DPI 单页采集，输出 PNG 为 2464 × 3504 像素，2,162,280 字节，副本保存于 `artifacts/scans/test-scan-20260915-155645953.png`。图像方向上下颠倒，已记录为后续采集方向处理问题；该样本同样不是 OMR 准确率样本。
- 应用清单已设置 `PerMonitorV2`。随后执行 x64 Debug `--no-restore` 构建，0 警告、0 错误。
- 使用实际 WinUI 窗口截图与可访问性树验证布局：约 1268 × 739 的窗口中操作按钮横排；约 594 × 739 的窄窗口中按钮纵向全宽且状态文字换行；约 594 × 390 的矮窗口出现垂直滚动，滚动后底部 InfoBar 完整可见。
- 上述尺寸来自当前工具窗口，不代表不同系统缩放比例、不同显示器或跨显示器切换的实测；验证期间没有更改系统缩放设置。
- 只读检查已确认 runtimeconfig 要求 `Microsoft.NETCore.App` 与 `Microsoft.AspNetCore.App` 10.0.0，系统已安装相应的 10.0.12 运行时；应用宿主、主 DLL、Windows App Runtime Bootstrap 与 NAPS2 Worker 均存在于 x64 输出目录。此前沙箱内 GUI 进程启动失败不能作为运行时缺失的证据。
- 2026-09-16 再次启动桌面应用并运行健康检查集成测试，固定响应、外部 Origin 拒绝、`Origin: null` 返回 403、未知路径 404 与 `POST /health` 返回 405 均通过。

## 2026-09-17 M1 模板 UI 最新验证

- `AnswerSheet.Desktop.csproj` 已引用无第三方包依赖的 `AnswerSheet.Core`。使用本地恢复结果执行 `dotnet build apps/desktop/AnswerSheet.Desktop.csproj --no-restore -p:Platform=x64`，Core 与 Desktop x64 Debug 均构建成功，0 个警告、0 个错误。
- 主代理前一轮在实际 WinUI 窗口确认：默认 20 题、每题 4 个选项的模板预览可生成；标题改为“M1 验证答题纸”后保存按钮禁用；重新生成后标题更新且保存按钮恢复可用；`FileSavePicker` 能实际打开。
- 保存实现已改为 `FileIO.WriteTextAsync`，并通过上述桌面编译；本轮真实写入和取消保存尚未完成。启动最新可执行文件后，Computer Use 在点击“新建答题纸模板”时返回 `coordinate input geometry is unavailable`，随后重新激活又检测到用户输入，因而没有继续点击、没有强杀进程，也没有宣称生成 UI 保存文件。
- M0 的 HTTPS / WebSocket 浏览器连接仍未完成；M1 的实体打印、比例测量和填涂后再扫描闭环仍未完成。M1 的打印控制器、图像导入和采集保存代码已加入，但其最新运行验证见下节，不能把代码实现写成完整验收。

## 2026-09-18 M1 代码实现同步与待确认验证

以下内容依据当前代码记录，和运行验证分开：

- `AnswerSheet.Core` 的模板 schema 为 1。标题先规范化为 NFC，再由规范化标题、题数和选项数生成 64 位小写 SHA-256 `TemplateId`；页面打印短编号，并加入独立的顶部方向标记。Core 回归程序当前为 9 项检查，先前运行结果为 9 项通过。
- `TemplatePrintController` 已接入 Windows 系统打印流程，按 A4 纵向实际尺寸生成打印页，并在分页阶段检查纸张尺寸和可打印区域；不自动缩放到其他纸张或超出打印机边距。
- `CaptureStore` 支持 PNG/JPEG 导入和扫描结果保存，保留原始图像并写入模板关联 manifest。原图和 manifest 先写入暂存目录，完成后移动到最终采集目录；取消、校验失败或写入失败会清理暂存记录。
- `CaptureWindow` 的扫描入口支持 WIA/TWAIN 平板、A4、150/300/600 DPI，并只接收首张图像，再交给 `CaptureStore` 保存。

CaptureStore 回归检查已确认如下：

- 最终测试项目只链接生产代码 `apps/desktop/CaptureStore.cs` 和 `AnswerSheet.Core`，并使用已缓存的 `Microsoft.Windows.SDK.NET.Ref` FrameworkReference；不再引用整个 WinUI Desktop 项目，避免测试进程触发 WinUI 初始化挂起。
- 在无新包下载的离线恢复后，构建结果为 0 个警告、0 个错误。可复现命令为：

  ```powershell
  dotnet restore .\tests\capture\AnswerSheet.Capture.Tests.csproj --ignore-failed-sources
  dotnet build .\tests\capture\AnswerSheet.Capture.Tests.csproj --no-restore -p:Platform=x64
  dotnet run --project .\tests\capture\AnswerSheet.Capture.Tests.csproj --no-build --no-restore -p:Platform=x64
  ```

- 运行结果为 `PASS: 5 capture regression tests.`。5 项覆盖模板 ID/schema 关联、PNG 原始字节与 manifest、`scan` 来源类型复用同一存储 API、非法图像/扩展名校验，以及取消后的 staging 清理。
- 这组回归检查证明 CaptureStore 的本地保存和校验行为，不等同于 WIA/TWAIN 实际设备扫描、采集窗口 UI 或打印流程验收。

## 2026-09-18 M1 WinUI、SVG、PNG 导入与 Print to PDF 软件验证

- 真实 WinUI 窗口使用默认 20 题、每题 4 个选项的模板，预览显示编号 `AS1-20x4-B7A4AFC5` 和顶部方向标记。
- 取消保存时界面显示“已取消保存 SVG”，保存按钮恢复可用。实际保存的 `artifacts/templates/m1-ui-save-20260917.svg` 大小为 28,762 字节；XML 检查确认 `width="210mm"`、`height="297mm"`、`viewBox="0 0 210 297"`、80 个填涂圆圈、1 个方向标记，以及完整模板 ID `b7a4afc51ac55b215158c8b31e1f49dc993786cafaad67e88fa66ce7d0394cf9`。
- PNG 导入使用已有授权测试纸，界面显示 2481 × 3506，并成功关联上述完整模板 ID。本次未执行物理扫描。
- 打印流程实际选定 Microsoft Print to PDF，预览为 1 页；打印后生成 `artifacts/templates/m1-print-20260918.pdf`，大小为 170,787 字节。界面状态为“系统已接收打印任务。打印是否完成请以系统状态为准。”，打印完成后生成、保存、打印和导入按钮均恢复可用。
- `pdfinfo` 确认该 PDF 的 Producer 为 Microsoft Print To PDF、1 页、595.276 × 841.89 pt（A4）、rotation 0、未加密。使用 `pdftoppm` 渲染的 `artifacts/templates/m1-print-20260918-page1.png` 已目视确认定位块、方向标记、编号、标题、列标和气泡清晰，无裁切、空白或重叠。

上述结果构成当前 M1 模板 UI、SVG 输出、PNG 导入关联和 Print to PDF 的软件级验收证据；不等同于实体打印比例、纸面填涂或再扫描识别闭环。

以下结果仍待确认，暂不记为通过：

- WIA/TWAIN 设备在新 M1 采集窗口中的实际扫描保存尚未执行；此前 M0 的旧版设备枚举和 300 DPI 单页扫描记录不等同于新 Capture 流程验收。
- M1 的实际打印、纸面距离测量、填涂后再扫描和模板识别闭环尚未执行。

## 尚未验证

- 取消中的驱动行为、多页和不同设备兼容性。
- HTTPS 浏览器到真实本地服务的 HTTP / WebSocket 连接。
- WIA/TWAIN 新采集窗口的实际设备运行，以及生成模板的实体打印、比例测量、填涂、再扫描闭环。
- 配对授权与业务接口（尚未实现）。

SDK 单元测试使用模拟响应，只证明客户端行为，不证明上述集成路径。

## 已执行静态检查

桌面项目 `.csproj`、应用清单和两个 XAML 文件已通过 XML 解析。该检查只证明 XML 格式有效，不能替代 XAML 类型检查、C# 编译或界面运行。

## SDK 自动测试

执行 `node --test tests/sdk/*.test.mjs`，7 项通过。覆盖回环端点限制、固定健康检查路径、重定向与凭据策略、请求超时、读取响应体超时、调用方取消、HTTP / 协议错误及定时器上限。

Node.js 直接擦除 TypeScript 类型运行测试；尚未执行 TypeScript 编译器类型检查，也尚未构建可发布的 npm 产物。
