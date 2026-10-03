# M3 本地服务契约

HTTP 基址 `http://127.0.0.1:17843`。JSON 属性 camelCase；枚举采用 camelCase 字符串，不接受整数。保留 `/health` 和既有 `LoopbackHealthServer` 类型。

所有业务接口要求精确 `Origin` + `Authorization: Bearer <token>`，凭据不能放 URL。Host 仅允许本监听端口的 `localhost` / `127.0.0.1`。配对可来自任意规范 HTTP/HTTPS Origin（拒绝 null、路径、尾随斜线、凭据、多值等）；必须先由桌面明确批准。health 带 Origin 时沿用环境 allowlist，不带 Origin 仍可读取。业务 preflight 仅允许已有有效 grant 的 Origin，配对 preflight 可来自合法 Origin。

## HTTP 路由

| 路由 | 输入 | 返回 |
| --- | --- | --- |
| GET /health | 无 | HealthResponse |
| POST /v1/pairing/requests | `{clientName}` | 202 `{requestId,expiresAt}` |
| POST /v1/pairing/exchange | `{requestId,code}` | `{grantId,token,expiresAt}` |
| DELETE /v1/grant | 当前 Bearer | 204，自撤销 |
| GET /v1/templates | 当前 Bearer | 静态默认模板数组（10题/4选项，含templateId/schemaVersion/templateNumber/svg），不含用户生成资源 |
| GET /v1/devices | 当前 Bearer | 硬件描述 JSON；deviceId 是当前进程会话中的匿名稳定标识，重启后需重新发现设备 |
| POST /v1/tasks | `{idempotencyKey,operation,parameters}` | 202 TaskSnapshot |
| POST /v1/tasks/upload | PNG/JPEG 原始 body | 202 TaskSnapshot |
| GET /v1/tasks | 当前 Bearer | 当前 grant 的 TaskSnapshot[] |
| GET /v1/tasks/{taskId} | 当前 Bearer | 当前 grant 的 TaskSnapshot，其他 grant ID 返回404 |
| POST /v1/tasks/{taskId}/cancel | 当前 Bearer | 当前 snapshot，独立幂等取消 |

上传要求 `Content-Type: image/png` 或 `image/jpeg`，`X-Idempotency-Key`，以及 `X-Omrina-Parameters` JSON header `{templateId,fileName}`（UTF8 最多4096字节；SDK应将非ASCII字符序列化为JSON的\\uXXXX转义以兼容浏览器header）。先创建模板再上传，templateId 必须属于当前 grant。

`TaskSnapshot` 为 `{taskId,operation,status,result,error,createdAt,updatedAt}`。status 为 `queued,running,completed,failed,cancelled`；error 为 `{code,message}`。result 为受控 JSON，不含本地路径。创建 idempotencyKey 按 grant 隔离，同 key 不同 operation/参数/上传字节返回409，完全相同则返回原任务而不重新运行。

## 业务参数

| operation | parameters | result |
| --- | --- | --- |
| template | `{title,questionCount,optionsPerQuestion}` | `{templateId,title,questionCount,optionsPerQuestion,schemaVersion,templateNumber,svg}`；svg 可保存/打印 |
| upload | header `{templateId,fileName}` + 图像流 | 受控 captureId 与采集信息 |
| scan | `{templateId,deviceId,dpi}` | 受控 captureId 与采集信息 |
| recognize | `{captureId}` | `{resultId,version,result}，result 为 ResultExporter JSON` |
| score | `{resultId,answerKey,pointsPerQuestion?}` | `{resultId,version,result}` |
| review | `{resultId,expectedVersion,reviewer,edits:[{questionNumber,answer|null,reason,timestampUtc?}],answerKey?}` | `{resultId,version,result}，含评分和复核历史` |
| export | `{resultId,format}` | `{format,content}` |

任务创建时 review.expectedVersion 必须为非负整数，具体版本冲突由 operations 报告。score成功修改标准答案/评分与review成功后都会递增version，调用方必须使用最近响应的version提交review。参数只接受资源 ID，禁止直接文件路径读取。recognition/score/review/export 可嵌 Core ResultExporter JSON。嵌入result对象及export.content中的Core枚举沿用ResultExporter已有PascalCase字符串；仅外层task/status协议枚举采用camelCase。

## WebSocket

