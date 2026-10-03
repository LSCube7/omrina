import { OmrinaSdkError } from "./errors.ts";
import { DEFAULT_ENDPOINT, DEFAULT_TIMEOUT_MS } from "./health.ts";

const MAX_TIMEOUT_MS = 2_147_483_647;
const MAX_JSON_BYTES = 64 * 1024;
const MAX_UPLOAD_METADATA_BYTES = 4 * 1024;
const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;
const MAX_SUBJECTIVE_IMAGE_BYTES = 8 * 1024 * 1024;
const PNG_SIGNATURE = new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]);
const INITIAL_RECONNECT_DELAY_MS = 250;
const MAX_RECONNECT_DELAY_MS = 5_000;

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export type TaskOperation =
  | "template"
  | "upload"
  | "scan"
  | "recognize"
  | "score"
  | "review"
  | "export"
  | "subjectiveCreate"
  | "subjectiveRead"
  | "subjectiveGrade"
  | "subjectiveExport";
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
  subjectiveRegions: TemplateSubjectiveRegionSummary[];
};

export type TemplateSubjectiveRegionSummary = {
  questionId: string;
  questionNumber: number;
  maxScore: number;
  rectangleMm: TemplateMillimetreRectangle;
};

export type TemplateMillimetreRectangle = {
  x: number;
  y: number;
  width: number;
  height: number;
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
  subjectiveRegions?: TemplateSubjectiveRegionInput[];
};

export type TemplateSubjectiveRegionInput = {
  questionNumber: number;
  maxScore: number;
  rectangleMm: TemplateMillimetreRectangle;
};

export type SchoolQuestionDefinition = {
  number: number;
  type: "Choice" | "Subjective";
  maximumScore?: number;
  body?: string;
  options?: string[] | null;
  subjectiveHeightMm?: number;
};

export type SchoolQuestionGroupDefinition = {
  id: string;
  title: string;
  questionNumbers: number[];
};

export type SchoolLayoutOrder = "Mixed" | "Separated";

export type SchoolSheetDefinition = {
  examId: string;
  layoutDocumentId: string;
  version?: number;
  title?: string;
  paper?: "A4Portrait" | "A3Landscape";
  mode?: "AnswerOnly" | "WithQuestions";
  columns?: number;
  bubbleShape?: "Circle" | "Rectangle";
  labelPlacement?: "Inside" | "Outside";
  bubbleWidthMm?: number;
  bubbleHeightMm?: number;
  candidateIdentity?: { mode?: "Barcode" | "Marking"; digits?: number; candidateId?: string | null };
  duplex?: boolean;
  repeatBackIdentity?: boolean;
  groups?: SchoolQuestionGroupDefinition[];
  layoutOrder?: SchoolLayoutOrder;
  questions: SchoolQuestionDefinition[];
};

export type SchoolQuestionGroupGeometry = {
  groupId: string;
  title: string;
  questionNumbers: number[];
  rectangleMm: TemplateMillimetreRectangle;
};

