import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  ConversationResponse,
  ConversationSummaryResponse,
  ConversationsApi,
  MessageResponse,
} from '../../core/api/api';
import { UserFacingError, describeHttpError } from '../../core/errors/problem';

/** Whether an answer used the documents. Derived from what is persisted, so it survives a reload. */
export type Grounding = 'cited' | 'uncited' | 'no_evidence';

export const NO_EVIDENCE_MODEL = 'none:no-evidence';

export function groundingOf(message: MessageResponse): Grounding {
  if (message.model === NO_EVIDENCE_MODEL) {
    return 'no_evidence';
  }
  return message.citations.length > 0 ? 'cited' : 'uncited';
}

/** A question being answered right now: shown, but not yet persisted (and never, if it fails). */
export interface PendingTurn {
  readonly question: string;
  readonly answer: string;
}

/** Which citation the reader is looking at: of which answer, and which number. */
export interface CitationSelection {
  readonly messageId: string;
  readonly number: number | null;
}

/**
 * The chat page's state and the one place an answer stream is driven (Step 13).
 *
 * The rule it keeps is the server's: a turn exists only once it has been answered and saved
 * (step-06). While streaming, the question and partial answer live in `pending`, outside the
 * persisted messages; `completed` replaces them with what the server saved; any failure discards
 * them and hands the question back so nothing the person typed is lost.
 */
@Injectable()
export class ConversationStore {
  private readonly api = inject(ConversationsApi);
  private controller: AbortController | null = null;

  readonly conversations = signal<ConversationSummaryResponse[]>([]);
  readonly current = signal<ConversationResponse | null>(null);
  readonly pending = signal<PendingTurn | null>(null);
  readonly error = signal<UserFacingError | null>(null);
  readonly loading = signal(false);
  readonly selection = signal<CitationSelection | null>(null);
  /** Polite announcement for screen readers; set once per finished answer, never per delta. */
  readonly announcement = signal('');

  readonly streaming = computed(() => this.pending() !== null);
  readonly messages = computed(() => this.current()?.messages ?? []);

  /** The answer whose citations the panel shows: the selected one, else the latest answer. */
  readonly citedMessage = computed<MessageResponse | null>(() => {
    const messages = this.messages();
    const selected = this.selection();
    if (selected) {
      return messages.find((m) => m.id === selected.messageId) ?? null;
    }
    return [...messages].reverse().find((m) => m.role === 'Assistant') ?? null;
  });

  async loadConversations(): Promise<void> {
    try {
      this.conversations.set(await firstValueFrom(this.api.list()));
    } catch (error) {
      this.error.set(describeHttpError(error));
    }
  }

  async open(id: string | null): Promise<void> {
    if (id === (this.current()?.id ?? null)) {
      return; // already showing it, e.g. the URL caught up with a conversation just started here
    }
    this.stop();
    this.selection.set(null);
    this.error.set(null);
    if (id === null) {
      this.current.set(null);
      return;
    }
    this.loading.set(true);
    try {
      this.current.set(await firstValueFrom(this.api.get(id)));
    } catch (error) {
      this.current.set(null);
      this.error.set(describeHttpError(error));
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * Asks in the current conversation, starting one first if there is none.
   * @returns the id of the conversation it was asked in, and whether an answer was saved. On
   *   failure the caller puts the question back in the composer.
   */
  async ask(question: string): Promise<{ conversationId: string | null; answered: boolean }> {
    this.error.set(null);
    this.selection.set(null);

    let conversation = this.current();
    if (!conversation) {
      try {
        conversation = await firstValueFrom(this.api.start());
        this.current.set(conversation);
      } catch (error) {
        this.error.set(describeHttpError(error));
        return { conversationId: null, answered: false };
      }
    }

    const controller = new AbortController();
    this.controller = controller;
    this.pending.set({ question, answer: '' });

    try {
      for await (const event of this.api.askStreaming(conversation.id, question, controller.signal)) {
        if (event.type === 'delta') {
          this.pending.update((turn) => (turn ? { ...turn, answer: turn.answer + event.text } : turn));
        } else if (event.type === 'completed') {
          const exchange = event.exchange;
          this.current.update((c) =>
            c ? { ...c, title: exchange.title, messages: [...c.messages, exchange.question, exchange.answer] } : c,
          );
          this.touchConversation(conversation.id, exchange.title);
          this.announcement.set(announce(exchange.answer));
          return { conversationId: conversation.id, answered: true };
        } else {
          this.error.set(event.error);
          return { conversationId: conversation.id, answered: false };
        }
      }
      // Ended without a final event: only when aborted by the person.
      return { conversationId: conversation.id, answered: false };
    } finally {
      this.pending.set(null);
      if (this.controller === controller) {
        this.controller = null;
      }
    }
  }

  /** Stops an answer being written. The server abandons the turn; nothing is saved. */
  stop(): void {
    this.controller?.abort();
    this.controller = null;
  }

  selectCitation(messageId: string, number: number | null): void {
    this.selection.set({ messageId, number });
  }

  private touchConversation(id: string, title: string | null | undefined): void {
    const now = new Date().toISOString();
    this.conversations.update((list) => {
      const existing = list.find((c) => c.id === id);
      const updated: ConversationSummaryResponse = existing
        ? { ...existing, title: title ?? existing.title, lastActivityAt: now }
        : { id, title: title ?? null, createdAt: now, lastActivityAt: now };
      return [updated, ...list.filter((c) => c.id !== id)];
    });
  }
}

function announce(answer: MessageResponse): string {
  switch (groundingOf(answer)) {
    case 'cited':
      return `Answer ready, with ${answer.citations.length} source${answer.citations.length === 1 ? '' : 's'}.`;
    case 'uncited':
      return 'Answer ready. It does not cite your documents.';
    default:
      return 'No relevant documents were found for that question.';
  }
}
