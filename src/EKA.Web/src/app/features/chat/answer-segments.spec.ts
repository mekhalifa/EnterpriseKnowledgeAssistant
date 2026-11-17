import { toAnswerSegments, withoutMarkers } from './answer-segments';

describe('toAnswerSegments', () => {
  it('turns known markers into citations and keeps the text around them', () => {
    expect(toAnswerSegments('Leave is 25 days [1]. Carry-over is 5 [2].', new Set([1, 2]))).toEqual([
      { kind: 'text', text: 'Leave is 25 days ' },
      { kind: 'citation', number: 1 },
      { kind: 'text', text: '. Carry-over is 5 ' },
      { kind: 'citation', number: 2 },
      { kind: 'text', text: '.' },
    ]);
  });

  it('leaves a bracketed number with no matching citation as plain text', () => {
    expect(toAnswerSegments('See clause [7].', new Set([1]))).toEqual([{ kind: 'text', text: 'See clause [7].' }]);
  });

  it('keeps markup as characters: segments are text, never HTML', () => {
    const segments = toAnswerSegments('<img src=x onerror=alert(1)> [1]', new Set([1]));

    expect(segments[0]).toEqual({ kind: 'text', text: '<img src=x onerror=alert(1)> ' });
  });
});

describe('withoutMarkers', () => {
  it("hides the model's provisional numbering while an answer streams", () => {
    expect(withoutMarkers('Leave is 25 days [3]. Also [1, 2].')).toBe('Leave is 25 days. Also.');
  });
});
