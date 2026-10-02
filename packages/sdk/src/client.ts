import { OmrinaSdkError } from "./errors.ts";
import { DEFAULT_ENDPOINT, DEFAULT_TIMEOUT_MS } from "./health.ts";

const MAX_TIMEOUT_MS = 2_147_483_647;
const MAX_JSON_BYTES = 64 * 1024;
const MAX_UPLOAD_METADATA_BYTES = 4 * 1024;
const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;
const INITIAL_RECONNECT_DELAY_MS = 250;
const MAX_RECONNECT_DELAY_MS = 5_000;

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type TaskOperation = "template" | "upload" | "scan" | "recognize" | "score" | "review" | "export";
export type TaskStatus = "queued" | "running" | "completed" | "failed" | "cancelled";

export type ProtocolError = {
  code: string;
  message: string;
};

export type TaskSnapshot = {
  taskId: string;
  operation: TaskOperation;
  status: TaskStatus;
  result: unknown | null;
  error: ProtocolError | null;
  createdAt: string;
  updatedAt: string;
};

export type PairingTicket = {
  requestId: string;
  expiresAt: string;
};

export type GrantCredentials = {
  grantId: string;
  token: string;
  expiresAt: string;
};

export type TemplateSummary = {
  templateId: string;
  schemaVersion: number;
  title: string;
  questionCount: number;
  optionsPerQuestion: number;
  templateNumber: string;
  svg: string;
};

export type DeviceDescriptor = {
  deviceId: string;
  name: string;
  driver: string;
};

export type TemplateTaskParameters = {
  title: string;
  questionCount: number;
  optionsPerQuestion: number;
};

export type ScanTaskParameters = {
  templateId: string;
  deviceId: string;
  dpi: number;
};

export type RecognitionTaskParameters = {
  captureId: string;
};

export type AnswerKey = Record<string, string>;

export type ScoreTaskParameters = {
  resultId: string;
  answerKey: AnswerKey;
  pointsPerQuestion?: number;
};

export type ReviewEdit = {
  questionNumber: number;
  answer: string | null;
  reason: string;
  timestampUtc?: string;
};

export type ReviewTaskParameters = {
  resultId: string;
  expectedVersion: number;
  reviewer: string;
  edits: ReviewEdit[];
  answerKey?: AnswerKey;
};

export type ExportTaskParameters = {
  resultId: string;
  format: "json" | "csv";
};

export type UploadImageRequest = {
  idempotencyKey: string;
  templateId: string;
  fileName: string;
  contentType: "image/png" | "image/jpeg";
  body: Blob | ArrayBuffer | Uint8Array;
};

export type TaskSubmission<TParameters extends object> = {
  idempotencyKey: string;
  parameters: TParameters;
};

export type RequestOptions = {
  timeoutMs?: number;
  signal?: AbortSignal;
};

export type PairingExchangeRequest = {
  requestId: string;
  code: string;
};

export type SdkResponse = {
  ok: boolean;
  status: number;
  json(): Promise<unknown>;
};

/** 用于测试或宿主注入的 fetch；SDK 自己构造固定路由与请求头。 */
export type SdkFetch = (input: string, init: RequestInit) => Promise<SdkResponse>;

export type EventSocket = Pick<
  WebSocket,
  "readyState" | "send" | "close" | "addEventListener" | "removeEventListener"
>;

export type EventSocketFactory = (url: string) => EventSocket;

export type OmrinaClientOptions = {
  /** 本地服务的 HTTP 根地址；只允许 localhost 或 127.0.0.1。 */
  endpoint?: string | URL;
  /** 每个请求的默认超时，单位为毫秒。 */
  timeoutMs?: number;
  /** 测试或宿主环境可注入；不会开放自定义路径或请求头。 */
  fetch?: SdkFetch;
  /** 测试或宿主环境可注入；SDK 始终连接固定的 /v1/events。 */
  webSocketFactory?: EventSocketFactory;
  /** 宿主在内存中提供的凭据；SDK 不会持久化。 */
  grant?: GrantCredentials;
};

export type CreateTaskOptions = RequestOptions & {
  idempotencyKey: string;
};

export type TaskEvent = {
  type: "task";
  sequence: number;
  task: TaskSnapshot;
};

export type TaskRecoveryReason = "connected" | "reconnected" | "sequence-gap";

export type TaskRecovery = {
  reason: TaskRecoveryReason;
  sequence: number;
  tasks: TaskSnapshot[];
};

export type WatchEventsOptions = {
  /** watchEvents 会持续运行，需由宿主信号结束订阅。 */
  signal: AbortSignal;
  timeoutMs?: number;
  onEvent(event: TaskEvent): void | Promise<void>;
  onRecovered?(recovery: TaskRecovery): void | Promise<void>;
  onAuthenticated?(sequence: number): void | Promise<void>;
};

type JsonTaskOperation = Exclude<TaskOperation, "upload">;
type RequestMethod = "GET" | "POST" | "DELETE";
type ResponseConsumer<T> = (response: SdkResponse, timedOut: () => boolean, signal?: AbortSignal) => Promise<T>;