`GET /v1/events` 升级后先发 `{type:"authenticate",token}`；5秒内成功收到 `{type:"authenticated",sequence,task:null}`，之后才发送 `{type:"task",sequence,task}`。HTTP 与 WS 的 Origin/凭据绑定相同，URL query 拒绝。每 grant sequence 单调递增，无历史重播。断线后 GET tasks 恢复状态，不重新创建或触发硬件任务。撤销/过期关闭连接，慢消费者队列溢出关闭连接并通过 GET 恢复。

认证失败、授权撤销或过期使用关闭码 `1008`；事件队列溢出或有效授权的连接容量已满使用 `1013`，授权本身仍有效。SDK 在已建立订阅的临时关闭后重连并查询任务恢复状态；`1008` 则清除内存凭据并停止订阅，需重新配对。

## Desktop 注入与本地授权

`ILocalAgentOperations.GetTemplatesAsync(CancellationToken)` / `GetDevicesAsync(CancellationToken)` 返回 `JsonElement`；前者只返回静态能力/默认模板，每grant预置相同默认ID。自定义模板必须通过template task生成，且upload/scan不能引用其他grant的自定义模板。`RunAsync(TaskOperationRequest request, Stream? image, CancellationToken)` 返回 `JsonElement`。

`TaskOperationRequest(string GrantId, TaskOperation Operation, JsonElement Parameters)`；Desktop 按 GrantId 隔离模板、采集与结果 ID。image 流由 Server 拥有，operations 必须在 RunAsync 返回前读取/复制，不能保留流引用。operations 必须遵守取消令牌，包含采集协调器调用；不自动重发扫描。可抛 `LocalOperationException(code,message)` 表示业务失败，message 必须为安全用户文本。其他异常只返回通用错误，不回显异常/路径。

`LoopbackHealthServer(ILocalAgentOperations? operations = null, IEnumerable<string>? allowedOrigins = null, int port = 17843, TimeProvider? timeProvider = null)`；时钟默认 `TimeProvider.System`，可注入框架时钟以测试过期行为。`PendingPairings`、`ActiveGrants` 为安全摘要快照；`ApprovePairing(requestId)` 返回一次性 code，仅在桌面本地显示；`RejectPairing(requestId)` / `RevokeGrant(grantId)` 返回 bool。`PairingStateChanged` / `GrantRevoked(string grantId)` 在调用/请求/清理线程发出，UI 自行 dispatcher，不阻塞回调。自然过期由每秒清理触发通知；待配对申请过期或错误次数耗尽后也会通知刷新。

凭据仅驻内存 SHA256 哈希，不日志、不存URL、不落盘。code一次性、5分钟过期、最多5次错误；grant 8小时过期。停止/重启清空授权和内存任务/资源索引；不删除 CaptureStore 文件或用户数据。

扫描设备 ID 由当前扫描会话的随机密钥对驱动与原生标识计算 HMAC，避免枚举顺序改变时选错设备，也不公开原生标识。原生身份缺失或同驱动内重复的候选全部排除；已移除设备的旧 ID 不会映射到另一台设备。会话重建后调用方需要重新读取设备列表。

请求 JSON 上限64KiB，上传20MiB，body读取30秒超时。最多8个同时读取body。每Origin配对请求/交换各10次/分钟，全局64个pending和64个grant；每grant任务60次/分钟、历史256个，全局历史512个和同时运行32个（上传同时最多4个）。任务5分钟取消时限，结果JSON上限1MiB，每grant事件连接8个、每连接队列128条。达到容量返回429，重配对或重启可重建内存会话。GET静态能力30秒超时，并跟随grant撤销取消。

## 离线验证

`dotnet restore tests/server/Omrina.Server.Tests.csproj --configfile .tools/NuGet.Config` 使用仓库离线源；`dotnet run --project tests/server/Omrina.Server.Tests.csproj --no-restore` 运行真实loopback HTTP/WS测试、mock operations，不调用扫描硬件。

`-- --browser-harness` 启动仅测试mock host17844；console stdin 接受 `pending`、`approve <requestId>`、`grants`、`revoke <grantId>`、`quit`。测试code只在测试console显示，不写文件；生产服务无自动批准和测试路由。

## 学校答题纸扩展（schema 3）

