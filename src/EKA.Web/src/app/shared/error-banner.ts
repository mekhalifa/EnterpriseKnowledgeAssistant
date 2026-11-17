import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { MatButton } from '@angular/material/button';
import { OidcSecurityService } from 'angular-auth-oidc-client';
import { UserFacingError } from '../core/errors/problem';

/**
 * One way to show a failure everywhere: what happened in plain words, the one action that can help
 * (retry, or sign in again), and a reference to quote. `role="alert"` makes a screen reader announce
 * it as soon as it appears (WCAG 4.1.3).
 */
@Component({
  selector: 'app-error-banner',
  imports: [MatButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="banner" role="alert">
      <p class="message">{{ error().message }}</p>
      @if (error().traceId; as reference) {
        <p class="reference">Reference: <code>{{ reference }}</code></p>
      }
      <div class="buttons">
        @if (error().signInRequired) {
          <button mat-flat-button type="button" (click)="signIn()">Sign in again</button>
        } @else if (error().retryable) {
          <button mat-stroked-button type="button" (click)="retry.emit()">Try again</button>
        }
      </div>
    </div>
  `,
  styles: `
    .banner {
      display: grid;
      gap: 4px;
      padding: 12px 16px;
      margin: 12px 0;
      border-radius: 8px;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
    }
    .message,
    .reference {
      margin: 0;
    }
    .reference {
      font: var(--mat-sys-body-small);
    }
    .buttons:empty {
      display: none;
    }
  `,
})
export class ErrorBanner {
  private readonly auth = inject(OidcSecurityService);

  readonly error = input.required<UserFacingError>();
  readonly retry = output<void>();

  protected signIn(): void {
    this.auth.authorize();
  }
}