class EventCallbackFailure extends Error {
  constructor(cause: unknown) {
    super("Event callback failed.", { cause });
    this.name = "EventCallbackFailure";
  }
}

/**
 * OMRINA M3 本地服务客户端。每个实例只访问固定的 /v1 路由，令牌只保存在实例内存中。
 */
export class OmrinaClient {
  readonly #baseUrl: URL;
  readonly #defaultTimeoutMs: number;
  readonly #fetch: SdkFetch;
  readonly #webSocketFactory: EventSocketFactory | undefined;
  #grant: GrantCredentials | undefined;

  constructor(options: OmrinaClientOptions = {}) {
    this.#baseUrl = createBaseUrl(options.endpoint ?? DEFAULT_ENDPOINT);
    this.#defaultTimeoutMs = validateTimeout(options.timeoutMs ?? DEFAULT_TIMEOUT_MS);
    this.#fetch = options.fetch ?? getGlobalFetch();
    this.#webSocketFactory = options.webSocketFactory;
    this.#grant = options.grant ? freezeGrant(parseGrantCredentials(options.grant)) : undefined;
  }

  /** 向桌面申请一次性配对审批。Origin 由浏览器自动附加。 */
  async requestPairing(
    request: { clientName: string },
    options: RequestOptions = {},
  ): Promise<PairingTicket> {
    requireNonEmptyString(request.clientName, "clientName");
    const body = encodeJsonBody({ clientName: request.clientName });
    return this.#requestJson(
      "/v1/pairing/requests",
      "POST",
      options,
      { headers: { "Content-Type": "application/json" }, body, authenticated: false, expectedStatus: 202 },
      parsePairingTicket,
    );
  }

  /** 用桌面显示的一次性 code 交换授权，并仅在此实例内保存 token。 */
  async exchangePairing(
    request: PairingExchangeRequest,
    options: RequestOptions = {},
  ): Promise<GrantCredentials> {
    requireNonEmptyString(request.requestId, "requestId");
    requireNonEmptyString(request.code, "code");
    const body = encodeJsonBody({ requestId: request.requestId, code: request.code });
    const grant = await this.#requestJson(
      "/v1/pairing/exchange",
      "POST",
      options,
      { headers: { "Content-Type": "application/json" }, body, authenticated: false },
      parseGrantCredentials,
    );
    this.#grant = freezeGrant(grant);
    return this.#grant;
  }

  /** 撤销当前授权；成功后立即从实例内存清除 token。 */
  async revokeGrant(options: RequestOptions = {}): Promise<void> {
    this.#requireGrant();
    await this.#requestNoContent("/v1/grant", "DELETE", options, { authenticated: true });
    this.#grant = undefined;
  }

  async getTemplates(options: RequestOptions = {}): Promise<TemplateSummary[]> {
    return this.#requestJson(
      "/v1/templates",
      "GET",
      options,
      { authenticated: true },
      parseTemplateCatalog,
    );
  }

  async getDevices(options: RequestOptions = {}): Promise<DeviceDescriptor[]> {
    return this.#requestJson(
      "/v1/devices",
      "GET",
      options,
      { authenticated: true },
      parseDeviceCatalog,
    );
  }

  createTemplateTask(
    request: TaskSubmission<TemplateTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateTemplateParameters(request.parameters);
    return this.#createJsonTask("template", request, options);
  }

  uploadImage(request: UploadImageRequest, options: RequestOptions = {}): Promise<TaskSnapshot> {
    validateIdempotencyKey(request.idempotencyKey);
    requireNonEmptyString(request.templateId, "templateId");
    validateFileName(request.fileName);
    const byteLength = getImageByteLength(request.body);
    if (byteLength > MAX_UPLOAD_BYTES) {
      throw invalidArgument("Image upload must not exceed 20 MiB.");
    }

    const metadata = encodeAsciiJsonHeader(
      { templateId: request.templateId, fileName: request.fileName },
      MAX_UPLOAD_METADATA_BYTES,
    );
    return this.#requestJson(
      "/v1/tasks/upload",
      "POST",
      options,
      {
        headers: {
          "Content-Type": request.contentType,
          "X-Idempotency-Key": request.idempotencyKey,
          "X-Omrina-Parameters": metadata,
        },
        body: createImageBlob(request.body, request.contentType),
        authenticated: true,
        expectedStatus: 202,
      },
      parseTaskSnapshot,
    );
  }

  createScanTask(
    request: TaskSubmission<ScanTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateScanParameters(request.parameters);
    return this.#createJsonTask("scan", request, options);
  }

  createRecognitionTask(
    request: TaskSubmission<RecognitionTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    requireNonEmptyString(request.parameters.captureId, "captureId");
    return this.#createJsonTask("recognize", request, options);
  }

  createScoreTask(
    request: TaskSubmission<ScoreTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    requireNonEmptyString(request.parameters.resultId, "resultId");
    validateAnswerKey(request.parameters.answerKey, "answerKey");
    if (request.parameters.pointsPerQuestion !== undefined
      && (!Number.isFinite(request.parameters.pointsPerQuestion) || request.parameters.pointsPerQuestion <= 0)) {
      throw invalidArgument("pointsPerQuestion must be a positive number.");
    }
    return this.#createJsonTask("score", request, options);
  }

  createReviewTask(
    request: TaskSubmission<ReviewTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateReviewParameters(request.parameters);
    return this.#createJsonTask("review", request, options);
  }

  createExportTask(
    request: TaskSubmission<ExportTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    requireNonEmptyString(request.parameters.resultId, "resultId");
    if (request.parameters.format !== "json" && request.parameters.format !== "csv") {
      throw invalidArgument("format must be json or csv.");
    }
    return this.#createJsonTask("export", request, options);
  }

  async listTasks(options: RequestOptions = {}): Promise<TaskSnapshot[]> {
    return this.#requestJson("/v1/tasks", "GET", options, { authenticated: true }, parseTaskList);
  }

  async getTask(taskId: string, options: RequestOptions = {}): Promise<TaskSnapshot> {
    const path = `/v1/tasks/${encodeTaskId(taskId)}`;
    return this.#requestJson(path, "GET", options, { authenticated: true }, parseTaskSnapshot);
  }

  async cancelTask(taskId: string, options: RequestOptions = {}): Promise<TaskSnapshot> {
    const path = `/v1/tasks/${encodeTaskId(taskId)}/cancel`;
    return this.#requestJson(path, "POST", options, { authenticated: true }, parseTaskSnapshot);
  }

  /**
   * 订阅 task 事件。连接中断后只重连 WebSocket 并 GET /v1/tasks 补齐状态，绝不重发创建请求。
   * 调用方应通过 signal 结束订阅。
   */
  async watchEvents(options: WatchEventsOptions): Promise<void> {
    const grant = this.#requireGrant();
    const timeoutMs = validateTimeout(options.timeoutMs ?? this.#defaultTimeoutMs);
    const socketFactory = this.#webSocketFactory ?? getGlobalWebSocketFactory();
    const signal = options.signal;
    let lastSequence: number | undefined;
    let hasConnected = false;
    let reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;

    while (true) {
      throwIfRequestAborted(false, signal);
      try {
        const closeCode = await this.#receiveEventConnection(
          socketFactory,
          grant.token,
          timeoutMs,
          signal,
          hasConnected,
          lastSequence,
          options,
          (sequence) => {
            lastSequence = sequence;
          },
        );
        hasConnected = true;
        if (signal.aborted) {
          throw getRequestAbortError(false, signal);
        }
        if (closeCode === 1008) {
          this.#grant = undefined;
          throw new OmrinaSdkError("UNAUTHORIZED", "The event connection was rejected.");
        }
        await delayWithSignal(reconnectDelayMs, signal);
        reconnectDelayMs = Math.min(reconnectDelayMs * 2, MAX_RECONNECT_DELAY_MS);
      } catch (cause) {
        if (signal.aborted) {
          throw getRequestAbortError(false, signal, cause);
        }
        if (cause instanceof EventCallbackFailure) {
          throw cause.cause;
        }
        if (cause instanceof OmrinaSdkError && cause.code === "UNAUTHORIZED") {
          this.#grant = undefined;
          throw cause;
        }
        if (cause instanceof OmrinaSdkError
          && (cause.code === "INVALID_RESPONSE" || cause.code === "INVALID_ARGUMENT")) {
          throw cause;
        }
        if (!hasConnected) {
          throw cause;
        }

        await delayWithSignal(reconnectDelayMs, signal);
        reconnectDelayMs = Math.min(reconnectDelayMs * 2, MAX_RECONNECT_DELAY_MS);
      }
    }
  }

  #createJsonTask<TParameters extends object>(
    operation: JsonTaskOperation,
    request: TaskSubmission<TParameters>,
    options: RequestOptions,
  ): Promise<TaskSnapshot> {
    validateIdempotencyKey(request.idempotencyKey);
    const body = encodeJsonBody({
      idempotencyKey: request.idempotencyKey,
      operation,
      parameters: request.parameters,
    });
    return this.#requestJson(
      "/v1/tasks",
      "POST",
      options,
      { headers: { "Content-Type": "application/json" }, body, authenticated: true, expectedStatus: 202 },
      parseTaskSnapshot,
    );
  }

  #requireGrant(): GrantCredentials {
    if (!this.#grant) {
      throw new OmrinaSdkError("MISSING_CREDENTIAL", "Pair this client with the desktop app first.");
    }
    return this.#grant;
  }

  #requestJson<T>(
    path: string,
    method: RequestMethod,
    options: RequestOptions,
    request: {
      authenticated?: boolean;
      headers?: Record<string, string>;
      body?: BodyInit;
      expectedStatus?: number;
    },
    parse: (payload: unknown) => T,
  ): Promise<T> {
    return this.#performRequest(path, method, options, request, async (response, timedOut, signal) => {
      let payload: unknown;
      try {
        payload = await response.json();
      } catch (cause) {
        const abortError = getRequestAbortError(timedOut(), signal, cause);
        if (abortError) {
          throw abortError;
        }
        throw new OmrinaSdkError("INVALID_RESPONSE", "The local agent did not return valid JSON.", { cause });
      }
      throwIfRequestAborted(timedOut(), signal);
      return parse(payload);
    });
  }

  #requestNoContent(
    path: string,
    method: RequestMethod,
    options: RequestOptions,
    request: { authenticated?: boolean; headers?: Record<string, string>; body?: BodyInit },
  ): Promise<void> {
    return this.#performRequest(path, method, options, request, async () => undefined);
  }

  async #performRequest<T>(
    path: string,
    method: RequestMethod,
    options: RequestOptions,
    request: {
      authenticated?: boolean;
      headers?: Record<string, string>;
      body?: BodyInit;
      expectedStatus?: number;
    },
    consume: ResponseConsumer<T>,
  ): Promise<T> {
    const timeoutMs = validateTimeout(options.timeoutMs ?? this.#defaultTimeoutMs);
    const headers: Record<string, string> = { ...request.headers };
    if (request.authenticated !== false) {
      headers.Authorization = `Bearer ${this.#requireGrant().token}`;
    }

    const controller = new AbortController();
    let timedOut = false;
    const abortForCaller = () => controller.abort(options.signal?.reason);
    if (options.signal?.aborted) {
      abortForCaller();
    } else {
      options.signal?.addEventListener("abort", abortForCaller, { once: true });
    }

    const timeout = setTimeout(() => {
      timedOut = true;
      controller.abort(new Error("Request timed out"));
    }, timeoutMs);

    try {
      throwIfRequestAborted(timedOut, options.signal);
      const init: RequestInit = {
        method,
        headers,
        signal: controller.signal,
        redirect: "error",
        credentials: "omit",
        cache: "no-store",
        mode: "cors",
      };
      if (request.body !== undefined) {
        init.body = request.body;
      }

      const response = await this.#fetch(buildRouteUrl(this.#baseUrl, path), init);
      throwIfRequestAborted(timedOut, options.signal);
      if (!response.ok) {
        const error = await parseHttpError(response);
        throwIfRequestAborted(timedOut, options.signal);
        if (error.code === "UNAUTHORIZED") {
          this.#grant = undefined;
        }
        throw error;
      }
      if (request.expectedStatus !== undefined && response.status !== request.expectedStatus) {
        throw new OmrinaSdkError(
          "INVALID_RESPONSE",
          `The local agent returned HTTP ${response.status}; expected ${request.expectedStatus}.`,
          { status: response.status },
        );
      }

      return await consume(response, () => timedOut, options.signal);
    } catch (cause) {
      if (cause instanceof OmrinaSdkError) {
        throw cause;
      }

      const abortError = getRequestAbortError(timedOut, options.signal, cause);
      if (abortError) {
        throw abortError;
      }

      throw new OmrinaSdkError("NETWORK_ERROR", "The local agent request failed.", { cause });
    } finally {
      clearTimeout(timeout);
      options.signal?.removeEventListener("abort", abortForCaller);
    }
  }

  async #receiveEventConnection(
    socketFactory: EventSocketFactory,
    token: string,
    timeoutMs: number,
    signal: AbortSignal,
    hasConnected: boolean,
    previousSequence: number | undefined,
    callbacks: WatchEventsOptions,
    rememberSequence: (sequence: number) => void,
  ): Promise<number> {
    throwIfRequestAborted(false, signal);
    const socketUrl = createEventsUrl(this.#baseUrl);
    let socket: EventSocket;
    try {
      socket = socketFactory(socketUrl);
    } catch (cause) {
      throw new OmrinaSdkError("NETWORK_ERROR", "Could not open the local event connection.", { cause });
    }

    return new Promise<number>((resolve, reject) => {
      let settled = false;
      let opened = false;
      let authenticated = false;
      let currentSequence = previousSequence;
      let timeout: ReturnType<typeof setTimeout> | undefined;
      let messageQueue = Promise.resolve();

      const onOpen = () => {
        opened = true;
        try {
          socket.send(JSON.stringify({ type: "authenticate", token }));
        } catch (cause) {
          rejectOnce(new OmrinaSdkError("EVENT_ERROR", "Could not authenticate the event connection.", { cause }));
        }
      };

      const onMessage = (event: MessageEvent<unknown>) => {
        messageQueue = messageQueue
          .then(async () => {
            await this.#handleEventMessage(
              event.data,
              socket,
              signal,
              timeoutMs,
              authenticated,
              hasConnected,
              currentSequence,
              callbacks,
              (sequence) => {
                authenticated = true;
                currentSequence = sequence;
                rememberSequence(sequence);
                if (timeout !== undefined) {
                  clearTimeout(timeout);
                  timeout = undefined;
                }
              },
              (sequence) => {
                currentSequence = sequence;
                rememberSequence(sequence);
              },
            );
          })
          .catch((cause: unknown) => rejectOnce(cause));
      };

      const onError = (event: Event) => {
        rejectOnce(new OmrinaSdkError("NETWORK_ERROR", "The local event connection failed.", { cause: event }));
      };

      const onClose = (event: CloseEvent) => {
        void messageQueue.then(() => {
          if (settled) {
            return;
          }
          if (!authenticated) {
            const error = event.code === 1008
              ? new OmrinaSdkError("UNAUTHORIZED", "The event connection was rejected.")
              : new OmrinaSdkError("EVENT_DISCONNECTED", opened
                ? "The event connection closed before authentication completed."
                : "The event connection closed before it opened.");
            rejectOnce(error);
            return;
          }
          resolveOnce(event.code);
        }, rejectOnce);
      };

      const cleanup = () => {
        if (timeout !== undefined) {
          clearTimeout(timeout);
        }
        signal.removeEventListener("abort", abortForCaller);
        socket.removeEventListener("open", onOpen);
        socket.removeEventListener("message", onMessage);
        socket.removeEventListener("error", onError);
        socket.removeEventListener("close", onClose);
      };

      const rejectOnce = (cause: unknown) => {
        if (settled) {
          return;
        }
        settled = true;
        cleanup();
        try {
          socket.close(1000, "client stopped");
        } catch {
          // Closing a failed socket is best effort; the original error is preserved.
        }
        reject(cause);
      };

      const resolveOnce = (code: number) => {
        if (settled) {
          return;
        }
        settled = true;
        cleanup();
        resolve(code);
      };

      timeout = setTimeout(() => {
        timeout = undefined;
        rejectOnce(new OmrinaSdkError("TIMEOUT", "Event authentication timed out."));
      }, timeoutMs);

      const abortForCaller = () => rejectOnce(getRequestAbortError(false, signal));
      if (signal.aborted) {
        abortForCaller();
        return;
      }
      signal.addEventListener("abort", abortForCaller, { once: true });

      socket.addEventListener("open", onOpen, { once: true });
      socket.addEventListener("message", onMessage);
      socket.addEventListener("error", onError, { once: true });
      socket.addEventListener("close", onClose, { once: true });
    });
  }

  async #handleEventMessage(
    message: unknown,
    socket: EventSocket,
    signal: AbortSignal,
    timeoutMs: number,
    authenticated: boolean,
    hasConnected: boolean,
    previousSequence: number | undefined,
    callbacks: WatchEventsOptions,
    onAuthenticated: (sequence: number) => void,
    rememberSequence: (sequence: number) => void,
  ): Promise<void> {
    if (typeof message !== "string") {
      throw invalidResponse("Event messages must be JSON text.");
    }
    let payload: unknown;
    try {
      payload = JSON.parse(message);
    } catch (cause) {
      throw new OmrinaSdkError("INVALID_RESPONSE", "Event message was not valid JSON.", { cause });
    }
    const event = parseAgentEvent(payload);
    if (event.type === "authenticated") {
      if (authenticated) {
        throw invalidResponse("The event connection authenticated more than once.");
      }
      if (previousSequence !== undefined && event.sequence < previousSequence) {
        throw invalidResponse("The event sequence moved backwards.");
      }

      try {
        await callbacks.onAuthenticated?.(event.sequence);
      } catch (cause) {
        throw new EventCallbackFailure(cause);
      }
      onAuthenticated(event.sequence);
      const tasks = await this.listTasks({ signal, timeoutMs });
      await this.#notifyRecovered(callbacks, {
        reason: hasConnected ? "reconnected" : "connected",
        sequence: event.sequence,
        tasks,
      });
      return;
    }

    if (!authenticated) {
      throw invalidResponse("The event connection sent a task before authentication.");
    }
    if (previousSequence === undefined || event.sequence <= previousSequence) {
      throw invalidResponse("The event sequence did not increase.");
    }
    if (event.sequence > previousSequence + 1) {
      const tasks = await this.listTasks({ signal, timeoutMs });
      await this.#notifyRecovered(callbacks, {
        reason: "sequence-gap",
        sequence: event.sequence,
        tasks,
      });
    }

    rememberSequence(event.sequence);
    try {
      await callbacks.onEvent(event);
    } catch (cause) {
      throw new EventCallbackFailure(cause);
    }

    if (socket.readyState === 3 && signal.aborted) {
      throw getRequestAbortError(false, signal);
    }
  }

  async #notifyRecovered(callbacks: WatchEventsOptions, recovery: TaskRecovery): Promise<void> {
    try {
      await callbacks.onRecovered?.(recovery);
    } catch (cause) {
      throw new EventCallbackFailure(cause);
    }
  }
}

