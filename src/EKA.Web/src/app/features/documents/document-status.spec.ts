import { isInProgress, statusAnnouncements, statusLabel } from './document-status';

describe('document status', () => {
  it('keeps polling until every document has finished, successfully or not', () => {
    expect(['Pending', 'Extracting', 'Chunking', 'Embedding'].every(isInProgress)).toBe(true);
    expect(isInProgress('Completed')).toBe(false);
    expect(isInProgress('Failed')).toBe(false);
  });

  it('shows a stage the client does not know yet by its own name rather than nothing', () => {
    expect(statusLabel('Reranking')).toBe('Reranking');
  });

  it('announces only arrivals at ready or failed, not every intermediate stage', () => {
    const before = new Map([
      ['a', 'Embedding'],
      ['b', 'Pending'],
      ['c', 'Extracting'],
      ['d', 'Completed'],
    ]);

    const messages = statusAnnouncements(before, [
      { id: 'a', title: 'Leave policy', status: 'Completed' },
      { id: 'b', title: 'Scan', status: 'Failed' },
      { id: 'c', title: 'Handbook', status: 'Chunking' },
      { id: 'd', title: 'Old', status: 'Completed' },
    ]);

    expect(messages).toEqual(['"Leave policy" is ready for questions.', '"Scan" could not be indexed.']);
  });
});
