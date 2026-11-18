import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input } from '@angular/core';
import { MessageResponse } from '../../core/api/api';
import { CitationSelection, Grounding, groundingOf } from './conversation-store';

/**
 * The sources behind one answer: which document, where in it, and how closely it matched.
 *
 * When a marker is chosen, its citation is highlighted and receives focus, so a keyboard or
 * screen-reader user lands on the source rather than having to find it. "Show sources" (a
 * selection without a number) moves focus to the whole list the same way.
 *
 * It reacts to each *selection*, not to the selected number. Every click makes a new selection
 * object, so choosing the same marker twice, or "Show sources" for the answer already on
 * display, still brings the panel into view. Reacting to the number alone made both clicks do
 * nothing visible, and on a narrow screen, where the panel sits below the conversation, the
 * button looked broken (post-01 follow-up).
 */
@Component({
  selector: 'app-citation-panel',
  imports: [DecimalPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 id="sources-heading">Sources</h2>
    @if (message(); as answer) {
      <p class="grounding" [attr.data-grounding]="grounding()">{{ groundingText() }}</p>
      @if (answer.citations.length > 0) {
        <ol class="citations" aria-labelledby="sources-heading" tabindex="-1" data-testid="citations">
          @for (citation of answer.citations; track citation.number) {
            <li
              tabindex="-1"
              [id]="'citation-' + citation.number"
              [class.selected]="citation.number === selection()?.number"
              [attr.data-testid]="'citation-' + citation.number"
            >
              <span class="number" aria-hidden="true">{{ citation.number }}</span>
              <div>
                <p class="title">{{ citation.documentTitle }}</p>
                @if (citation.sectionPath) {
                  <p class="detail">Section: {{ citation.sectionPath }}</p>
                }
                @if (citation.pageStart) {
                  <p class="detail">
                    {{ citation.pageEnd && citation.pageEnd !== citation.pageStart ? 'Pages ' + citation.pageStart + '–' + citation.pageEnd : 'Page ' + citation.pageStart }}
                  </p>
                }
                <p class="detail">Passage {{ +citation.chunkIndex + 1 }} · match {{ +citation.score * 100 | number: '1.0-0' }}%</p>
              </div>
            </li>
          }
        </ol>
      }
    } @else {
      <p class="empty">Ask a question to see the sources its answer is based on.</p>
    }
  `,
  styleUrl: './citation-panel.scss',
})
export class CitationPanel {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly message = input<MessageResponse | null>(null);
  readonly selection = input<CitationSelection | null>(null);

  constructor() {
    effect(() => {
      const selection = this.selection();
      if (selection === null) {
        return;
      }
      // After this change has rendered the panel's new content and the highlighted item.
      queueMicrotask(() => {
        const target = this.host.nativeElement.querySelector<HTMLElement>(
          selection.number === null ? '.citations' : `#citation-${selection.number}`,
        );
        target?.focus({ preventScroll: true });
        target?.scrollIntoView?.({ block: selection.number === null ? 'start' : 'nearest' });
      });
    });
  }

  protected grounding(): Grounding | null {
    const message = this.message();
    return message ? groundingOf(message) : null;
  }

  protected groundingText(): string {
    switch (this.grounding()) {
      case 'cited':
        return 'This answer is based on the passages below.';
      case 'uncited':
        return 'This answer cites none of your documents. Treat it with care.';
      case 'no_evidence':
        return 'No document you can read was relevant to this question, so no answer was generated.';
      default:
        return '';
    }
  }
}
