import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButton } from '@angular/material/button';
import { MatChipGrid, MatChipInput, MatChipInputEvent, MatChipRemove, MatChipRow } from '@angular/material/chips';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogClose,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatFormField, MatHint, MatLabel } from '@angular/material/form-field';
import { MatProgressBar } from '@angular/material/progress-bar';
import { MatRadioButton, MatRadioGroup } from '@angular/material/radio';
import { DocumentsApi } from '../../core/api/api';
import { UserFacingError, describeHttpError } from '../../core/errors/problem';
import { ErrorBanner } from '../../shared/error-banner';

export interface AccessDialogData {
  readonly documentId: string;
  readonly title: string;
}

/** The access-key shapes the API accepts besides `tenant` (ADR-0027). The server validates again. */
export const PRINCIPAL_PATTERN = /^(user|group):\S+$/;

/**
 * Who may read a document: everyone in the organization, or a list of people and groups.
 *
 * Shown only to callers the server says may manage the document (`canManage`), but that is a
 * courtesy; the PUT is authorized on the server, and a refusal shows here as an error.
 */
@Component({
  selector: 'app-access-dialog',
  imports: [
    FormsModule,
    MatButton,
    MatChipGrid,
    MatChipInput,
    MatChipRemove,
    MatChipRow,
    MatDialogActions,
    MatDialogClose,
    MatDialogContent,
    MatDialogTitle,
    MatFormField,
    MatHint,
    MatLabel,
    MatProgressBar,
    MatRadioButton,
    MatRadioGroup,
    ErrorBanner,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Who can read "{{ data.title }}"</h2>
    <mat-dialog-content>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" aria-label="Loading current access" />
      } @else {
        <mat-radio-group [(ngModel)]="mode" name="mode" aria-label="Who can read this document" class="modes">
          <mat-radio-button value="tenant">Everyone in your organization</mat-radio-button>
          <mat-radio-button value="restricted">Only specific people and groups</mat-radio-button>
        </mat-radio-group>

        @if (mode === 'restricted') {
          <mat-form-field appearance="outline" class="principals">
            <mat-label>People and groups</mat-label>
            <mat-chip-grid #grid aria-label="People and groups who can read">
              @for (principal of principals(); track principal) {
                <mat-chip-row (removed)="removePrincipal(principal)">
                  {{ principal }}
                  <button matChipRemove [attr.aria-label]="'Remove ' + principal">×</button>
                </mat-chip-row>
              }
              <input placeholder="group:hr or user:&lt;id&gt;" [matChipInputFor]="grid" (matChipInputTokenEnd)="addPrincipal($event)" />
            </mat-chip-grid>
            <mat-hint>Press Enter after each one. Owners and administrators can always manage it.</mat-hint>
          </mat-form-field>
          @if (invalid(); as text) {
            <p class="invalid" role="alert">"{{ text }}" is not a group:… or user:… entry.</p>
          }
        }
        @if (error(); as problem) {
          <app-error-banner [error]="problem" (retry)="save()" />
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" [mat-dialog-close]="false">Cancel</button>
      <button mat-flat-button type="button" (click)="save()" [disabled]="loading() || saving() || (mode === 'restricted' && principals().length === 0)">
        Save
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .modes {
      display: grid;
      gap: 8px;
      margin-bottom: 16px;
    }
    .principals {
      width: 100%;
    }
    .invalid {
      color: var(--mat-sys-error);
    }
  `,
})
export class AccessDialog {
  protected readonly data = inject<AccessDialogData>(MAT_DIALOG_DATA);
  private readonly api = inject(DocumentsApi);
  private readonly ref = inject<MatDialogRef<AccessDialog, boolean>>(MatDialogRef);

  protected mode: 'tenant' | 'restricted' = 'tenant';
  protected readonly principals = signal<string[]>([]);
  protected readonly loading = signal(true);
  protected readonly saving = signal(false);
  protected readonly invalid = signal<string | null>(null);
  protected readonly error = signal<UserFacingError | null>(null);

  constructor() {
    this.api.get(this.data.documentId).subscribe({
      next: (document) => {
        const restricted = !(document.access.length === 1 && document.access[0] === 'tenant');
        this.mode = restricted ? 'restricted' : 'tenant';
        this.principals.set(restricted ? [...document.access] : []);
        this.loading.set(false);
      },
      error: (error) => {
        this.loading.set(false);
        this.error.set(describeHttpError(error));
      },
    });
  }

  protected addPrincipal(event: MatChipInputEvent): void {
    const value = event.value.trim();
    if (!value) {
      return;
    }
    if (!PRINCIPAL_PATTERN.test(value)) {
      this.invalid.set(value);
      return;
    }
    this.invalid.set(null);
    this.principals.update((list) => (list.includes(value) ? list : [...list, value]));
    event.chipInput.clear();
  }

  protected removePrincipal(principal: string): void {
    this.principals.update((list) => list.filter((p) => p !== principal));
  }

  protected save(): void {
    const principals = this.mode === 'tenant' ? ['tenant'] : this.principals();
    this.saving.set(true);
    this.error.set(null);
    this.api.setAccess(this.data.documentId, principals).subscribe({
      next: () => this.ref.close(true),
      error: (error) => {
        this.saving.set(false);
        this.error.set(describeHttpError(error));
      },
    });
  }
}
