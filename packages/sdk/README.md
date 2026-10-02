# OMRINA 本地服务 TypeScript SDK

这是 OMRINA M3 本地 Agent 的无运行时依赖客户端。它保留 M0 `checkHealth()`，并提供配对、模板与设备读取、图像上传、业务任务、任务恢复和 WebSocket 事件接口。

## 构建与类型检查

在 `packages/sdk` 目录运行：

```powershell
npm run typecheck
npm run build
```

构建会把 `src/` 编译为 `dist/`，同时生成 `.d.ts` 声明。包的 `exports` 指向 `dist/index.js` 与 `dist/index.d.ts`；`dist/` 不提交到 Git，但 npm 包清单包含这些构建文件。本阶段仅执行 `npm pack --dry-run` 检查包内容，尚未发布。TypeScript 仅作为开发依赖，SDK 没有运行时依赖。

在仓库根目录运行 SDK 单元测试：

```powershell
node --test tests/sdk/*.test.mjs
```

测试通过注入的 fetch 与 WebSocket，不会连接真实的本地 Agent。

## 已打包消费者验收

在仓库根目录运行：

```powershell
node .\tests\sdk-package\consumer-smoke.mjs
```

该 smoke 使用已安装的 TypeScript 5.9.3 构建 SDK，以本地 `npm pack` tarball 建立临时消费者，不安装依赖也不访问网络。它检查包中只有 README、package manifest 和 `dist/` 文件；再通过实际包 `exports` 运行 `checkHealth()` 与 `OmrinaClient`，并使用严格 TypeScript 设置编译消费者源码，确认声明文件从解包后的包解析。唯一产物写入根目录忽略的 `artifacts/sdk-package-consumer-*` 临时目录。

## Desktop adapter 端到端验证

`tests/integration/m3-sdk-e2e.mjs` 可使用 SDK 构建产物联调 Desktop 的本地 HTTP harness。先启动 harness 并从其首行取得合成图片路径：

```powershell
dotnet run --no-build --project tests/m3-desktop/Omrina.M3.Desktop.Tests.csproj -- --http-harness
```

另开终端运行以下命令，并传入 harness 显示的 PNG/JPEG fixture 路径：

```powershell
node .\tests\integration\m3-sdk-e2e.mjs http://127.0.0.1:17845 "C:\path\to\synthetic-answer-sheet.png"
```

脚本将申请配对并显示 `requestId`；在 harness 控制台运行 `approve <requestId>`，再把它显示的一次性 code 输入脚本。它通过模板、上传、识别、评分、复核、导出和任务列表，并在结束时撤销本次授权。脚本只把文件名传给服务，不会上传本机路径或打印 token；PNG/JPEG 文件只用于本机 harness。测试来源固定为 `http://localhost:3000`。

## 基本用法

```ts
import { OmrinaClient } from "./dist/index.js";

const client = new OmrinaClient({ endpoint: "http://127.0.0.1:17843" });
const ticket = await client.requestPairing({ clientName: "OMRINA local client" });

// 桌面应用批准后显示一次性 code，由宿主界面安全地收集。
await client.exchangePairing({ requestId: ticket.requestId, code: pairingCode });

const templates = await client.getTemplates();
const devices = await client.getDevices();
const task = await client.createTemplateTask({
  idempotencyKey: crypto.randomUUID(),
  parameters: { title: "课堂测验", questionCount: 20, optionsPerQuestion: 4 },
});

const latest = await client.getTask(task.taskId);
await client.cancelTask(latest.taskId);
await client.revokeGrant();
```

`exchangePairing()` 返回授权信息并在当前实例内存保存令牌，供后续固定业务接口使用。SDK 不写入存储。宿主负责在内存中管理客户端生命周期；不要把令牌写入 URL、日志或持久化配置。

## 任务方法

- `uploadImage()` 提交 PNG/JPEG 原始字节，使用 `templateId` 与不含本地路径的 `fileName` 元数据。
- `createTemplateTask()`、`createScanTask()`、`createRecognitionTask()`、`createScoreTask()`、`createReviewTask()`、`createExportTask()` 对应服务端固定 operation；不开放通用 URL 或任意请求头。
- 所有创建任务的方法都要求调用方提供 `idempotencyKey`。SDK 遇到网络错误时不会自动重发有副作用的请求。
- `TaskSnapshot.result` 保持 `unknown`，因为它依 operation 变化；任务、错误、事件外层结构会逐字段校验。
- `getTemplates()` 返回 `TemplateSummary[]`，包含可用于预览或保存的 `svg` 字符串；`getDevices()` 返回 `DeviceDescriptor[]`。
- `watchEvents()` 使用固定 WebSocket 路径并首先发送内存令牌。连接恢复时只读取 `GET /v1/tasks`，不会重建任务。用 `AbortSignal` 停止订阅。

## Origin、网络与权限

端点只允许 HTTP `localhost` 或 `127.0.0.1`，SDK 将所有 HTTP 与 WebSocket 调用固定到协议路由。HTTP 请求不跟随重定向、不发送 cookies，且不允许调用方传任意 header/path。浏览器会自动发送当前页面的精确 `Origin`；SDK 不尝试设置浏览器禁止脚本修改的 `Origin` 请求头。非浏览器宿主应通过其受控 fetch 实现提供服务端要求的精确来源。

## 本地浏览器联调

先启动模拟业务的测试服务（不启动桌面 UI 或扫描设备），再在另一个终端启动页面：

```powershell
dotnet run --project .\tests\server\Omrina.Server.Tests.csproj --no-restore -- --browser-harness
node .\tests\integration\m3-static-server.mjs
```

页面服务仅监听 `127.0.0.1:17846`，打开 `http://127.0.0.1:17846/m3-browser.html`。页面默认连接 `http://127.0.0.1:17844` 测试 host，可修改端点。申请配对后，在测试服务终端输入 `pending`，核对来源和请求 ID，再输入 `approve <requestId>`，将显示的一次性码填入页面。页面不会将 token 放入 URL 或日志。生产应用的允许/拒绝操作在桌面设置页完成，不提供自动批准路由。

## SDK 到实际业务适配器的联调

先构建 SDK，再运行测试适配器服务：

```powershell
dotnet run --project .\tests\m3-desktop\Omrina.M3.Desktop.Tests.csproj --no-restore -- --http-harness
```

服务绑定 `127.0.0.1:17845`，输出本次临时合成 PNG 的路径。在另一终端使用该路径：

```powershell
node .\tests\integration\m3-sdk-e2e.mjs http://127.0.0.1:17845 "本次合成PNG路径"
```

脚本使用固定测试来源 `http://localhost:3000`。在服务终端核对 `pending` 后批准请求，把一次性码输入脚本，验证模板、上传、识别、评分、复核、导出和任务恢复，最后撤销测试授权。完成后在服务终端输入 `quit`。

本联调使用实际 Server、Desktop 业务适配器和 Core 评分/复核/导出；设备和成功识别输出使用模拟实现。它不验证真实答题纸识别准确率或浏览器权限。实际验证范围与失败记录见 [验证记录](../../docs/development/validation.md)。
