/** Ingestion statuses as the API names them (`IngestionStatus` in the domain). */
export type IngestionStatus = 'Pending' | 'Extracting' | 'Chunking' | 'Embedding' | 'Completed' | 'Failed';

const labels: Record<string, string> = {
  Pending: 'Queued',
  Extracting: 'Reading',
  Chunking: 'Splitting',
  Embedding: 'Indexing',
  Completed: 'Ready',
  Failed: 'Failed',
};

/** True while the worker still has work to do: the list keeps polling until none is. */
export function isInProgress(status: string): boolean {
  return status !== 'Completed' && status !== 'Failed';
}

/** A person's word for the status. Unknown statuses (a stage added later) show as-is, not as blank. */
export function statusLabel(status: string): string {
  return labels[status] ?? status;
}

/**
 * What changed between two polls that is worth announcing to a screen reader: a document becoming
 * ready, or failing. Intermediate stages are not announced; a live region that speaks every few
 * seconds is noise that drowns out everything else.
 */
export function statusAnnouncements(
  before: ReadonlyMap<string, string>,
  after: readonly { id: string; title: string; status: string }[],
): string[] {
  const messages: string[] = [];
  for (const document of after) {
    const previous = before.get(document.id);
    if (previous === undefined || previous === document.status || !isInProgress(previous)) {
      continue;
    }
    if (document.status === 'Completed') {
      messages.push(`"${document.title}" is ready for questions.`);
    } else if (document.status === 'Failed') {
      messages.push(`"${document.title}" could not be indexed.`);
    }
  }
  return messages;
}
