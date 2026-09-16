# M0 验证记录

日期：2026-09-16。

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

## 尚未验证

- 取消中的驱动行为、多页和不同设备兼容性。
- HTTPS 浏览器到真实本地服务的 HTTP / WebSocket 连接。
- 配对授权与业务接口（尚未实现）。

SDK 单元测试使用模拟响应，只证明客户端行为，不证明上述集成路径。

## 已执行静态检查

桌面项目 `.csproj`、应用清单和两个 XAML 文件已通过 XML 解析。该检查只证明 XML 格式有效，不能替代 XAML 类型检查、C# 编译或界面运行。

## SDK 自动测试

执行 `node --test tests/sdk/*.test.mjs`，7 项通过。覆盖回环端点限制、固定健康检查路径、重定向与凭据策略、请求超时、读取响应体超时、调用方取消、HTTP / 协议错误及定时器上限。

Node.js 直接擦除 TypeScript 类型运行测试；尚未执行 TypeScript 编译器类型检查，也尚未构建可发布的 npm 产物。
