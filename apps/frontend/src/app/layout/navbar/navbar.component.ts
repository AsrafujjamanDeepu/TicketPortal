import { Component, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';
import { TpButtonDirective } from '../../shared/ui/button/tp-button.directive';

/**
 * Shows different nav links depending on the logged-in user's role. Add a
 * link here when your feature module ships its top-level landing page —
 * don't scatter portal-switcher links inside individual feature components.
 */
@Component({
  selector: 'tp-navbar',
  standalone: true,
  imports: [RouterLink, TpButtonDirective],
  template: `
    <header class="tp-navbar">
      <div class="tp-navbar__inner">
        <a routerLink="/search" class="tp-navbar__brand">
          <span class="tp-navbar__logo">🚌</span>
          TicketPortal
        </a>

        <nav class="tp-navbar__links">
          @if (!auth.isAuthenticated() || auth.hasRole('Customer')) {
            <a routerLink="/search">Search Trips</a>
          }
          @if (auth.hasRole('Customer')) {
            <a routerLink="/my-bookings">My Bookings</a>
          }
          @if (auth.hasRole('Operator')) {
            <a routerLink="/operator">Operator Panel</a>
          }
          @if (auth.hasRole('Staff')) {
            <a routerLink="/counter">Counter Desk</a>
            <a routerLink="/finance">Finance</a>
          }
          @if (auth.hasRole('Admin')) {
            <a routerLink="/admin">Admin</a>
          }
        </nav>

        <div class="tp-navbar__actions">
          @if (auth.isAuthenticated()) {
            <span class="tp-navbar__user">{{ auth.currentUser()?.userName }}</span>
            <button tpButton variant="secondary" size="sm" (click)="logout()">Log out</button>
          } @else {
            <a routerLink="/auth/login"><button tpButton variant="secondary" size="sm">Log in</button></a>
            <a routerLink="/auth/register"><button tpButton variant="primary" size="sm">Sign up</button></a>
          }
        </div>
      </div>
    </header>
  `,
  styles: [
    `
      .tp-navbar {
        background: var(--tp-surface);
        border-bottom: 1px solid var(--tp-border);
        position: sticky;
        top: 0;
        z-index: 100;
      }

      .tp-navbar__inner {
        max-width: 1160px;
        margin: 0 auto;
        padding: var(--tp-space-3) var(--tp-space-5);
        display: flex;
        align-items: center;
        gap: var(--tp-space-6);
      }

      .tp-navbar__brand {
        display: flex;
        align-items: center;
        gap: var(--tp-space-2);
        font-family: var(--tp-font-heading);
        font-weight: 700;
        font-size: 18px;
        color: var(--tp-text);
        flex-shrink: 0;
      }

      .tp-navbar__logo {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 32px;
        height: 32px;
        border-radius: var(--tp-radius-sm);
        background: var(--tp-yellow);
      }

      .tp-navbar__links {
        display: flex;
        gap: var(--tp-space-5);
        flex: 1;
      }

      .tp-navbar__links a {
        font-size: 14px;
        font-weight: 500;
        color: var(--tp-text-muted);
        transition: color var(--tp-transition-fast);
      }

      .tp-navbar__links a:hover {
        color: var(--tp-text);
      }

      .tp-navbar__actions {
        display: flex;
        align-items: center;
        gap: var(--tp-space-3);
      }

      .tp-navbar__user {
        font-size: 14px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      @media (max-width: 720px) {
        .tp-navbar__links {
          display: none;
        }
      }
    `,
  ],
})
export class NavbarComponent {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/auth/login']);
  }
}
