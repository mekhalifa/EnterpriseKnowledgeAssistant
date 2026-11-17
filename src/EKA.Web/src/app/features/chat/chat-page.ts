import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatFormField, MatHint, MatInput, MatLabel } from '@angular/material/input';
import { MatProgressBar } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { ErrorBanner } from '../../shared/error-banner';
import { AnswerText } from './answer-text';
import { withoutMarkers } from './answer-segments';
import { CitationPanel } from './citation-panel';
import { ConversationStore, groundingOf } from './conversation-store';

/** The server's limit (AskQuestionRequestValidator.MaxQuestionLength), mirrored for the counter. */
export const MAX_QUESTION_LENGTH = 8000;

/**
 * Ask questions about your documents and inspect the sources of each answer (Step 13).
 *
 * Layout: conversations | thread and composer | sources. Under 960px the three stack, with the
 * conversation list collapsed into a disclosure.
 */
@Component({
  selector: 'app-chat-page',
  imports: [
    DatePipe,
    FormsModule,
    RouterLink,
    MatButton,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatProgressBar,
    AnswerText,
    CitationPanel,
    ErrorBanner,
  ],
  providers: [ConversationStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './chat-page.html',
  styleUrl: './chat-page.scss',
})
export class ChatPage {
  protected readonly store = inject(ConversationStore);
  private readonly router = inject(Router);
  private readonly thread = viewChild<ElementRef<HTMLElement>>('thread');
  private readonly composer = viewChild<ElementRef<HTMLTextAreaElement>>('composer');

  /** The conversation id from `?c=`, bound by the router (withComponentInputBinding). */
  readonly c = input<string | undefined>();

  protected readonly maxLength = MAX_QUESTION_LENGTH;
  /** The conversation list starts open beside the thread, collapsed above it on a phone. */
  protected readonly wideScreen = window.matchMedia?.('(min-width: 961px)').matches ?? true;
  protected readonly groundingOf = groundingOf;
  protected readonly withoutMarkers = withoutMarkers;
  protected readonly question = signal('');

  constructor() {
    void this.store.loadConversations();

    // Depends on the URL only. open() reads store.current(); tracked, starting a conversation
    // (current set, ?c= not yet) would re-run this with null and abort the answer being streamed.
    effect(() => {
      const id = this.c() ?? null;
      untracked(() => void this.store.open(id));
    });

    // Keep the newest text in view while an answer streams or a thread loads.
    effect(() => {
      this.store.pending();
      this.store.messages();
      const element = this.thread()?.nativeElement;
      if (element) {
        queueMicrotask(() => element.scrollTo?.({ top: element.scrollHeight }));
      }
    });
  }

  protected async send(): Promise<void> {
    const text = this.question().trim();
    if (!text || this.store.streaming()) {
      return;
    }
    this.question.set('');

    const { conversationId, answered } = await this.store.ask(text);

    if (conversationId && conversationId !== this.c()) {
      // Put the new conversation in the address bar: shareable with yourself, survives a reload.
      await this.router.navigate([], { queryParams: { c: conversationId }, replaceUrl: !this.c() });
    }
    if (!answered && !this.question()) {
      // Nothing was saved; give the question back rather than make the person retype it.
      this.question.set(text);
    }
    this.composer()?.nativeElement.focus();
  }

  protected onComposerKeydown(event: KeyboardEvent): void {
    // Enter sends, Shift+Enter is a new line; IME composition (e.g. Japanese input) is left alone.
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      void this.send();
    }
  }

  protected newConversation(): void {
    void this.router.navigate(['/chat']);
    this.composer()?.nativeElement.focus();
  }

  protected retryLast(): void {
    void this.send();
  }
}