function getGlobalFetch(): SdkFetch {
  if (typeof globalThis.fetch !== "function") {
    throw new OmrinaSdkError("NETWORK_ERROR", "No fetch implementation is available.");
  }
  return (input, init) => globalThis.fetch(input, init);
}

function getGlobalWebSocketFactory(): EventSocketFactory {
  if (typeof globalThis.WebSocket !== "function") {
    throw new OmrinaSdkError("EVENT_ERROR", "No WebSocket implementation is available.");
  }
  return (url) => new globalThis.WebSocket(url);
}

function createImageBlob(body: Blob | ArrayBuffer | Uint8Array, contentType: string): Blob {
  if (typeof Blob !== "undefined" && body instanceof Blob) {
    return body;
  }
  if (body instanceof ArrayBuffer) {
    return new Blob([body], { type: contentType });
  }
  if (body instanceof Uint8Array) {
    const buffer = new ArrayBuffer(body.byteLength);
    new Uint8Array(buffer).set(body);
    return new Blob([buffer], { type: contentType });
  }
  throw invalidArgument("Image body must be a Blob, ArrayBuffer, or Uint8Array.");
}

function createBaseUrl(endpoint: string | URL): URL {
  let url: URL;
  try {
    url = new URL(endpoint);
  } catch (cause) {
    throw new OmrinaSdkError("INVALID_ENDPOINT", "Endpoint must be an absolute HTTP URL.", { cause });
  }

  if (url.protocol !== "http:"
    || (url.hostname !== "127.0.0.1" && url.hostname.toLowerCase() !== "localhost")
    || url.username
    || url.password) {
    throw new OmrinaSdkError(
      "INVALID_ENDPOINT",
      "Endpoint must be an unauthenticated HTTP URL on localhost or 127.0.0.1.",
    );
  }
  return new URL(`${url.origin}/`);
}

