import { DatePipe, DecimalPipe } from '@angular/common';
import { HttpEventType } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormField, MatHint, MatInput, MatLabel } from '@angular/material/input';
import { MatPaginator, PageEvent } from '@angular/material/paginator';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatProgressSpinner } from '@angular/material/progress-spinner';
import { MatTableModule } from '@angular/material/table';
import { DocumentSummaryResponse, DocumentsApi } from '../../core/api/api';
import { UserFacingError, describeHttpError } from '../../core/errors/problem';
import { ErrorBanner } from '../../shared/error-banner';
import { AccessDialog, AccessDialogData } from './access-dialog';
import { isInProgress, statusAnnouncements, statusLabel } from './document-status';

/** How often the list is re-read while a document is being indexed. */
export const POLL_INTERVAL_MS = 3000;

interface UploadState {
  readonly fileName: string;
  readonly percent: number;
}

/**
 * Documents: upload, watch indexing, manage access (Step 13).
 *
 * Polling, not push. The list is re-read every few seconds only while something is in progress,
 * and stops by itself when everything has finished. Push (SSE or SignalR from the worker) would
 * need a channel from the worker process to the API — a message bus — for a wait that is usually
 * under a minute. ADR-0033 records when that would change.
 */
@Component({
  selector: 'app-documents-page',
  imports: [
    DatePipe,
    DecimalPipe,
    FormsModule,
    MatButton,
    MatFormField,
    MatHint,
    MatInput,
    MatLabel,
    MatPaginator,
    MatProgressBar,
    MatProgressSpinner,
    MatTableModule,
    ErrorBanner,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './documents-page.html',
  styleUrl: './documents-page.scss',
})
export class DocumentsPage {
  private readonly api = inject(DocumentsApi);
  private readonly dialog = inject(MatDialog);
  private pollTimer: ReturnType<typeof setTimeout> | undefined;
  private lastStatuses = new Map<string, string>();

  protected readonly columns = ['title', 'status', 'chunks', 'size', 'created', 'actions'];
  protected readonly accept = '.pdf,.docx,.md,.markdown,.txt';
  protected readonly statusLabel = statusLabel;
  protected readonly isInProgress = isInProgress;

  protected readonly documents = signal<DocumentSummaryResponse[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly pageSize = signal(20);
  protected readonly loading = signal(true);
  protected readonly error = signal<UserFacingError | null>(null);
  protected readonly upload = signal<UploadState | null>(null);
  protected readonly notice = signal<string | null>(null);
  /** Text for the polite live region; screen readers speak it when it changes. */
  protected readonly announcement = signal('');

  protected title = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.pollTimer));
    this.load();
  }

  protected load(): void {
    clearTimeout(this.pollTimer);
    this.api.list(this.page(), this.pageSize()).subscribe({
      next: (page) => {
        const messages = statusAnnouncements(this.lastStatuses, page.items);
        if (messages.length > 0) {
          this.announcement.set(messages.join(' '));
        }
        this.lastStatuses = new Map(page.items.map((d) => [d.id, d.status]));
        this.documents.set(page.items);
        this.total.set(Number(page.totalCount));
        this.loading.set(false);
        this.error.set(null);

        if (page.items.some((d) => isInProgress(d.status))) {
          this.pollTimer = setTimeout(() => this.load(), POLL_INTERVAL_MS);
        }
      },
      error: (error) => {
        this.loading.set(false);
        this.error.set(describeHttpError(error));
      },
    });
  }

  protected onPage(event: PageEvent): void {
    this.page.set(event.pageIndex + 1);
    this.pageSize.set(event.pageSize);
    this.load();
  }

  protected onFileChosen(input: HTMLInputElement): void {
    const file = input.files?.[0];
    input.value = ''; // choosing the same file again must fire change again
    if (!file) {
      return;
    }

    this.notice.set(null);
    this.error.set(null);
    this.upload.set({ fileName: file.name, percent: 0 });

    this.api.upload(file, this.title).subscribe({
      next: (event) => {
        if (event.type === HttpEventType.UploadProgress && event.total) {
          this.upload.set({ fileName: file.name, percent: Math.round((100 * event.loaded) / event.total) });
        } else if (event.type === HttpEventType.Response) {
          this.upload.set(null);
          this.title = '';
          // 200, not 201: these exact bytes were already uploaded (step-07). Not an error.
          const message =
            event.status === 200
              ? `This file was already uploaded as "${event.body?.title}".`
              : `"${event.body?.title}" was uploaded and is being indexed.`;
          this.notice.set(message);
          this.announcement.set(message);
          this.page.set(1);
          this.load();
        }
      },
      error: (error) => {
        this.upload.set(null);
        this.error.set(describeHttpError(error));
      },
    });
  }

  protected editAccess(document: DocumentSummaryResponse): void {
    this.dialog
      .open<AccessDialog, AccessDialogData, boolean>(AccessDialog, {
        data: { documentId: document.id, title: document.title },
        width: '560px',
        maxWidth: 'calc(100vw - 32px)',
        autoFocus: 'first-tabbable',
      })
      .afterClosed()
      .subscribe((changed) => {
        if (changed) {
          this.announcement.set(`Access to "${document.title}" was updated.`);
          this.load();
        }
      });
  }

  protected retry(document: DocumentSummaryResponse): void {
    this.api.reset(document.id).subscribe({
      next: () => {
        this.announcement.set(`"${document.title}" was queued again.`);
        this.load();
      },
      error: (error) => this.error.set(describeHttpError(error)),
    });
  }

  protected remove(document: DocumentSummaryResponse): void {
    if (!window.confirm(`Delete "${document.title}"? It will no longer be used to answer questions.`)) {
      return;
    }
    this.api.delete(document.id).subscribe({
      next: () => {
        this.announcement.set(`"${document.title}" was deleted.`);
        this.load();
      },
      error: (error) => this.error.set(describeHttpError(error)),
    });
  }
}
