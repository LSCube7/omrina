/** OMRINA M0 本地服务默认地址。 */
export const DEFAULT_ENDPOINT = "http://127.0.0.1:17843";
export const DEFAULT_TIMEOUT_MS = 5_000;
export const SERVICE_NAME = "omrina-local";
const MAX_TIMEOUT_MS = 2_147_483_647;

export type HealthStatus = {
  service: typeof SERVICE_NAME;
  protocolVersion: 1;
  status: "ready";
};

export type HealthErrorCode =
  | "INVALID_ENDPOINT"
  | "INVALID_TIMEOUT"
  | "TIMEOUT"
  | "ABORTED"
  | "NETWORK_ERROR"
  | "HTTP_ERROR"
  | "INVALID_RESPONSE";

export class OmrinaSdkError extends Error {
  readonly code: HealthErrorCode;
  readonly status?: number;

  constructor(code: HealthErrorCode, message: string, options: { cause?: unknown; status?: number } = {}) {
    super(message, { cause: options.cause });
    this.name = "OmrinaSdkError";
    this.code = code;
    this.status = options.status;
  }
}

export type HealthResponse = {
  ok: boolean;
  status: number;
  json(): Promise<unknown>;
};

export type HealthFetch = (
  input: string,
  init: { method: "GET"; signal: AbortSignal; redirect: "error"; credentials: "omit" },
) => Promise<HealthResponse>;

export type CheckHealthOptions = {
  /** 本地服务的根地址；仅允许回环 HTTP 地址。 */
  endpoint?: string | URL;
  /** 单次请求的最长等待时间，单位为毫秒。 */
  timeoutMs?: number;
  /** 调用方的取消信号。 */
  signal?: AbortSignal;
  /** 测试或宿主环境可注入的 fetch 实现。 */
  fetch?: HealthFetch;
};

/**
 * 请求本地 OMRINA 服务的固定 M0 健康检查接口。
 *
 * 此客户端只接受 HTTP 回环地址，且始终访问 `/health`，避免 SDK 被用作通用网络请求器。
 */
export async function checkHealth(options: CheckHealthOptions = {}): Promise<HealthStatus> {
  const healthUrl = createHealthUrl(options.endpoint ?? DEFAULT_ENDPOINT);
  const timeoutMs = validateTimeout(options.timeoutMs ?? DEFAULT_TIMEOUT_MS);
  const fetchImplementation = options.fetch ?? getGlobalFetch();

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
    controller.abort(new Error("Health check timed out"));
  }, timeoutMs);

  try {
    const response = await fetchImplementation(healthUrl, {
      method: "GET",
      signal: controller.signal,
      redirect: "error",
      credentials: "omit",
    });

    throwIfAborted(timedOut, options.signal);

    if (!response.ok) {
      throw new OmrinaSdkError(
        "HTTP_ERROR",
        `Health check returned HTTP ${response.status}.`,
        { status: response.status },
      );
    }

    let payload: unknown;
    try {
      payload = await response.json();
    } catch (cause) {
      const abortError = getAbortError(timedOut, options.signal, cause);
      if (abortError) {
        throw abortError;
      }
      throw new OmrinaSdkError("INVALID_RESPONSE", "Health check did not return JSON.", { cause });
    }

    throwIfAborted(timedOut, options.signal);
    return parseHealthStatus(payload);
  } catch (cause) {
    if (cause instanceof OmrinaSdkError) {
      throw cause;
    }

    const abortError = getAbortError(timedOut, options.signal, cause);
    if (abortError) {
      throw abortError;
    }

    throw new OmrinaSdkError("NETWORK_ERROR", "Health check request failed.", { cause });
  } finally {
    clearTimeout(timeout);
    options.signal?.removeEventListener("abort", abortForCaller);
  }
}

function getGlobalFetch(): HealthFetch {
  if (typeof globalThis.fetch !== "function") {
    throw new OmrinaSdkError("NETWORK_ERROR", "No fetch implementation is available.");
  }

  return globalThis.fetch.bind(globalThis) as HealthFetch;
}

function createHealthUrl(endpoint: string | URL): string {
  let url: URL;
  try {
    url = new URL(endpoint);
  } catch (cause) {
    throw new OmrinaSdkError("INVALID_ENDPOINT", "Endpoint must be an absolute HTTP URL.", { cause });
  }

  if (url.protocol !== "http:" || !isLoopbackHost(url.hostname) || url.username || url.password) {
    throw new OmrinaSdkError(
      "INVALID_ENDPOINT",
      "Endpoint must be an unauthenticated HTTP URL on a loopback host.",
    );
  }

  url.pathname = "/health";
  url.search = "";
  url.hash = "";
  return url.toString();
}

function isLoopbackHost(hostname: string): boolean {
  const host = hostname.toLowerCase().replace(/^\[|\]$/g, "");
  if (host === "localhost" || host === "::1") {
    return true;
  }

  const octets = host.split(".");
  return octets.length === 4
    && octets[0] === "127"
    && octets.every((octet) => /^\d{1,3}$/.test(octet) && Number(octet) <= 255);
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

function throwIfAborted(timedOut: boolean, callerSignal: AbortSignal | undefined): void {
  const abortError = getAbortError(timedOut, callerSignal);
  if (abortError) {
    throw abortError;
  }
}

function getAbortError(
  timedOut: boolean,
  callerSignal: AbortSignal | undefined,
  cause?: unknown,
): OmrinaSdkError | undefined {
  if (timedOut) {
    return new OmrinaSdkError("TIMEOUT", "Health check timed out.", { cause });
  }

  if (callerSignal?.aborted) {
    return new OmrinaSdkError("ABORTED", "Health check was cancelled.", { cause });
  }

  return undefined;
}

function parseHealthStatus(payload: unknown): HealthStatus {
  if (!isRecord(payload)
    || payload.service !== SERVICE_NAME
    || payload.protocolVersion !== 1
    || payload.status !== "ready") {
    throw new OmrinaSdkError(
      "INVALID_RESPONSE",
      "Health check returned an unsupported service, protocol version, or status.",
    );
  }

  return {
    service: SERVICE_NAME,
    protocolVersion: 1,
    status: "ready",
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
