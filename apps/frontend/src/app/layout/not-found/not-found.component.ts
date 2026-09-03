import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TpButtonDirective } from '../../shared/ui/button/tp-button.directive';

@Component({
  selector: 'tp-not-found',
  standalone: true,
  imports: [RouterLink, TpButtonDirective],
  template: `
    <div class="tp-page tp-status-page">
      <div class="tp-status-page__icon">🍌</div>
      <h1>404 — Page not found</h1>
      <p class="tp-muted">The page you're looking for doesn't exist or was moved.</p>
      <a routerLink="/search"><button tpButton variant="primary">Back to Search</button></a>
    </div>
  `,
  styles: [
    `
      .tp-status-page {
        text-align: center;
        padding-top: var(--tp-space-7);
        padding-bottom: var(--tp-space-7);
      }

      .tp-status-page__icon {
        font-size: 56px;
        margin-bottom: var(--tp-space-3);
      }
    `,
  ],
})
export class NotFoundComponent {}
