import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { CitationResponse } from '../../core/api/api';
import { toAnswerSegments } from './answer-segments';

/**
 * An answer with its citation markers as buttons. Every other character is a text node: Angular
 * interpolation escapes it, and nothing here uses innerHTML, so model output cannot become markup
 * (threat model LLM05). Line breaks are kept by CSS (`white-space: pre-wrap`), not by turning
 * newlines into `<br>`.
 */
@Component({
  selector: 'app-answer-text',
  changeDetection: ChangeDetectionStrategy.OnPush,
  // The text sits tight inside <ng-container>: the indentation around it is then a whitespace-only
  // node, which the compiler drops. Written bare, it would be collapsed to a space on each side of
  // every run of answer text. A test pins the exact output.
  template: `
    @for (segment of segments(); track $index) {
      @if (segment.kind === 'text') {
        <ng-container>{{ segment.text }}</ng-container>
      } @else {
        <button
          type="button"
          class="marker"
          [class.selected]="segment.number === selected()"
          [attr.aria-label]="'Source ' + segment.number + ': ' + titleOf(segment.number)"
          (click)="cite.emit(segment.number)"
        >[{{ segment.number }}]</button>
      }
    }
  `,
  styles: `
    :host {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }
    .marker {
      font: inherit;
      font-size: 0.85em;
      vertical-align: super;
      line-height: 1;
      padding: 0 2px;
      margin: 0 1px;
      border: none;
      border-radius: 4px;
      cursor: pointer;
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }
    .marker.selected {
      background: var(--mat-sys-primary);
      color: var(--mat-sys-on-primary);
    }
  `,
})
export class AnswerText {
  readonly content = input.required<string>();
  readonly citations = input<readonly CitationResponse[]>([]);
  readonly selected = input<number | null>(null);
  readonly cite = output<number>();

  protected readonly segments = computed(() =>
    toAnswerSegments(this.content(), new Set(this.citations().map((c) => Number(c.number)))),
  );

  protected titleOf(number: number): string {
    return this.citations().find((c) => Number(c.number) === number)?.documentTitle ?? '';
  }
}
