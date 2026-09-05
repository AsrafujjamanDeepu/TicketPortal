import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';
import { TpButtonDirective } from '../../shared/ui/button/tp-button.directive';
import { TpLogoComponent } from '../../shared/ui/logo/tp-logo.component';

/**
 * Shows different nav links depending on the logged-in user's role. Add a
 * link here when your feature module ships its top-level landing page —
 * don't scatter portal-switcher links inside individual feature components.
 */
@Component({
  selector: 'tp-navbar',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, TpButtonDirective, TpLogoComponent],
  template: `
    <header class="tp-navbar tp-glass">
      <div class="tp-navbar__inner">
        <a routerLink="/search" class="tp-navbar__brand">
          <tp-logo [size]="34" />
        </a>

        <nav class="tp-navbar__links">
          @if (!auth.isAuthenticated() || auth.hasRole('Customer')) {
            <a routerLink="/search" routerLinkActive="tp-navbar__link--active">Search Trips</a>
          }
          @if (auth.hasRole('Customer')) {
            <a routerLink="/my-bookings" routerLinkActive="tp-navbar__link--active">My Bookings</a>
          }
          @if (auth.hasRole('Operator', 'Staff', 'Admin')) {
            <a routerLink="/operator" routerLinkActive="tp-navbar__link--active">Operator Panel</a>
          }
          @if (auth.hasRole('Staff')) {
            <a routerLink="/counter" routerLinkActive="tp-navbar__link--active">Counter Desk</a>
            <a routerLink="/finance" routerLinkActive="tp-navbar__link--active">Finance</a>
          }
          @if (auth.hasRole('Admin')) {
            <a routerLink="/admin" routerLinkActive="tp-navbar__link--active">Admin</a>
          }
        </nav>

        <div class="tp-navbar__actions">
          @if (auth.isAuthenticated()) {
            <span class="tp-navbar__user">
              <span class="tp-navbar__avatar">{{ initial() }}</span>
              {{ auth.currentUser()?.userName }}
            </span>
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
        border-bottom: 1px solid var(--tp-border);
        position: sticky;
        top: 0;
        z-index: 100;
      }

      .tp-navbar__inner {
        max-width: 1200px;
        margin: 0 auto;
        padding: var(--tp-space-3) var(--tp-space-5);
        display: flex;
        align-items: center;
        gap: var(--tp-space-6);
      }

      .tp-navbar__brand {
        display: flex;
        align-items: center;
        flex-shrink: 0;
        transition: transform var(--tp-transition-fast);
      }

      .tp-navbar__brand:hover {
        transform: translateY(-1px);
      }

      .tp-navbar__links {
        display: flex;
        gap: var(--tp-space-5);
        flex: 1;
      }

      .tp-navbar__links a {
        position: relative;
        padding: var(--tp-space-1) 0;
        font-size: 14px;
        font-weight: 500;
        color: var(--tp-text-muted);
        transition: color var(--tp-transition-fast);
      }

      .tp-navbar__links a::after {
        content: '';
        position: absolute;
        left: 0;
        right: 100%;
        bottom: -13px;
        height: 2px;
        border-radius: 2px;
        background: var(--tp-gradient-brand);
        transition: right var(--tp-transition);
      }

      .tp-navbar__links a:hover {
        color: var(--tp-text);
      }

      .tp-navbar__links a:hover::after {
        right: 0;
      }

      .tp-navbar__link--active {
        color: var(--tp-text) !important;
        font-weight: 600;
      }

      .tp-navbar__link--active::after {
        right: 0 !important;
      }

      .tp-navbar__actions {
        display: flex;
        align-items: center;
        gap: var(--tp-space-3);
      }

      .tp-navbar__user {
        display: flex;
        align-items: center;
        gap: var(--tp-space-2);
        font-size: 14px;
        font-weight: 600;
        color: var(--tp-text-muted);
      }

      .tp-navbar__avatar {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        width: 26px;
        height: 26px;
        border-radius: 50%;
        background: var(--tp-gradient-brand);
        color: var(--tp-text-on-yellow);
        font-size: 12px;
        font-weight: 700;
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

  protected initial(): string {
    const name = this.auth.currentUser()?.userName ?? '';
    return name.charAt(0).toUpperCase() || '•';
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/auth/login']);
  }
}
