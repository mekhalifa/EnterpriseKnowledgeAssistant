import { ServerSentEventParser, readServerSentEvents } from './server-sent-events';

describe('ServerSentEventParser', () => {
  it('emits an event at the blank line that ends it', () => {
    const parser = new ServerSentEventParser();

    expect(parser.push('event: delta\ndata: {"text":"Hi"}\n')).toEqual([]);
    expect(parser.push('\n')).toEqual([{ event: 'delta', data: '{"text":"Hi"}' }]);
  });

  it('reassembles an event split across chunks at any point, including inside a CRLF', () => {
    const wire = 'event: delta\r\ndata: {"text":"a"}\r\n\r\nevent: completed\r\ndata: {}\r\n\r\n';
    const parser = new ServerSentEventParser();
    const events = [...wire].flatMap((character) => parser.push(character));

    expect(events).toEqual([
      { event: 'delta', data: '{"text":"a"}' },
      { event: 'completed', data: '{}' },
    ]);
  });

  it('joins multiple data lines with a newline and ignores comments', () => {
    const parser = new ServerSentEventParser();

    expect(parser.push(': keep-alive\ndata: one\ndata: two\n\n')).toEqual([{ event: 'message', data: 'one\ntwo' }]);
  });

  it('drops an event the stream ended in the middle of', () => {
    const parser = new ServerSentEventParser();
    parser.push('event: delta\ndata: {"text":"half');

    expect(parser.end()).toEqual([]);
  });
});

describe('readServerSentEvents', () => {
  it('decodes a multi-byte character split between two network chunks', async () => {
    const bytes = new TextEncoder().encode('event: delta\ndata: {"text":"café"}\n\n');
    const split = bytes.length - 6; // inside the two bytes of "é"
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(bytes.slice(0, split));
        controller.enqueue(bytes.slice(split));
        controller.close();
      },
    });

    const events = [];
    for await (const event of readServerSentEvents(body)) {
      events.push(event);
    }

    expect(events).toEqual([{ event: 'delta', data: '{"text":"café"}' }]);
  });
});
