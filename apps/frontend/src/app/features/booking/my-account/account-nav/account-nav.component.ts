import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

/**
 * TpTabsComponent (shared UI kit) is index-based — it's built for tabs that switch which
 * component is rendered in place, not tabs that are each their own route. My Account's five
 * sections ARE each their own route (so profile/addresses/wallet are all directly linkable and
 * survive a refresh), so this is a small bespoke nav instead, styled to match the same visual
 * language (yellow underline on the active tab) rather than reusing TpTabsComponent's
 * component-scoped styles, which wouldn't apply outside that component anyway.
 */
@Component({
  selector: 'tp-account-nav',
  standalone: true,
  imports: [RouterLink, RouterLinkActive],
  template: `
    <nav class="tp-account-nav" aria-label="My Account">
      <a routerLink="/my-bookings" routerLinkActive="tp-account-nav__link--active" [routerLinkActiveOptions]="{ exact: true }">
        Bookings
      </a>
      <a routerLink="/my-bookings/profile" routerLinkActive="tp-account-nav__link--active">Profile</a>
      <a routerLink="/my-bookings/addresses" routerLinkActive="tp-account-nav__link--active">Addresses</a>
      <a routerLink="/my-bookings/wallet" routerLinkActive="tp-account-nav__link--active">Wallet</a>
      <a routerLink="/my-bookings/cancellations" routerLinkActive="tp-account-nav__link--active">Cancellations &amp; Refunds</a>
    </nav>
  `,
  styles: [
    `
      .tp-account-nav {
        display: flex;
        gap: var(--tp-space-2);
        border-bottom: 1px solid var(--tp-border);
        margin-bottom: var(--tp-space-5);
        overflow-x: auto;
      }

      .tp-account-nav a {
        white-space: nowrap;
        padding: var(--tp-space-3) var(--tp-space-2);
        font-weight: 600;
        font-size: 14px;
        color: var(--tp-text-muted);
        border-bottom: 2px solid transparent;
        margin-bottom: -1px;
        transition:
          color var(--tp-transition-fast),
          border-color var(--tp-transition-fast);
      }

      .tp-account-nav a:hover {
        color: var(--tp-text);
      }

      .tp-account-nav__link--active {
        color: var(--tp-text);
        border-bottom-color: var(--tp-yellow-dark);
      }
    `,
  ],
})
export class AccountNavComponent {}
