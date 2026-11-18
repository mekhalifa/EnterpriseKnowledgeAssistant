import { TestBed } from '@angular/core/testing';
import { CitationResponse, MessageResponse } from '../../core/api/api';
import { CitationPanel } from './citation-panel';

function citation(number: number): CitationResponse {
  return { number, documentId: `d${number}`, documentTitle: `Policy ${number}`, chunkIndex: 0, pageStart: null, pageEnd: null, sectionPath: null, score: 0.8 };
}

const answer: MessageResponse = {
  id: 'a1',
  role: 'Assistant',
  content: 'Twenty-five days [1], carried over until March [2].',
  createdAt: '2026-11-18T08:00:00Z',
  tokenCount: null,
  model: 'gpt',
  citations: [citation(1), citation(2)],
};

async function render() {
  const fixture = TestBed.createComponent(CitationPanel);
  fixture.componentRef.setInput('message', answer);
  await fixture.whenStable();
  return fixture;
}

/** Selects as the page does: a new object per click, then waits for the panel to react. */
async function select(fixture: Awaited<ReturnType<typeof render>>, number: number | null) {
  fixture.componentRef.setInput('selection', { messageId: answer.id, number });
  await fixture.whenStable();
  await Promise.resolve();
}

describe('CitationPanel', () => {
  it('moves focus to the source list when "Show sources" is chosen', async () => {
    // The bug: a selection without a number did nothing at all, so the button looked dead,
    // and on a narrow screen the list stayed out of view below the conversation.
    const fixture = await render();
    const list = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="citations"]');

    await select(fixture, null);

    expect(document.activeElement).toBe(list);
  });

  it('focuses and highlights the chosen citation', async () => {
    const fixture = await render();
    const host = fixture.nativeElement as HTMLElement;

    await select(fixture, 2);

    const second = host.querySelector('#citation-2');
    expect(document.activeElement).toBe(second);
    expect(second?.classList.contains('selected')).toBe(true);
    expect(host.querySelector('#citation-1')?.classList.contains('selected')).toBe(false);
  });

  it('brings the same citation back when its marker is chosen a second time', async () => {
    // Reacting to the number alone made a repeated click a no-op: the number had not changed.
    const fixture = await render();

    await select(fixture, 1);
    (document.activeElement as HTMLElement).blur();
    await select(fixture, 1);

    expect(document.activeElement).toBe((fixture.nativeElement as HTMLElement).querySelector('#citation-1'));
  });
});