function buildRouteUrl(baseUrl: URL, path: string): string {
  return new URL(path, baseUrl).toString();
}

function createEventsUrl(baseUrl: URL): string {
  const url = new URL("/v1/events", baseUrl);
  url.protocol = "ws:";
  return url.toString();
}

function validateTimeout(timeoutMs: number): number {
  if (!Number.isFinite(timeoutMs) || timeoutMs <= 0 || timeoutMs > MAX_TIMEOUT_MS) {
    throw new OmrinaSdkError(
      "INVALID_TIMEOUT",
      `timeoutMs must be between 1 and ${MAX_TIMEOUT_MS} milliseconds.`,
    );
  }
  return timeoutMs;
}

function throwIfRequestAborted(timedOut: boolean, signal?: AbortSignal): void {
  const error = getRequestAbortError(timedOut, signal);
  if (error) {
    throw error;
  }
}

function getRequestAbortError(
  timedOut: boolean,
  signal?: AbortSignal,
  cause?: unknown,
): OmrinaSdkError | undefined {
  if (timedOut) {
    return new OmrinaSdkError("TIMEOUT", "The local agent request timed out.", { cause });
  }
  if (signal?.aborted) {
    return new OmrinaSdkError("ABORTED", "The local agent request was cancelled.", { cause });
  }
  return undefined;
}

