export type ApiErrorCode =
  | 'unauthorized'
  | 'auth_unavailable'
  | 'forbidden_scope'
  | 'forbidden_collector'
  | 'collector_not_found'
  | 'not_found'
  | 'method_not_allowed'
  | 'unsupported_media_type'
  | 'payload_too_large'
  | 'invalid_json'
  | 'invalid_snapshot'
  | 'invalid_revision'
  | 'invalid_query'
  | 'invalid_request'
  | 'result_too_large'
  | 'revision_conflict'
  | 'stale_revision'
  | 'generation_inactive'
  | 'internal_error';

export class ApiError extends Error {
  readonly status: number;
  readonly code: ApiErrorCode;
  readonly details: Record<string, unknown> | undefined;

  constructor(
    status: number,
    code: ApiErrorCode,
    message: string,
    details?: Record<string, unknown>,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

export interface ErrorBody {
  error: {
    code: ApiErrorCode;
    message: string;
    details?: Record<string, unknown>;
  };
}

export function errorBody(err: ApiError): ErrorBody {
  const body: ErrorBody = { error: { code: err.code, message: err.message } };
  if (err.details !== undefined) {
    body.error.details = err.details;
  }
  return body;
}
