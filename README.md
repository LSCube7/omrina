# AnswerSheet

Windows 本地答题纸工具与无 UI TypeScript SDK。当前处于 M0 技术验证阶段，尚未实现完整阅卷产品。

## 架构

- 本地 UI：WinUI 3 / Windows App SDK / C#。
- 扫描：NAPS2 SDK，评估 TWAIN、WIA 和辅助进程兼容性。
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

## 后续里程碑

1. M0：WinUI 3、NAPS2 和浏览器本地连接验证。
2. M1：模板生成、打印和图像采集。
3. M2：识别、评分、人工复核与导出。
4. M3：完整 SDK 接入。
5. M4：安装分发、兼容性和使用文档。