function delayWithSignal(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const timeout = setTimeout(() => {
      signal.removeEventListener("abort", abort);
      resolve();
    }, milliseconds);
    const abort = () => {
      clearTimeout(timeout);
      signal.removeEventListener("abort", abort);
      reject(getRequestAbortError(false, signal));
    };
    signal.addEventListener("abort", abort, { once: true });
    if (signal.aborted) {
      abort();
    }
  });
}

function encodeJsonBody(value: unknown, maximumBytes = MAX_JSON_BYTES): string {
  validateJsonValue(value, "request body");
  if (!isRecord(value)) {
    throw invalidArgument("Request parameters must be a JSON object.");
  }
  let body: string | undefined;
  try {
    body = JSON.stringify(value);
  } catch (cause) {
    throw invalidArgument("Request parameters could not be serialized as JSON.", cause);
  }
  if (typeof body !== "string") {
    throw invalidArgument("Request parameters could not be serialized as JSON.");
  }
  if (new TextEncoder().encode(body).byteLength > maximumBytes) {
    throw invalidArgument(`JSON request must not exceed ${maximumBytes} bytes.`);
  }
  return body;
}

function encodeAsciiJsonHeader(value: unknown, maximumBytes: number): string {
  validateJsonValue(value, "request header");
  if (!isRecord(value)) {
    throw invalidArgument("Request header parameters must be a JSON object.");
  }
  let body: string | undefined;
  try {
    body = JSON.stringify(value);
  } catch (cause) {
    throw invalidArgument("Request header parameters could not be serialized as JSON.", cause);
  }
  if (typeof body !== "string") {
    throw invalidArgument("Request header parameters could not be serialized as JSON.");
  }
  const asciiBody = body.replace(/[^\u0000-\u007f]/g, (character) => {
    return `\\u${character.charCodeAt(0).toString(16).padStart(4, "0")}`;
  });
  if (new TextEncoder().encode(asciiBody).byteLength > maximumBytes) {
    throw invalidArgument(`JSON request header must not exceed ${maximumBytes} bytes.`);
  }
  return asciiBody;
}