`template` 操作新增互斥输入 `{schoolDefinition}`；原 schema 1/2 请求继续支持。`schoolDefinition` 包含 `examId/layoutDocumentId/version/title/paper/mode/columns/bubbleShape/labelPlacement/bubbleWidthMm/bubbleHeightMm/candidateIdentity/duplex/repeatBackIdentity/questions`。只有 `examId/layoutDocumentId/questions` 是请求必需项，其余沿用 Core 默认值；持久化快照则要求完整字段，以免历史版式因为默认值变化而改变。

学校定义枚举采用 PascalCase 字符串：`A4Portrait/A3Landscape`、`AnswerOnly/WithQuestions`、`Choice/Subjective`、`Circle/Rectangle`、`Inside/Outside`、`Barcode/Marking`。这与外层 operation/status 的 camelCase 分开。`candidateIdentity` 为 `{mode,digits,candidateId?}`；`questions` 为 `[{number,type,maximumScore?,body?,options?,subjectiveHeightMm?}]`。拒绝所有层级未知字段、路径字段、重复属性与重复题号；不接受 enum 整数。

成功返回 `{documentId,examId,version,pages}`，每页含原模板摘要与 `schemaVersion:3,paper,side,pageIndex,widthMm,heightMm,schoolMetadata,svg`。`schoolMetadata` 为 `{examId,layoutDocumentId,version,pageNumber,side,templateId}`；`pageIndex` 零基，`pageNumber` 一基，`side` 为 `Front/Back`。分页模板分别注册在当前 grant，可直接沿用 upload/scan/recognize 操作；目录接口不会列出桌面本地考试或其他 grant 的资料。

学校定义限制：题目 1–500、最多 64 页、选择题选项 2–6、考号 1–20 位数字；A4 一栏、A3 两栏或三栏；圆框直径/矩形宽度 `(0,2] mm`、矩形高度 `(0,4] mm`、每题主观区高度 `[10,230] mm`。题号必须唯一且为正整数。标题/考试 ID/版式 ID 最多 80 字符；每题正文最多 8000 字符；定义 UTF-8 最大 60000 字节，仍受完整 HTTP 请求 64 KiB 限制。无法放入一栏的题目会明确拒绝。

学校 capture 在提交目录前验证原图四角、方向和机器码；考试 ID、版式版本、页码、正反面或页模板身份不匹配时返回 `CAPTURE_SCHOOL_PAGE_MISMATCH`，不保存页面。manifest 保存完整页快照、学校元数据及原图 SHA256；重新打开时复算快照身份并验证原图哈希，兼容读取既有 schema 1/2 数据。

upload/scan 的 capture 摘要及 recognize 外层结果增加 `schoolMetadata,candidateId,identityStatus`。仅可靠读出的考号为 `Identified`；空白、多涂、无条码、无法解码或无反面身份区为 `RequireAssociation`，考号为空。不依据考试码或页序猜测学生，不自动配对无身份反面。旧版 capture 的学校字段为 null。

学校 score/review 接受当前页全部选择题答案，也接受完整考试选择题答案（校验合法选项后取本页）；不连续题号按定义原样保留，分值来自学校定义。输出为页面选择题成绩，不能代表缺页、未确认身份或未完成主观题的整卷最终成绩。

scan 纸张由关联页模板决定，NAPS2 请求 A4 210×297 或 A3 420×297 mm 平板扫描，禁止尺寸拉伸/裁切及回退 A4；实际设备/驱动可能拒绝 A3，未验证能力不声明为支持。导入 A3 不依赖设备。

`node tests/integration/school-sdk-e2e.mjs` 使用真实 loopback HTTP 与已构建 SDK，对合成 A3 纸执行配对、生成、导入、可靠考号、识别、非连续题号复核、错误考试拒绝和撤销授权；测试 host 不运行扫描硬件，也不扩大生产批准边界。

学校模板文档的完整任务结果（包括所有页 SVG）沿用 1 MiB UTF-8 JSON 上限。服务端在注册任何页面模板之前检查该大小；超限返回 `TEMPLATE_DOCUMENT_TOO_LARGE`，当前授权不会遗留该次生成的页模板。最多 64 页是布局上限，不保证包含预览的结果能进入 1 MiB；较长文档需减少页数或正文后重新生成。

学校扫描结果包含与上传一致的 `schoolMetadata/candidateId/identityStatus`，并保留 `dpi/pageSize/flatbed` 扫描信息；SDK 导出 `SchoolScanCaptureSummary` 表达完整返回类型。
