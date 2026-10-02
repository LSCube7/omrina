export type HealthErrorCode =
  | "INVALID_ENDPOINT"
  | "INVALID_TIMEOUT"
  | "TIMEOUT"
  | "ABORTED"
  | "NETWORK_ERROR"
  | "HTTP_ERROR"
  | "INVALID_RESPONSE";

export type OmrinaSdkErrorCode =
  | HealthErrorCode
  | "INVALID_ARGUMENT"
  | "MISSING_CREDENTIAL"
  | "UNAUTHORIZED"
  | "REMOTE_ERROR"
  | "EVENT_ERROR"
  | "EVENT_DISCONNECTED";

export class OmrinaSdkError extends Error {
  readonly code: OmrinaSdkErrorCode;
  readonly status: number | undefined;
  readonly remoteCode: string | undefined;

  constructor(
    code: OmrinaSdkErrorCode,
    message: string,
    options: { cause?: unknown; status?: number; remoteCode?: string } = {},
  ) {
    super(message, { cause: options.cause });
    this.name = "OmrinaSdkError";
    this.code = code;
    this.status = options.status;
    this.remoteCode = options.remoteCode;
  }
}
