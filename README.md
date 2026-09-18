# AnswerSheet

Windows 本地答题纸工具与无 UI TypeScript SDK。M0 本地扫描原型和 M1 模板、打印、图像采集原型已建立，完整阅卷产品以及 M1 的实物闭环验收仍未完成。

## 架构

- 本地 UI：WinUI 3 / Windows App SDK / C#。
- 扫描：NAPS2 SDK，支持在本地 WinUI 流程中枚举 WIA/TWAIN 设备，并以平板、A4、150/300/600 DPI 采集首张图像。
- SDK：TypeScript，供第三方应用调用本地功能，不绑定前端框架。
- 模板排版、识别、评分、复核与存储由本地引擎执行。

## 开发约定

SDK、依赖与构建输出不提交。开发工具下载到 `.tools`；首次构建需要已授权的 NuGet 网络访问。

SDK 源码位于 `packages/sdk`。在仓库根目录运行 `node --test tests/sdk/*.test.mjs` 可执行无依赖客户端测试（当前使用 Node.js 25）。桌面构建说明见 `apps/desktop/README.md`，详细验证记录见 `docs/development/validation.md`。

桌面应用已启动并监听 `http://127.0.0.1:17843` 时，可手动运行以下真实集成检查：

```powershell
node .\tests\integration\health.mjs
```

该脚本不会启动应用、扫描端口或访问扫描设备。它会通过 SDK 检查固定的 `/health` 响应，并确认任意外部 `Origin` 被拒绝、未知路径返回 404、`POST /health` 返回 405。请在未配置 `ANSWERSHEET_ALLOWED_ORIGINS` 的默认服务实例上运行；若该变量显式允许 `https://answersheet-integration.invalid`，外部来源检查会按配置失败。它不验证浏览器 CORS / Local Network Access 行为；该部分需要在真实浏览器中单独联调。

本阶段验证界面启动、设备枚举、连接协议与单页测试扫描。实际扫描必须使用已明确放置的测试纸，避免采集设备上的未知内容。尚未提供生产配对与授权实现，不能将健康检查原型作为开放设备访问接口使用。

## M1 当前范围

`src/AnswerSheet.Core` 负责单页 A4 的毫米几何和 SVG 输出。模板 schema 版本为 1；标题先规范化为 NFC，再由规范化标题、题数和选项数生成完整小写 SHA-256 `TemplateId`。页面同时打印短编号，并在顶部放置独立的方向标记，便于后续模板匹配和方向判断。Core 回归程序当前包含 9 项检查，覆盖容量、边界、确定性、SVG 安全和模板身份。

WinUI 已接入模板预览、系统打印和图像采集原型。打印流程固定以 A4 纵向的实际尺寸输出，并在系统打印回调中检查页面尺寸和可打印区域，不自动把模板缩放到其他纸张或被打印机边距裁切。采集流程支持用户选择 PNG/JPEG 导入，也支持 WIA/TWAIN 平板首张扫描；图像会先校验，再以原图和 manifest 一起保存，manifest 记录来源和模板身份。

2026-09-18 的软件级验证已覆盖模板预览、SVG 保存与取消、PNG 导入关联和 Print to PDF 输出；CaptureStore 的 5 项回归检查也已通过。验证得到的模板编号为 `AS1-20x4-B7A4AFC5`，完整 `TemplateId` 为 `b7a4afc51ac55b215158c8b31e1f49dc993786cafaad67e88fa66ce7d0394cf9`。这仍不包含实体打印比例测量、填涂后再扫描闭环，因此不能据此声称 M1 全部验收。

## 后续里程碑

M1 模板核心位于 `src/AnswerSheet.Core`，无第三方包依赖。运行 `dotnet run --project tests/core/AnswerSheet.Core.Tests.csproj --no-restore -- --write-example` 可验证几何和 SVG 导出并生成本地示例；首次运行需恢复该测试项目。实施范围与验收条件见 [M1 计划](docs/architecture/m1.md)。M0 的 HTTPS、WebSocket 和配对授权验证仍未完成，不能以本地健康检查替代。

1. M0：WinUI 3、NAPS2 和浏览器本地连接验证。
2. M1：模板生成、打印和图像采集；实体打印比例及填涂再扫描闭环待验收。
3. M2：识别、评分、人工复核与导出。
4. M3：完整 SDK 接入。
5. M4：安装分发、兼容性和使用文档。
