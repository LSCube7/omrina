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

## 主观题模板与人工批阅

主观题区域在生成答题纸时定义。`createTemplateTask()` 的 `subjectiveRegions` 接收题号、满分和纸面毫米矩形 `rectangleMm`；题目 ID 由模板定义稳定生成。预览、打印和采集关联使用同一模板。批阅时依据采集保存的模板和页面定位提取题图，不再提交原图像素区域。

- `createSubjectiveReviewTask()` 创建批阅文档，参数只有 `captureId`；题目、分值和区域取自该采集的模板。缺少主观题模板或定位失败会明确拒绝。
- `createSubjectiveReadTask()` 读取批阅文档及当前版本。
- `createSubjectiveGradeTask()` 提交草稿、确认或重置；包含 `reviewId`、`expectedVersion`、`reviewer` 和 `edits`，整批成功或整批失败。
- `createSubjectiveExportTask()` 导出 JSON 或 CSV。
- `getSubjectiveQuestionImage(reviewId, questionId)` 返回 PNG 字节；只读取已保存的题目区域，不开放任意路径或裁剪参数。

先保存草稿，再确认相同的分数和评语。修改已确认题目时，显式提交 `status: "draft"`；`status: "ungraded"` 重置评分。未全部确认时，最终主观题小计为 `null`。任务结果沿用 `TaskSnapshot.result` 的 `unknown` 边界，由宿主按具体操作核对。

创建任务仍需提供幂等键。版本冲突后先重新读取并核对，SDK 不自动覆盖。批阅记录保存于本机，但新授权不会继承旧授权的记录。尚未发布的旧像素题区创建参数已移除；已有本地评分记录仍可重新打开。后续纸面样式、多页、真实纸张与界面验收及 OCR/AI 接入仍待推进，详细限制见 [M5 架构](../../docs/architecture/m5.md)。

模板区域参数示例（选择题 4 道、主观题第 5 题）：

```ts
const task = await client.createTemplateTask({
  idempotencyKey: "mixed-template-1",
  parameters: {
    title: "练习答题纸",
    questionCount: 4,
    optionsPerQuestion: 4,
    subjectiveRegions: [{
      questionNumber: 5,
      maxScore: 10,
      rectangleMm: { x: 14, y: 100, width: 180, height: 50 },
    }],
  },
});
// 上传或扫描该模板后，使用任务返回的 captureId 创建批阅记录。
```

### 自动回环验证

自动回环验证使用已编译的实际 Desktop 测试适配器：

```powershell
dotnet build .\tests\m3-desktop\Omrina.M3.Desktop.Tests.csproj --no-restore
node .\tests\integration\m5-sdk-e2e.mjs .\tests\m3-desktop\bin\Debug\net10.0\Omrina.M3.Desktop.Tests.dll
```

先按本文开头构建 SDK。该脚本启动专用测试 harness，自动批准本轮临时授权，使用其生成的合成 PNG，并在结束时撤销授权及退出子进程。不启动桌面界面或扫描设备，不代表浏览器权限和真实纸张验收。

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

## 学校答题纸（schema 3）

`createSchoolTemplateTask()` 复用 `template` 操作，传入 `{schoolDefinition}`，不能混入旧模板的 `title/questionCount/subjectiveRegions` 参数。学校枚举使用以下大小写：`A4Portrait/A3Landscape`、`AnswerOnly/WithQuestions`、`Choice/Subjective`、`Circle/Rectangle`、`Inside/Outside`、`Barcode/Marking`。