export type SchoolTemplateTaskParameters = { schoolDefinition: SchoolSheetDefinition };
export type SchoolPageMetadata = {
  examId: string;
  layoutDocumentId: string;
  version: number;
  pageNumber: number;
  side: "Front" | "Back";
  templateId: string;
};
export type SchoolTemplatePage = TemplateSummary & {
  schemaVersion: 3;
  paper: "A4Portrait" | "A3Landscape";
  side: "Front" | "Back";
  pageIndex: number;
  widthMm: number;
  heightMm: number;
  schoolMetadata: SchoolPageMetadata;
  schoolGroups: SchoolQuestionGroupGeometry[];
};
export type SchoolTemplateDocument = { documentId: string; examId: string; version: number; pages: SchoolTemplatePage[] };
export type SchoolIdentityStatus = "Identified" | "RequireAssociation";
export type SchoolCaptureSummary = {
  captureId: string;
  templateId: string;
  createdAt: string;
  sourceType: "import" | "scan";
  imageWidth: number;
  imageHeight: number;
  byteLength: number;
  schoolMetadata: SchoolPageMetadata;
  candidateId: string | null;
  identityStatus: SchoolIdentityStatus;
};
export type SchoolScanCaptureSummary = SchoolCaptureSummary & {
  dpi: number;
  pageSize: "A4" | "A3";
  flatbed: boolean;
};
export type SchoolRecognitionTaskResult = {
  resultId: string;
  version: number;
  schoolMetadata: SchoolPageMetadata;
  candidateId: string | null;
  identityStatus: SchoolIdentityStatus;
  result: unknown;
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

export type SubjectivePixelRegion = {
  x: number;
  y: number;
  width: number;
  height: number;
};

export type SubjectiveCreateTaskParameters = {
  captureId: string;
};

export type SubjectiveGradeStatus = "draft" | "confirmed" | "ungraded";

export type SubjectiveGradeEdit = {
  questionId: string;
  status: SubjectiveGradeStatus;
  score: number | null;
  comment: string;
};

export type SubjectiveGradeTaskParameters = {
  reviewId: string;
  expectedVersion: number;
  reviewer: string;
  edits: SubjectiveGradeEdit[];
};

export type SubjectiveQuestionStatus = "ungraded" | "draft" | "confirmed";

export type SubjectiveQuestion = {
  questionId: string;
  questionNumber: number;
  maxScore: number;
  region: SubjectivePixelRegion;
  status: SubjectiveQuestionStatus;
  score: number | null;
  comment: string | null;
  reviewer: string | null;
  confirmedAtUtc: string | null;
};

export type SubjectiveGradeState = {
  status: SubjectiveQuestionStatus;
  score: number | null;
  comment: string | null;
  reviewer: string | null;
  confirmedAtUtc: string | null;
};

export type SubjectiveGradeChange = {
  questionId: string;
  questionNumber: number;
  action: "setDraft" | "confirm" | "reset";
  before: SubjectiveGradeState;
  after: SubjectiveGradeState;
};

export type SubjectiveGradeHistoryBatch = {
  version: number;
  reviewer: string;
  timestampUtc: string;
  changes: SubjectiveGradeChange[];
};

export type SubjectiveReviewDocument = {
  reviewId: string;
  captureId: string;
  version: number;
  questions: SubjectiveQuestion[];
  history: SubjectiveGradeHistoryBatch[];
  createdAtUtc: string;
  updatedAtUtc: string;
  isFinal: boolean;
  finalSubtotal: number | null;
};

export type SubjectiveReadTaskParameters = {
  reviewId: string;
};

export type SubjectiveExportTaskParameters = {
  reviewId: string;
  format: "json" | "csv";
};

export type SubjectiveExportResult = {
  format: "json" | "csv";
  content: string;
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
  arrayBuffer?(): Promise<ArrayBuffer>;
  body?: ReadableStream<Uint8Array> | null;
  headers?: Pick<Headers, "get">;
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

  createSchoolTemplateTask(
    request: TaskSubmission<SchoolTemplateTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateSchoolDefinition(request.parameters.schoolDefinition);
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

  createSubjectiveReviewTask(
    request: TaskSubmission<SubjectiveCreateTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateSubjectiveCreateParameters(request.parameters);
    return this.#createJsonTask("subjectiveCreate", request, options);
  }

  createSubjectiveReadTask(
    request: TaskSubmission<SubjectiveReadTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateGuid(request.parameters.reviewId, "reviewId");
    return this.#createJsonTask("subjectiveRead", request, options);
  }

  createSubjectiveGradeTask(
    request: TaskSubmission<SubjectiveGradeTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateSubjectiveGradeParameters(request.parameters);
    return this.#createJsonTask("subjectiveGrade", request, options);
  }

  createSubjectiveExportTask(
    request: TaskSubmission<SubjectiveExportTaskParameters>,
    options: RequestOptions = {},
  ): Promise<TaskSnapshot> {
    validateGuid(request.parameters.reviewId, "reviewId");
    if (request.parameters.format !== "json" && request.parameters.format !== "csv") {
      throw invalidArgument("format must be json or csv.");
    }
    return this.#createJsonTask("subjectiveExport", request, options);
  }

  getSubjectiveQuestionImage(
    reviewId: string,
    questionId: string,
    options: RequestOptions = {},
  ): Promise<Uint8Array> {
    validateGuid(reviewId, "reviewId");
    validateGuid(questionId, "questionId");
    const path = `/v1/subjective-reviews/${encodeURIComponent(reviewId)}/questions/${encodeURIComponent(questionId)}/image`;
    return this.#performRequest(path, "GET", options, { authenticated: true, expectedStatus: 200 }, async (response, timedOut, signal) => {
      if (response.headers) {
        const contentType = response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
        if (contentType !== "image/png") {
          throw invalidResponse("Subjective image response must be PNG.");
        }
      } else {
        throw invalidResponse("Subjective image response is missing its content type.");
      }
      let imageBytes: Uint8Array;
      try {
        imageBytes = await readBoundedSubjectiveImage(response, timedOut, signal);
      } catch (cause) {
        const abortError = getRequestAbortError(timedOut(), signal, cause);
        if (abortError) {
          throw abortError;
        }
        if (cause instanceof OmrinaSdkError) {
          throw cause;
        }
        throw new OmrinaSdkError("INVALID_RESPONSE", "The local agent returned an unreadable image body.", { cause });
      }
      throwIfRequestAborted(timedOut(), signal);
      if (imageBytes.byteLength === 0) {
        throw invalidResponse("Subjective image response size is outside the supported range.");
      }
      if (imageBytes.byteLength < PNG_SIGNATURE.byteLength
        || PNG_SIGNATURE.some((byte, index) => imageBytes[index] !== byte)) {
        throw invalidResponse("Subjective image response does not contain a PNG image.");
      }
      return imageBytes;
    });
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
        if (response.status === 401 || response.status === 403) {
          this.#grant = undefined;
        }
        const error = await awaitWithRequestSignal(
          parseHttpError(response),
          () => timedOut,
          controller.signal,
        );
        throwIfRequestAborted(timedOut, controller.signal);
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

      return await consume(response, () => timedOut, controller.signal);
    } catch (cause) {
      if (cause instanceof OmrinaSdkError) {
        throw cause;
      }

      const abortError = getRequestAbortError(timedOut, controller.signal, cause);
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

function awaitWithRequestSignal<T>(
  operation: Promise<T>,
  timedOut: () => boolean,
  signal?: AbortSignal,
): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    if (signal?.aborted) {
      void operation.catch(() => undefined);
      reject(getRequestAbortError(timedOut(), signal));
      return;
    }

    let settled = false;
    const abort = () => settle(() => reject(getRequestAbortError(timedOut(), signal)));
    const cleanup = () => signal?.removeEventListener("abort", abort);
    const settle = (action: () => void) => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();
      action();
    };

    signal?.addEventListener("abort", abort, { once: true });
    operation.then(
      (value) => settle(() => resolve(value)),
      (cause: unknown) => settle(() => reject(cause)),
    );
  });
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
    || typeof payload.svg !== "string"
    || (payload.subjectiveRegions !== undefined && !Array.isArray(payload.subjectiveRegions))) {
    throw invalidResponse("Template entry did not match the supported protocol.");
  }
  const subjectiveRegions = payload.subjectiveRegions === undefined
    ? []
    : payload.subjectiveRegions.map(parseTemplateSubjectiveRegionSummary);
  return {
    templateId: payload.templateId,
    schemaVersion: payload.schemaVersion,
    title: payload.title,
    questionCount: payload.questionCount,
    optionsPerQuestion: payload.optionsPerQuestion,
    templateNumber: payload.templateNumber,
    svg: payload.svg,
    subjectiveRegions,
  };
}

