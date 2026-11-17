/** One server-sent event: its name (`message` when the server gave none) and its data. */
export interface ServerSentEvent {
  readonly event: string;
  readonly data: string;
}

/**
 * Reads a `text/event-stream` body into events, as they complete.
 *
 * Why not `EventSource`: it can only GET and cannot send an `Authorization` header, and asking
 * a question is a POST with a bearer token. So the body is read with `fetch` and parsed here.
 *
 * The parser follows the WHATWG framing rules that matter for this API: events end at a blank
 * line; `event:` and `data:` fields; one optional space after the colon; multiple `data:` lines
 * joined with `\n`; lines starting with `:` are comments; `\r\n`, `\r` and `\n` all end a line.
 * Network chunks split anywhere, including inside a line or a multi-byte character, so text is
 * decoded in streaming mode and only complete lines are interpreted.
 */
export async function* readServerSentEvents(
  body: ReadableStream<Uint8Array>,
  signal?: AbortSignal,
): AsyncGenerator<ServerSentEvent> {
  const reader = body.getReader();
  // stream: true holds back the first bytes of a character split across chunks until the rest arrive.
  const decoder = new TextDecoder();
  const parser = new ServerSentEventParser();

  try {
    while (true) {
      signal?.throwIfAborted();
      const { value, done } = await reader.read();
      if (done) {
        break;
      }
      yield* parser.push(decoder.decode(value, { stream: true }));
    }
    yield* parser.push(decoder.decode());
    yield* parser.end();
  } finally {
    reader.releaseLock();
  }
}

/** The framing state machine, separate from I/O so it can be tested chunk by chunk. */
export class ServerSentEventParser {
  private buffer = '';
  private eventName = '';
  private dataLines: string[] = [];

  /** Feeds decoded text; returns the events completed by it. */
  push(text: string): ServerSentEvent[] {
    this.buffer += text;
    const events: ServerSentEvent[] = [];

    // A trailing '\r' may be the first half of '\r\n': keep it until the next chunk decides.
    let match: RegExpExecArray | null;
    const lineEnd = /\r\n|\r(?!$)|\n/g;
    let consumed = 0;
    while ((match = lineEnd.exec(this.buffer)) !== null) {
      const line = this.buffer.slice(consumed, match.index);
      consumed = match.index + match[0].length;
      const event = this.line(line);
      if (event) {
        events.push(event);
      }
    }
    this.buffer = this.buffer.slice(consumed);
    return events;
  }

  /** The stream ended. An event without its terminating blank line is incomplete: dropped, per spec. */
  end(): ServerSentEvent[] {
    this.buffer = '';
    this.eventName = '';
    this.dataLines = [];
    return [];
  }

  private line(line: string): ServerSentEvent | null {
    if (line === '') {
      return this.dispatch();
    }
    if (line.startsWith(':')) {
      return null;
    }

    const colon = line.indexOf(':');
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) {
      value = value.slice(1);
    }

    if (field === 'event') {
      this.eventName = value;
    } else if (field === 'data') {
      this.dataLines.push(value);
    }
    // id: and retry: are reconnection features; a POST answer is never resumed.
    return null;
  }

  private dispatch(): ServerSentEvent | null {
    const event =
      this.dataLines.length === 0 ? null : { event: this.eventName || 'message', data: this.dataLines.join('\n') };
    this.eventName = '';
    this.dataLines = [];
    return event;
  }
}