function validateJsonValue(value: unknown, label: string): asserts value is JsonValue {
  if (!isJsonValue(value, new WeakSet<object>())) {
    throw invalidArgument(`${label} must contain only finite JSON values.`);
  }
}

function isJsonValue(value: unknown, seen: WeakSet<object>): value is JsonValue {
  if (value === null || typeof value === "string" || typeof value === "boolean") {
    return true;
  }
  if (typeof value === "number") {
    return Number.isFinite(value);
  }
  if (typeof value !== "object") {
    return false;
  }
  if (seen.has(value)) {
    return false;
  }
  seen.add(value);

  let valid: boolean;
  if (Array.isArray(value)) {
    valid = value.every((entry: unknown) => isJsonValue(entry, seen));
  } else {
    const prototype = Object.getPrototypeOf(value);
    valid = (prototype === Object.prototype || prototype === null)
      && Object.values(value).every((entry: unknown) => isJsonValue(entry, seen));
  }
  seen.delete(value);
  return valid;
}

function parseHttpError(response: SdkResponse): Promise<OmrinaSdkError> {
  return response.json().then((payload: unknown) => {
    const remoteError = parseProtocolError(payload);
    const unauthorized = response.status === 401 || response.status === 403;
    const errorOptions = remoteError
      ? { status: response.status, remoteCode: remoteError.code }
      : { status: response.status };
    return new OmrinaSdkError(
      unauthorized ? "UNAUTHORIZED" : "REMOTE_ERROR",
      remoteError?.message ?? `The local agent returned HTTP ${response.status}.`,
      errorOptions,
    );
  }).catch(() => {
    const unauthorized = response.status === 401 || response.status === 403;
    return new OmrinaSdkError(
      unauthorized ? "UNAUTHORIZED" : "HTTP_ERROR",
      `The local agent returned HTTP ${response.status}.`,
      { status: response.status },
    );
  });
}