function parseTemplateSubjectiveRegionSummary(payload: unknown): TemplateSubjectiveRegionSummary {
  if (!isRecord(payload)
    || typeof payload.questionId !== "string"
    || !isPositiveInt32(payload.questionNumber)
    || typeof payload.maxScore !== "number"
    || !Number.isFinite(payload.maxScore)
    || payload.maxScore <= 0
    || payload.maxScore > 1_000_000
    || !isRecord(payload.rectangleMm)
    || !isFiniteNonNegativeNumber(payload.rectangleMm.x)
    || !isFiniteNonNegativeNumber(payload.rectangleMm.y)
    || !isFinitePositiveNumber(payload.rectangleMm.width)
    || !isFinitePositiveNumber(payload.rectangleMm.height)
    || payload.rectangleMm.x + payload.rectangleMm.width > 210
    || payload.rectangleMm.y + payload.rectangleMm.height > 297) {
    throw invalidResponse("Template subjective region did not match the supported protocol.");
  }
  return {
    questionId: payload.questionId,
    questionNumber: payload.questionNumber,
    maxScore: payload.maxScore,
    rectangleMm: {
      x: payload.rectangleMm.x,
      y: payload.rectangleMm.y,
      width: payload.rectangleMm.width,
      height: payload.rectangleMm.height,
    },
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
    || value === "export"
    || value === "subjectiveCreate"
    || value === "subjectiveRead"
    || value === "subjectiveGrade"
    || value === "subjectiveExport";
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
  if (parameters.subjectiveRegions === undefined) {
    return;
  }
  if (!Array.isArray(parameters.subjectiveRegions) || parameters.subjectiveRegions.length < 1 || parameters.subjectiveRegions.length > 64) {
    throw invalidArgument("subjectiveRegions must contain between 1 and 64 template regions when provided.");
  }
  const questionNumbers = new Set<number>();
  for (const region of parameters.subjectiveRegions) {
    if (!isRecord(region)
      || !isPositiveInt32(region.questionNumber)
      || region.questionNumber > 2_147_483_647
      || questionNumbers.has(region.questionNumber)
      || typeof region.maxScore !== "number"
      || !Number.isFinite(region.maxScore)
      || region.maxScore <= 0
      || region.maxScore > 1_000_000
      || !isRecord(region.rectangleMm)
      || !isFiniteNonNegativeNumber(region.rectangleMm.x)
      || !isFiniteNonNegativeNumber(region.rectangleMm.y)
      || !isFinitePositiveNumber(region.rectangleMm.width)
      || !isFinitePositiveNumber(region.rectangleMm.height)
      || region.rectangleMm.x + region.rectangleMm.width > 210
      || region.rectangleMm.y + region.rectangleMm.height > 297) {
      throw invalidArgument("Each subjective template region must be a unique bounded millimetre rectangle with a positive maximum score.");
    }
    questionNumbers.add(region.questionNumber);
  }
}

function validateSchoolDefinition(definition: SchoolSheetDefinition): void {
  const fail = (): never => { throw invalidArgument("School answer-sheet definition is invalid."); };
  const keys = (value: unknown, allowed: string[]): value is Record<string, unknown> =>
    isRecord(value) && Object.keys(value).every((key) => allowed.includes(key));
  if (!keys(definition, ["examId", "layoutDocumentId", "version", "title", "paper", "mode", "columns", "bubbleShape", "labelPlacement", "bubbleWidthMm", "bubbleHeightMm", "candidateIdentity", "duplex", "repeatBackIdentity", "groups", "layoutOrder", "questions"])) fail();
  requireNonEmptyString(definition.examId, "examId");
  requireNonEmptyString(definition.layoutDocumentId, "layoutDocumentId");
  if (definition.examId.length > 80 || definition.layoutDocumentId.length > 80) fail();
  if (definition.version !== undefined && !isPositiveInt32(definition.version)) fail();
  if (definition.title !== undefined && (typeof definition.title !== "string" || definition.title.trim().length === 0 || definition.title.length > 80)) fail();
  if (definition.paper !== undefined && !["A4Portrait", "A3Landscape"].includes(definition.paper)) fail();
  if (definition.mode !== undefined && !["AnswerOnly", "WithQuestions"].includes(definition.mode)) fail();
  if (definition.bubbleShape !== undefined && !["Circle", "Rectangle"].includes(definition.bubbleShape)) fail();
  if (definition.labelPlacement !== undefined && !["Inside", "Outside"].includes(definition.labelPlacement)) fail();
  const columns = definition.columns ?? 1;
  if ((definition.paper ?? "A4Portrait") === "A4Portrait" ? columns !== 1 : columns !== 2 && columns !== 3) fail();
  const width = definition.bubbleWidthMm ?? 1.8;
  const height = definition.bubbleHeightMm ?? 1.8;
  const isTenthMillimetre = (value: number): boolean => isFinitePositiveNumber(value)
    && Number.isSafeInteger(Math.round(value * 10))
    && Math.abs(value * 10 - Math.round(value * 10)) <= 1e-9;
  if (!isTenthMillimetre(width) || !isTenthMillimetre(height) || Math.round(height * 10) > 20) fail();
  if ((definition.bubbleShape ?? "Circle") === "Circle" && Math.round(width * 10) > 20) fail();
  if (definition.duplex !== undefined && typeof definition.duplex !== "boolean") fail();
  if (definition.repeatBackIdentity !== undefined && typeof definition.repeatBackIdentity !== "boolean") fail();
  if (definition.layoutOrder !== undefined && definition.layoutOrder !== "Mixed" && definition.layoutOrder !== "Separated") fail();
  if (definition.candidateIdentity !== undefined) {
    const identity = definition.candidateIdentity;
    if (!keys(identity, ["mode", "digits", "candidateId"])) fail();
    if (identity.mode !== undefined && !["Barcode", "Marking"].includes(identity.mode)) fail();
    const digits = identity.digits ?? 8;
    if (!isPositiveInt32(digits) || digits > 20) fail();
    if (identity.candidateId != null && (typeof identity.candidateId !== "string" || !/^\d+$/.test(identity.candidateId) || identity.candidateId.length !== digits)) fail();
  }
  if (!Array.isArray(definition.questions) || definition.questions.length < 1 || definition.questions.length > 500) fail();
  const numbers = new Set<number>();
  const questionTypes = new Map<number, SchoolQuestionDefinition["type"]>();
  for (const question of definition.questions) {
    if (!keys(question, ["number", "type", "maximumScore", "body", "options", "subjectiveHeightMm"]) || !isPositiveInt32(question.number) || numbers.has(question.number) || !["Choice", "Subjective"].includes(question.type)) fail();
    numbers.add(question.number);
    questionTypes.set(question.number, question.type);
    if (question.maximumScore !== undefined && (!isFinitePositiveNumber(question.maximumScore) || question.maximumScore > 1_000_000)) fail();
    if (question.body !== undefined && (typeof question.body !== "string" || question.body.length > 8000)) fail();
    if (question.type === "Choice" && (!Array.isArray(question.options) || question.options.length < 2 || question.options.length > 6 || question.options.some((option) => typeof option !== "string" || option.length > 1000))) fail();
    if (question.subjectiveHeightMm !== undefined && (!Number.isFinite(question.subjectiveHeightMm) || question.subjectiveHeightMm < 10 || question.subjectiveHeightMm > 230)) fail();
  }

  if (definition.groups === undefined || (Array.isArray(definition.groups) && definition.groups.length === 0)) return;
  if (!Array.isArray(definition.groups) || definition.groups.length > definition.questions.length) fail();
  const groupIds = new Set<string>();
  const groupedQuestionNumbers = new Set<number>();
  for (const group of definition.groups) {
    if (!keys(group, ["id", "title", "questionNumbers"])
      || typeof group.id !== "string"
      || group.id.length < 1
      || group.id.length > 80
      || !/^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$/.test(group.id)
      || groupIds.has(group.id)
      || typeof group.title !== "string"
      || group.title.trim().length === 0
      || group.title.length > 80
      || /[\r\n\t]/.test(group.title)
      || !isXml10String(group.title)
      || !Array.isArray(group.questionNumbers)
      || group.questionNumbers.length < 1
      || group.questionNumbers.length > numbers.size) fail();
    groupIds.add(group.id);

    let groupQuestionType: SchoolQuestionDefinition["type"] | undefined;
    for (const questionNumber of group.questionNumbers) {
      if (!isPositiveInt32(questionNumber)
        || !numbers.has(questionNumber)
        || groupedQuestionNumbers.has(questionNumber)) fail();
      const questionType = questionTypes.get(questionNumber)!;
      if (groupQuestionType !== undefined && groupQuestionType !== questionType) fail();
      groupQuestionType = questionType;
      groupedQuestionNumbers.add(questionNumber);
    }
  }
  if (groupedQuestionNumbers.size !== numbers.size) fail();
}

function isXml10String(value: string): boolean {
  for (const character of value) {
    const codePoint = character.codePointAt(0)!;
    if (codePoint !== 0x9 && codePoint !== 0xa && codePoint !== 0xd
      && !(codePoint >= 0x20 && codePoint <= 0xd7ff)
      && !(codePoint >= 0xe000 && codePoint <= 0xfffd)
      && !(codePoint >= 0x10000 && codePoint <= 0x10ffff)) {
      return false;
    }
  }
  return true;
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

function validateSubjectiveCreateParameters(parameters: SubjectiveCreateTaskParameters): void {
  if (!isRecord(parameters)
    || Object.keys(parameters).some((key) => key !== "captureId")) {
    throw invalidArgument("subjectiveCreate accepts only the captureId; regions come from its template.");
  }
  requireNonEmptyString(parameters.captureId, "captureId");
}

function isFiniteNonNegativeNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value >= 0;
}

function isFinitePositiveNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

function isPositiveInt32(value: unknown): value is number {
  return isInteger(value) && value > 0 && value <= 2_147_483_647;
}

function validateSubjectiveGradeParameters(parameters: SubjectiveGradeTaskParameters): void {
  validateGuid(parameters.reviewId, "reviewId");
  if (!Number.isSafeInteger(parameters.expectedVersion) || parameters.expectedVersion < 0) {
    throw invalidArgument("expectedVersion must be a non-negative safe integer.");
  }
  requireNonEmptyString(parameters.reviewer, "reviewer");
  if (parameters.reviewer.length > 64) {
    throw invalidArgument("reviewer must be 64 characters or fewer.");
  }
  if (!Array.isArray(parameters.edits) || parameters.edits.length < 1 || parameters.edits.length > 64) {
    throw invalidArgument("edits must contain between 1 and 64 question updates.");
  }
  const questionIds = new Set<string>();
  for (const edit of parameters.edits) {
    if (!isRecord(edit)) {
      throw invalidArgument("Each subjective grade edit must include a question ID, status, score, and comment.");
    }
    validateGuid(edit.questionId, "questionId");
    if (questionIds.has(edit.questionId)) {
      throw invalidArgument("questionId values in edits must be unique.");
    }
    questionIds.add(edit.questionId);
    if (edit.status !== "draft" && edit.status !== "confirmed" && edit.status !== "ungraded") {
      throw invalidArgument("status must be draft, confirmed, or ungraded.");
    }
    if (edit.score !== null
      && (typeof edit.score !== "number" || !Number.isFinite(edit.score) || edit.score < 0)) {
      throw invalidArgument("score must be a non-negative number or null.");
    }
    if (typeof edit.comment !== "string" || edit.comment.length > 256) {
      throw invalidArgument("comment must be 256 characters or fewer.");
    }
    if ((edit.status === "ungraded" && (edit.score !== null || edit.comment.trim() !== ""))
      || ((edit.status === "draft" || edit.status === "confirmed") && edit.score === null)) {
      throw invalidArgument("The score and comment must match the selected subjective status.");
    }
  }
}

function validateGuid(value: string, label: string): void {
  requireNonEmptyString(value, label);
  if (!/^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.test(value)) {
    throw invalidArgument(`${label} must be a GUID.`);
  }
}

async function readBoundedSubjectiveImage(
  response: SdkResponse,
  timedOut: () => boolean,
  signal?: AbortSignal,
): Promise<Uint8Array> {
  const contentLength = response.headers?.get("content-length");
  if (contentLength !== null && contentLength !== undefined && contentLength.trim() !== "") {
    const normalizedLength = contentLength.trim();
    if (!/^\d+$/.test(normalizedLength)) {
      throw invalidResponse("Subjective image response has an invalid content length.");
    }
    const declaredLength = Number(normalizedLength);
    if (!Number.isSafeInteger(declaredLength) || declaredLength > MAX_SUBJECTIVE_IMAGE_BYTES) {
      throw invalidResponse("Subjective image response size is outside the supported range.");
    }
  }

  if (response.body) {
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let totalBytes = 0;
    let mayReleaseLock = true;
    try {
      while (true) {
        throwIfRequestAborted(timedOut(), signal);
        let result: ReadableStreamReadResult<Uint8Array>;
        try {
          result = await awaitWithRequestSignal(reader.read(), timedOut, signal);
        } catch (cause) {
          if (signal?.aborted) {
            mayReleaseLock = false;
            void reader.cancel().catch(() => undefined);
          }
          throw cause;
        }
        const { done, value } = result;
        if (done) {
          break;
        }
        totalBytes += value.byteLength;
        if (totalBytes > MAX_SUBJECTIVE_IMAGE_BYTES) {
          void reader.cancel().catch(() => undefined);
          throw invalidResponse("Subjective image response size is outside the supported range.");
        }
        chunks.push(value);
      }
    } finally {
      if (mayReleaseLock) {
        reader.releaseLock();
      }
    }

    const output = new Uint8Array(totalBytes);
    let offset = 0;
    for (const chunk of chunks) {
      output.set(chunk, offset);
      offset += chunk.byteLength;
    }
    return output;
  }

  if (!response.arrayBuffer) {
    throw invalidResponse("The local agent did not return a binary image body.");
  }
  const payload = await awaitWithRequestSignal(response.arrayBuffer(), timedOut, signal);
  if (!(payload instanceof ArrayBuffer) || payload.byteLength > MAX_SUBJECTIVE_IMAGE_BYTES) {
    throw invalidResponse("Subjective image response size is outside the supported range.");
  }
  return new Uint8Array(payload);
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
