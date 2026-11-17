import { TestBed } from '@angular/core/testing';
import { CitationResponse } from '../../core/api/api';
import { AnswerText } from './answer-text';

const citation: CitationResponse = {
  number: 1,
  documentId: 'd1',
  documentTitle: 'Leave policy',
  chunkIndex: 0,
  pageStart: null,
  pageEnd: null,
  sectionPath: 'Leave',
  score: 0.8,
};

async function render(content: string, citations: CitationResponse[] = [citation]) {
  const fixture = TestBed.createComponent(AnswerText);
  fixture.componentRef.setInput('content', content);
  fixture.componentRef.setInput('citations', citations);
  await fixture.whenStable();
  return fixture;
}

describe('AnswerText', () => {
  it('renders markup in an answer as visible characters, never as elements', async () => {
    const fixture = await render('<img src=x onerror="alert(1)"><script>alert(2)</script> [1]');
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('script')).toBeNull();
    expect(host.textContent).toContain('<img src=x onerror="alert(1)">');
  });

  it('turns each known marker into a labelled button that reports its number', async () => {
    const fixture = await render('25 days [1].');
    const cited: number[] = [];
    fixture.componentInstance.cite.subscribe((n) => cited.push(n));

    const button = (fixture.nativeElement as HTMLElement).querySelector('button')!;
    button.click();

    expect(button.textContent).toBe('[1]');
    expect(button.getAttribute('aria-label')).toBe('Source 1: Leave policy');
    expect(cited).toEqual([1]);
  });

  it('adds no characters of its own around the answer text and keeps line breaks', async () => {
    const fixture = await render('Line one.\nLine two [1].');

    expect((fixture.nativeElement as HTMLElement).textContent).toBe('Line one.\nLine two [1].');
  });

  it('leaves a link in the answer as inert text', async () => {
    const fixture = await render('Visit [here](javascript:alert(1)) or https://evil.example');

    expect((fixture.nativeElement as HTMLElement).querySelector('a')).toBeNull();
  });
});
