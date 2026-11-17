import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatProgressSpinner } from '@angular/material/progress-spinner';

/**
 * Where Keycloak sends the browser back with `?code=…`. The app initializer has already exchanged
 * the code (withAppInitializerAuthCheck) and the library navigates to the page that was asked for;
 * this only covers the moment in between.
 */
@Component({
  selector: 'app-callback',
  imports: [MatProgressSpinner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="callback" aria-busy="true">
      <mat-progress-spinner mode="indeterminate" diameter="40" aria-label="Signing in" />
      <p>Signing you in…</p>
    </main>
  `,
  styles: `
    .callback {
      display: grid;
      place-items: center;
      gap: 16px;
      min-height: 60vh;
    }
  `,
})
export class Callback {}
