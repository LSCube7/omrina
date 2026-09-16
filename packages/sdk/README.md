# AnswerSheet 本地 SDK（M0）

这是一个无依赖的 TypeScript 健康检查客户端。默认会请求
`http://127.0.0.1:17843/health`，并且只允许 HTTP 回环地址（`127.0.0.0/8`、`localhost` 和 `::1`）。

```ts
import { checkHealth } from "./src/index.ts";

const health = await checkHealth();
// { service: "answersheet-local", protocolVersion: 1, status: "ready" }
```

可以传入 `endpoint`、`timeoutMs`、`signal`，以及用于测试或宿主环境的 `fetch` 实现。服务端返回非 2xx、网络失败、超时、取消或返回结构不符合 M0 协议时，会抛出带有 `code` 的 `AnswerSheetSdkError`。

## 验证

```powershell
node --test tests/sdk/*.test.mjs
```

该测试使用注入的模拟 `fetch`，不会建立真实的本地服务连接。桌面 M0 原型已提供同一回环健康检查端点；真实联调需先启动桌面应用，再从仓库根目录运行：

```powershell
node .\tests\integration\health.mjs
```

该集成脚本验证固定响应、外部 Origin 拒绝、未知路径 404 与 `POST /health` 的 405。它不验证浏览器 CORS 或 Local Network Access 行为，也不授予设备访问权限。