function parsePairingTicket(payload: unknown): PairingTicket {
  if (!isRecord(payload) || typeof payload.requestId !== "string" || !isDateString(payload.expiresAt)) {
    throw invalidResponse("Pairing response did not match the supported protocol.");
  }
  return { requestId: payload.requestId, expiresAt: payload.expiresAt };
}

function parseGrantCredentials(payload: unknown): GrantCredentials {
  if (!isRecord(payload)
    || typeof payload.grantId !== "string"
    || typeof payload.token !== "string"
    || payload.token.length === 0
    || !isDateString(payload.expiresAt)) {
    throw invalidResponse("Grant response did not match the supported protocol.");
  }
  return { grantId: payload.grantId, token: payload.token, expiresAt: payload.expiresAt };
}

function freezeGrant(grant: GrantCredentials): GrantCredentials {
  return Object.freeze({ ...grant });
}

function parseTemplateCatalog(payload: unknown): TemplateSummary[] {
  if (!Array.isArray(payload)) {
    throw invalidResponse("Template response must be a JSON array.");
  }
  return payload.map(parseTemplateSummary);
}

function parseTemplateSummary(payload: unknown): TemplateSummary {
  if (!isRecord(payload)
    || typeof payload.templateId !== "string"
    || !isInteger(payload.schemaVersion)
    || typeof payload.title !== "string"
    || !isInteger(payload.questionCount)
    || !isInteger(payload.optionsPerQuestion)
    || typeof payload.templateNumber !== "string"
    || typeof payload.svg !== "string") {
    throw invalidResponse("Template entry did not match the supported protocol.");
  }
  return {
    templateId: payload.templateId,
    schemaVersion: payload.schemaVersion,
    title: payload.title,
    questionCount: payload.questionCount,
    optionsPerQuestion: payload.optionsPerQuestion,
    templateNumber: payload.templateNumber,
    svg: payload.svg,
  };
}

function parseDeviceCatalog(payload: unknown): DeviceDescriptor[] {
  if (!Array.isArray(payload)) {
    throw invalidResponse("Device response must be a JSON array.");
  }
  return payload.map((entry: unknown) => {
    if (!isRecord(entry)
      || typeof entry.deviceId !== "string"
      || typeof entry.name !== "string"
      || typeof entry.driver !== "string") {
      throw invalidResponse("Device entry did not match the supported protocol.");
    }
    return { deviceId: entry.deviceId, name: entry.name, driver: entry.driver };
  });
}

function parseTaskList(payload: unknown): TaskSnapshot[] {
  if (!Array.isArray(payload)) {
    throw invalidResponse("Task list response must be a JSON array.");
  }
  return payload.map(parseTaskSnapshot);
}

function parseTaskSnapshot(payload: unknown): TaskSnapshot {
  if (!isRecord(payload)
    || typeof payload.taskId !== "string"
    || !isTaskOperation(payload.operation)
    || !isTaskStatus(payload.status)
    || !Object.hasOwn(payload, "result")
    || (payload.result !== null && payload.result === undefined)
    || !Object.hasOwn(payload, "error")
    || (payload.error !== null && !isProtocolError(payload.error))
    || !isDateString(payload.createdAt)
    || !isDateString(payload.updatedAt)) {
    throw invalidResponse("Task response did not match the supported protocol.");
  }
  return {
    taskId: payload.taskId,
    operation: payload.operation,
    status: payload.status,
    result: payload.result,
    error: payload.error === null ? null : { code: payload.error.code, message: payload.error.message },
    createdAt: payload.createdAt,
    updatedAt: payload.updatedAt,
  };
}

function parseProtocolError(payload: unknown): ProtocolError | undefined {
  if (!isRecord(payload) || typeof payload.code !== "string" || typeof payload.message !== "string") {
    return undefined;
  }
  return { code: payload.code, message: payload.message };
}

function isProtocolError(value: unknown): value is ProtocolError {
  return parseProtocolError(value) !== undefined;
}

