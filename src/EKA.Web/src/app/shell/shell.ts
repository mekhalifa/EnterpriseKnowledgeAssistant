import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatButton } from '@angular/material/button';
import { MatToolbar } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { OidcSecurityService } from 'angular-auth-oidc-client';

/**
 * The frame around every signed-in page: a skip link, the banner with navigation and the user,
 * and the main landmark. Landmarks and the skip link are what let a keyboard or screen-reader user
 * reach the content without tabbing through the navigation on every page (WCAG 2.4.1).
 */
@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbar, MatButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <header>
      <mat-toolbar class="toolbar">
        <span class="brand">Knowledge Assistant</span>
        <nav aria-label="Main">
          <a mat-button routerLink="/chat" routerLinkActive="active" ariaCurrentWhenActive="page">Ask</a>
          <a mat-button routerLink="/documents" routerLinkActive="active" ariaCurrentWhenActive="page">Documents</a>
        </nav>
        <span class="spacer"></span>
        @if (userName(); as name) {
          <span class="user" data-testid="user-name">{{ name }}</span>
        }
        <button mat-stroked-button type="button" (click)="signOut()">Sign out</button>
      </mat-toolbar>
    </header>
    <main id="main" tabindex="-1">
      <router-outlet />
    </main>
  `,
  styleUrl: './shell.scss',
})
export class Shell {
  private readonly auth = inject(OidcSecurityService);
  private readonly user = toSignal(this.auth.userData$);

  protected readonly userName = computed(() => {
    const data = this.user()?.userData as { name?: string; preferred_username?: string } | null | undefined;
    return data?.name ?? data?.preferred_username ?? null;
  });

  protected signOut(): void {
    // Ends the Keycloak session too (RP-initiated logout), not only this tab's tokens: otherwise
    // the next "sign in" would silently reuse the session and sign the same person straight back in.
    this.auth.logoff().subscribe();
  }
}
