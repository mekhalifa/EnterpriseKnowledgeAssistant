import { HttpClient, HttpEvent, HttpParams } from '@angular/common/http';
import { Injectable, InjectionToken, inject } from '@angular/core';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { Observable, firstValueFrom } from 'rxjs';
import { Problem, UserFacingError, describeProblem } from '../errors/problem';
import { readServerSentEvents } from '../sse/server-sent-events';
import type {
  AskQuestionResponse,
  ConversationResponse,
  ConversationSummaryResponse,
  DocumentResponse,
  PagedResponseOfDocumentSummaryResponse,
} from './schema';

export type * from './schema';

/** Where the API lives. Its own token so tests and the auth interceptor agree on one value. */
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL');

/**
 * The document endpoints. Types come from the generated `schema.ts` (openapi-typescript over the
 * checked-in OpenAPI document), so a contract change that is not regenerated fails `tsc`, and a
 * regenerated one that is not checked in fails the API's own snapshot test (ADR-0033).
 */
@Injectable({ providedIn: 'root' })
export class DocumentsApi {
  private readonly http = inject(HttpClient);
  private readonly base = `${inject(API_BASE_URL)}/api/documents`;

  list(page: number, pageSize: number): Observable<PagedResponseOfDocumentSummaryResponse> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<PagedResponseOfDocumentSummaryResponse>(this.base, { params });
  }

  get(id: string): Observable<DocumentResponse> {
    return this.http.get<DocumentResponse>(`${this.base}/${id}`);
  }

  /** Multipart upload with progress events, so the UI can show bytes sent. */
  upload(file: File, title?: string): Observable<HttpEvent<DocumentResponse>> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (title?.trim()) {
      form.append('title', title.trim());
    }
    return this.http.post<DocumentResponse>(this.base, form, { reportProgress: true, observe: 'events' });
  }

  setAccess(id: string, principals: readonly string[]): Observable<DocumentResponse> {
    return this.http.put<DocumentResponse>(`${this.base}/${id}/access`, { principals });
  }

  reset(id: string): Observable<DocumentResponse> {
    return this.http.post<DocumentResponse>(`${this.base}/${id}/reset`, null);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }
}

/** What an answer stream yields, in order: deltas, then exactly one `completed` or `error`. */
export type AnswerStreamEvent =
  | { readonly type: 'delta'; readonly text: string }
  | { readonly type: 'completed'; readonly exchange: AskQuestionResponse }
  | { readonly type: 'error'; readonly error: UserFacingError };

@Injectable({ providedIn: 'root' })
export class ConversationsApi {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(OidcSecurityService);
  private readonly base = `${inject(API_BASE_URL)}/api/conversations`;

  list(): Observable<ConversationSummaryResponse[]> {
    return this.http.get<ConversationSummaryResponse[]>(this.base);
  }

  get(id: string): Observable<ConversationResponse> {
    return this.http.get<ConversationResponse>(`${this.base}/${id}`);
  }

  start(): Observable<ConversationResponse> {
    return this.http.post<ConversationResponse>(this.base, null);
  }

  /**
   * Asks a question and yields the answer as it is written (ADR-0033).
   *
   * Failures never throw: an HTTP error before the stream (404, 429, 502…), a network failure,
   * and an in-band `error` event all arrive as one final `error` event, so the caller has a
   * single place to restore the question. Aborting `signal` stops reading and closes the request;
   * the server then abandons the turn and persists nothing.
   */
  async *askStreaming(conversationId: string, question: string, signal: AbortSignal): AsyncGenerator<AnswerStreamEvent> {
    let response: Response;
    try {
      // fetch, not HttpClient: HttpClient buffers the whole body before handing it over. The auth
      // interceptor therefore does not run, so the token is attached here, to this API URL only.
      const token = await firstValueFrom(this.auth.getAccessToken());
      response = await fetch(`${this.base}/${conversationId}/messages/stream`, {
        method: 'POST',
        headers: {
          Authorization: `Bearer ${token}`,
          'Content-Type': 'application/json',
          Accept: 'text/event-stream',
        },
        body: JSON.stringify({ question }),
        signal,
      });
    } catch (error) {
      if (signal.aborted) {
        return;
      }
      yield { type: 'error', error: describeProblem(0, null) };
      return;
    }

    if (!response.ok || !response.body) {
      const problem = await response.json().catch(() => null) as Problem | null;
      yield { type: 'error', error: describeProblem(response.status, problem, response.headers.get('Retry-After')) };
      return;
    }

    try {
      for await (const event of readServerSentEvents(response.body, signal)) {
        const data = JSON.parse(event.data);
        if (event.event === 'delta') {
          yield { type: 'delta', text: (data as { text: string }).text };
        } else if (event.event === 'completed') {
          yield { type: 'completed', exchange: data as AskQuestionResponse };
          return;
        } else if (event.event === 'error') {
          const problem = data as Problem;
          yield { type: 'error', error: describeProblem(problem.status ?? 500, problem) };
          return;
        }
      }
    } catch (error) {
      if (signal.aborted) {
        return;
      }
      yield { type: 'error', error: describeProblem(0, null) };
      return;
    }

    // The body ended without a final event: the connection broke mid-answer.
    if (!signal.aborted) {
      yield { type: 'error', error: describeProblem(0, null) };
    }
  }
}
