import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { AnswerStreamEvent, AskQuestionResponse, ConversationResponse, ConversationsApi, MessageResponse } from '../../core/api/api';
import { ConversationStore, groundingOf } from './conversation-store';

const conversation: ConversationResponse = { id: 'c1', title: null, createdAt: '2026-11-17T08:00:00Z', modifiedAt: null, messages: [] };

function message(overrides: Partial<MessageResponse>): MessageResponse {
  return { id: 'm', role: 'Assistant', content: '', createdAt: '2026-11-17T08:00:00Z', tokenCount: null, model: 'gpt', citations: [], ...overrides };
}

const exchange: AskQuestionResponse = {
  conversationId: 'c1',
  title: 'Leave?',
  question: message({ id: 'q1', role: 'User', content: 'Leave?', model: null }),
  answer: message({
    id: 'a1',
    content: 'You get 25 days [1].',
    citations: [{ number: 1, documentId: 'd1', documentTitle: 'Leave policy', chunkIndex: 0, pageStart: null, pageEnd: null, sectionPath: 'Leave', score: 0.8 }],
  }),
  promptTokens: 40,
  historyMessagesIncluded: 0,
  historyMessagesOmitted: 0,
  promptTemplateVersion: 2,
  grounding: 'cited',
  sourcesRetrieved: 1,
  sourcesProvided: 1,
};

/** A stream the test controls: each `emit` delivers one event to the store. */
function scriptedStream(events: AnswerStreamEvent[], observed: { pendingDuring: (string | undefined)[] }, store: () => ConversationStore) {
  return async function* () {
    for (const event of events) {
      yield event;
      await Promise.resolve();
      observed.pendingDuring.push(store().pending()?.answer);
    }
  };
}

describe('ConversationStore', () => {
  let api: { start: ReturnType<typeof vi.fn>; askStreaming: ReturnType<typeof vi.fn>; list: ReturnType<typeof vi.fn>; get: ReturnType<typeof vi.fn> };
  let store: ConversationStore;

  beforeEach(() => {
    api = { start: vi.fn(() => of(conversation)), askStreaming: vi.fn(), list: vi.fn(() => of([])), get: vi.fn() };
    TestBed.configureTestingModule({ providers: [ConversationStore, { provide: ConversationsApi, useValue: api }] });
    store = TestBed.inject(ConversationStore);
  });

  it('starts a conversation on the first question, shows deltas as they arrive, then keeps only what the server saved', async () => {
    const observed = { pendingDuring: [] as (string | undefined)[] };
    api.askStreaming.mockImplementation(
      scriptedStream(
        [
          { type: 'delta', text: 'You get ' },
          { type: 'delta', text: '25 days [3].' },
          { type: 'completed', exchange },
        ],
        observed,
        () => store,
      ),
    );

    const result = await store.ask('Leave?');

    expect(result).toEqual({ conversationId: 'c1', answered: true });
    expect(observed.pendingDuring.slice(0, 2)).toEqual(['You get ', 'You get 25 days [3].']);
    expect(store.pending()).toBeNull();
    expect(store.messages().map((m) => m.content)).toEqual(['Leave?', 'You get 25 days [1].']);
    expect(store.conversations()[0]).toMatchObject({ id: 'c1', title: 'Leave?' });
    expect(store.announcement()).toBe('Answer ready, with 1 source.');
  });

  it('discards a half-written answer on failure and reports it, so the question can be asked again', async () => {
    api.askStreaming.mockImplementation(async function* () {
      yield { type: 'delta', text: 'You get' } as AnswerStreamEvent;
      yield { type: 'error', error: { message: 'The AI service is not responding right now.', retryable: true } } as AnswerStreamEvent;
    });

    const result = await store.ask('Leave?');

    expect(result.answered).toBe(false);
    expect(store.pending()).toBeNull();
    expect(store.messages()).toEqual([]);
    expect(store.error()?.retryable).toBe(true);
  });

  it('stopping aborts the request the stream is reading', async () => {
    let signal: AbortSignal | undefined;
    api.askStreaming.mockImplementation(async function* (_id: string, _q: string, s: AbortSignal) {
      signal = s;
      yield { type: 'delta', text: 'You' } as AnswerStreamEvent;
      // An open connection: nothing more arrives until the request is aborted.
      await new Promise<void>((resolve) => s.addEventListener('abort', () => resolve()));
    });

    const asking = store.ask('Leave?');
    await vi.waitFor(() => expect(store.pending()?.answer).toBe('You'));
    store.stop();
    await asking;

    expect(signal?.aborted).toBe(true);
    expect(store.messages()).toEqual([]);
  });

  it('derives grounding from what is persisted, so a reloaded thread shows it too', () => {
    expect(groundingOf(exchange.answer)).toBe('cited');
    expect(groundingOf(message({ model: 'none:no-evidence' }))).toBe('no_evidence');
    expect(groundingOf(message({}))).toBe('uncited');
  });
});
