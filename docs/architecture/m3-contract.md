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
