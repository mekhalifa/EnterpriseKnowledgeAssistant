import { HttpErrorResponse } from '@angular/common/http';

/** An RFC 9457 problem document as the API sends it, with its stable `code` extension. */
export interface Problem {
  readonly status?: number | null;
  readonly title?: string | null;
  readonly detail?: string | null;
  readonly code?: string;
  readonly traceId?: string;
  readonly errors?: Record<string, string[]>;
}

/** What the UI shows: a sentence a person can act on, and whether trying again can help. */
export interface UserFacingError {
  readonly message: string;
  readonly retryable: boolean;
  /** Seconds to wait, from Retry-After, when the server said. */
  readonly retryAfterSeconds?: number;
  /** For a support request: ties this message to the server's log line. */
  readonly traceId?: string;
  /** 401: the session is gone, and the fix is signing in again, not retrying. */
  readonly signInRequired?: boolean;
}

/**
 * Turns any failure (an HTTP error, a stream `error` event, a network failure) into one message.
 *
 * Decisions key on the stable `code` and the status, never on the English in `detail` (ADR-0011:
 * the code is the contract). Server wording is shown only for validation, where it names the
 * field to fix; elsewhere it may describe internals, and a person needs to know what to do next.
 */
export function describeProblem(status: number, problem: Problem | null, retryAfter?: string | null): UserFacingError {
  const code = problem?.code;
  const traceId = problem?.traceId;
  const retryAfterSeconds = parseRetryAfter(retryAfter);

  if (status === 0) {
    return { message: 'The service could not be reached. Check your connection and try again.', retryable: true };
  }
  if (status === 401) {
    return { message: 'Your session has ended. Sign in again to continue.', retryable: false, signInRequired: true };
  }
  if (status === 403) {
    return { message: 'You do not have permission to do that.', retryable: false, traceId };
  }
  if (status === 404) {
    return { message: 'That item no longer exists, or you do not have access to it.', retryable: false, traceId };
  }
  if (status === 413) {
    return { message: 'The file is too large to upload.', retryable: false };
  }
  if (status === 429 && code === 'usage.budget_exhausted') {
    return {
      message: "Your organization has used this month's AI allowance. Contact an administrator.",
      retryable: false,
      traceId,
    };
  }
  if (status === 429) {
    const wait = retryAfterSeconds ? ` Try again in ${retryAfterSeconds} second${retryAfterSeconds === 1 ? '' : 's'}.` : ' Try again shortly.';
    return { message: `You are sending requests too quickly.${wait}`, retryable: true, retryAfterSeconds, traceId };
  }
  if (status === 400) {
    const first = problem?.errors ? Object.values(problem.errors).flat()[0] : undefined;
    return { message: first ?? problem?.detail ?? 'The request was not valid.', retryable: false, traceId };
  }
  if (status === 409) {
    return { message: problem?.detail ?? 'That conflicts with the current state. Refresh and try again.', retryable: false, traceId };
  }
  if (status === 503 && code === 'database.unavailable') {
    // Not "nothing was saved": a write can fail after the database committed it, so the client
    // cannot promise that. Retrying a read is always safe; a repeated upload is idempotent.
    return { message: 'The service is temporarily unavailable. Try again in a moment.', retryable: true, traceId };
  }
  if (status === 502 || status === 503 || status === 504) {
    return {
      message: 'The AI service is not responding right now. Nothing was saved; try again in a moment.',
      retryable: true,
      traceId,
    };
  }
  return { message: 'Something went wrong on our side. Try again; if it keeps happening, quote the reference below.', retryable: true, traceId };
}

/** The same, from an Angular HttpClient failure. */
export function describeHttpError(error: unknown): UserFacingError {
  if (error instanceof HttpErrorResponse) {
    const problem = typeof error.error === 'object' && error.error !== null ? (error.error as Problem) : null;
    return describeProblem(error.status, problem, error.headers?.get('Retry-After'));
  }
  return describeProblem(0, null);
}

function parseRetryAfter(value: string | null | undefined): number | undefined {
  if (!value) {
    return undefined;
  }
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds >= 0 ? Math.ceil(seconds) : undefined;
}