function parseAgentEvent(payload: unknown):
  | { type: "authenticated"; sequence: number }
  | TaskEvent {
  if (!isRecord(payload) || !isSequence(payload.sequence)) {
    throw invalidResponse("Event did not match the supported protocol.");
  }
  if (payload.type === "authenticated") {
    return { type: "authenticated", sequence: payload.sequence };
  }
  if (payload.type === "task") {
    return { type: "task", sequence: payload.sequence, task: parseTaskSnapshot(payload.task) };
  }
  throw invalidResponse("Event type is not supported.");
}

function isTaskOperation(value: unknown): value is TaskOperation {
  return value === "template"
    || value === "upload"
    || value === "scan"
    || value === "recognize"
    || value === "score"
    || value === "review"
    || value === "export";
}

function isTaskStatus(value: unknown): value is TaskStatus {
  return value === "queued"
    || value === "running"
    || value === "completed"
    || value === "failed"
    || value === "cancelled";
}

function isSequence(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0;
}

function isDateString(value: unknown): value is string {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function isInteger(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value);
}

function validateTemplateParameters(parameters: TemplateTaskParameters): void {
  requireNonEmptyString(parameters.title, "title");
  requirePositiveInteger(parameters.questionCount, "questionCount");
  requirePositiveInteger(parameters.optionsPerQuestion, "optionsPerQuestion");
}

function validateScanParameters(parameters: ScanTaskParameters): void {
  requireNonEmptyString(parameters.templateId, "templateId");
  requireNonEmptyString(parameters.deviceId, "deviceId");
  requirePositiveInteger(parameters.dpi, "dpi");
}

function validateReviewParameters(parameters: ReviewTaskParameters): void {
  requireNonEmptyString(parameters.resultId, "resultId");
  if (!Number.isInteger(parameters.expectedVersion)
    || parameters.expectedVersion < 0
    || parameters.expectedVersion > 2_147_483_647) {
    throw invalidArgument("expectedVersion must be a non-negative 32-bit integer.");
  }
  requireNonEmptyString(parameters.reviewer, "reviewer");
  if (!Array.isArray(parameters.edits)) {
    throw invalidArgument("edits must be an array.");
  }
  for (const edit of parameters.edits) {
    if (!isRecord(edit)) {
      throw invalidArgument("Each review edit must include a positive question number, answer, and reason.");
    }
    const questionNumber = edit.questionNumber;
    if (typeof questionNumber !== "number"
      || !Number.isSafeInteger(questionNumber)
      || questionNumber <= 0
      || !(typeof edit.answer === "string" || edit.answer === null)
      || typeof edit.reason !== "string"
      || (edit.timestampUtc !== undefined && !isDateString(edit.timestampUtc))) {
      throw invalidArgument("Each review edit must include a positive question number, answer, and reason.");
    }
  }
  if (parameters.answerKey !== undefined) {
    validateAnswerKey(parameters.answerKey, "answerKey");
  }
}

function validateAnswerKey(answerKey: AnswerKey, label: string): void {
  if (!isRecord(answerKey)
    || !Object.values(answerKey).every((answer: unknown) => typeof answer === "string")) {
    throw invalidArgument(`${label} must map question numbers to answer strings.`);
  }
}

function validateFileName(fileName: string): void {
  requireNonEmptyString(fileName, "fileName");
  if (fileName === "."
    || fileName === ".."
    || /[\\/:\u0000-\u001f\u007f]/.test(fileName)) {
    throw invalidArgument("fileName must be a name without a local path.");
  }
}

function getImageByteLength(body: Blob | ArrayBuffer | Uint8Array): number {
  if (typeof Blob !== "undefined" && body instanceof Blob) {
    return body.size;
  }
  if (body instanceof ArrayBuffer) {
    return body.byteLength;
  }
  if (body instanceof Uint8Array) {
    return body.byteLength;
  }
  throw invalidArgument("Image body must be a Blob, ArrayBuffer, or Uint8Array.");
}

function requireNonEmptyString(value: string, label: string): void {
  if (typeof value !== "string" || value.trim().length === 0) {
    throw invalidArgument(`${label} must be a non-empty string.`);
  }
}

function requirePositiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw invalidArgument(`${label} must be a positive integer.`);
  }
}

function validateIdempotencyKey(value: string): void {
  requireNonEmptyString(value, "idempotencyKey");
  if (/[\u0000-\u001f\u007f]/.test(value)) {
    throw invalidArgument("idempotencyKey contains invalid header characters.");
  }
}

function encodeTaskId(taskId: string): string {
  requireNonEmptyString(taskId, "taskId");
  if (!/^[A-Za-z0-9._~-]+$/.test(taskId) || taskId === "." || taskId === "..") {
    throw invalidArgument("taskId must be a URL-safe identifier.");
  }
  return encodeURIComponent(taskId);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function invalidArgument(message: string, cause?: unknown): OmrinaSdkError {
  return new OmrinaSdkError("INVALID_ARGUMENT", message, { cause });
}

function invalidResponse(message: string): OmrinaSdkError {
  return new OmrinaSdkError("INVALID_RESPONSE", message);
}