```ts
const task = await client.createSchoolTemplateTask({
  idempotencyKey: crypto.randomUUID(),
  parameters: {
    schoolDefinition: {
      examId: "math-2026", layoutDocumentId: "math-layout", version: 1,
      title: "数学测验", paper: "A3Landscape", columns: 3,
      mode: "AnswerOnly", bubbleShape: "Rectangle", labelPlacement: "Inside",
      bubbleWidthMm: 2, bubbleHeightMm: 1.8,
      candidateIdentity: { mode: "Barcode", digits: 8 },
      questions: [
        { number: 1, type: "Choice", maximumScore: 2, options: ["甲", "乙", "丙", "丁"] },
        { number: 2, type: "Subjective", maximumScore: 10, subjectiveHeightMm: 40 },
      ],
    },
  },
});
```

成功任务返回 `SchoolTemplateDocument`：`{documentId,examId,version,pages}`。每页包含 `templateId/schemaVersion/paper/side/pageIndex/widthMm/heightMm/schoolMetadata/svg` 及原有模板摘要字段；`pageIndex` 从 0 开始，`schoolMetadata.pageNumber` 从 1 开始，正反面为 `Front/Back`。使用对应页的 `templateId` 上传或扫描；每页资源仍只属于生成它的授权。`TaskSnapshot.result` 保持 `unknown`，导出的类型用于调用方检查结果后使用。

定义必须包含 `examId/layoutDocumentId/questions`。未提供的字段采用 Core 默认值；A3 必须显式指定两栏或三栏。题号为唯一正整数；选择题有 2–6 个选项；题目 1–500 道，最多 64 页；圆框直径/矩形宽度大于 0 且 ≤2 mm，矩形高度 ≤4 mm；主观题高度为 10–230 mm，仍须能放进所选栏。题目正文最多 8000 字符，定义序列化后最多 60000 UTF-8 字节，HTTP 总请求最多 64 KiB。SDK 与服务端拒绝未知字段、路径字段和重复题号；服务端也拒绝重复 JSON 属性。

学校页上传/扫描会校验四角、方向及考试机器码的考试、版本、页码和正反面；无法确认时任务返回 `CAPTURE_SCHOOL_PAGE_MISMATCH`，不保存该页面。采集结果和识别结果外层提供 `schoolMetadata/candidateId/identityStatus`：可靠读出考号时 `Identified`，空白、多涂、损坏条码或无反面身份区时 `RequireAssociation` 且 `candidateId:null`。考试码不能替代考号；本轮不会根据页序自动配对学生。

`answerKey` 可以传本页全部选择题或整个考试全部选择题，题号沿用全局题号；学校题目分值由定义决定。页面选择题评分不能作为整份多页混合答卷的最终总分，页面完整性、身份与主观题确认仍需桌面流程处理。

扫描沿用 `{templateId,deviceId,dpi}`，纸张从模板获得。当前适配器只请求平板扫描：A4 纵向或 A3 横向，不拉伸/裁切为请求尺寸，不回退 A4；设备能力未经过验证时不会声明支持 A3。实际扫描须设备和驱动支持，请用实纸验收；导入不需要扫描设备。

离线学校真实 HTTP SDK 回归：先离线构建 SDK 和 `tests/m3-desktop`，再运行 `node tests/integration/school-sdk-e2e.mjs`。脚本启动临时 loopback 测试 host，使用合成纸验证生成、采集、考号、识别、非连续题号复核、错考试拒绝和授权隔离；不会扫描真实设备。测试 host 的批准入口仅在测试进程标准输入，生产 API 不增加自动批准。

学校模板文档的完整任务结果（包括所有页 SVG）沿用 1 MiB UTF-8 JSON 上限。服务端在注册任何页面模板之前检查该大小；超限返回 `TEMPLATE_DOCUMENT_TOO_LARGE`，当前授权不会遗留该次生成的页模板。最多 64 页是布局上限，不保证包含预览的结果能进入 1 MiB；较长文档需减少页数或正文后重新生成。

学校扫描结果包含与上传一致的 `schoolMetadata/candidateId/identityStatus`，并保留 `dpi/pageSize/flatbed` 扫描信息；SDK 导出 `SchoolScanCaptureSummary` 表达完整返回类型。
