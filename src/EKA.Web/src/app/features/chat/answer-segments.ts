/** A run of answer text, or a citation marker `[n]` pointing at citation number n. */
export type AnswerSegment = { readonly kind: 'text'; readonly text: string } | { readonly kind: 'citation'; readonly number: number };

const marker = /\[(\d{1,3})\]/g;

/**
 * Splits a persisted answer at its citation markers, keeping only markers that match a citation
 * the server returned. The server has already renumbered markers and removed invented ones
 * (step-10); a bracketed number with no matching citation stays as plain text.
 *
 * This is the whole of answer "rendering": segments become text nodes and buttons in the
 * template. Answer text is model output, which is untrusted (threat model LLM05), so it is never
 * interpreted as HTML or Markdown — not even "safe" Markdown — and a link in it stays inert text.
 */
export function toAnswerSegments(content: string, citationNumbers: ReadonlySet<number>): AnswerSegment[] {
  const segments: AnswerSegment[] = [];
  let last = 0;

  for (const match of content.matchAll(marker)) {
    const number = Number(match[1]);
    if (!citationNumbers.has(number)) {
      continue;
    }
    if (match.index > last) {
      segments.push({ kind: 'text', text: content.slice(last, match.index) });
    }
    segments.push({ kind: 'citation', number });
    last = match.index + match[0].length;
  }

  if (last < content.length) {
    segments.push({ kind: 'text', text: content.slice(last) });
  }
  return segments;
}

/**
 * While an answer streams, its markers are the model's own numbering, which the server will
 * renumber when the answer completes. Showing `[3]` and then swapping it for `[1]` would be
 * misleading, so markers are hidden until the final answer arrives.
 */
export function withoutMarkers(streamingText: string): string {
  return streamingText.replace(/ ?\[\d{1,3}(?:\s*,\s*\d{1,3})*\]/g, '');
}
