import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { MatTabsModule } from '@angular/material/tabs';

/**
 * My Account's five sections are each their own route (so profile/addresses/
 * wallet are all directly linkable and survive a refresh), so this uses
 * Angular Material's router-aware `mat-tab-nav-bar` / `mat-tab-link` rather
 * than TpTabsComponent (which is index-based, for tabs that swap which
 * component renders in place rather than tabs that navigate).
 *
 * This replaces a previous hand-rolled `<nav>` that set `overflow-x: auto`
 * without an explicit `overflow-y`. Per the CSS overflow spec, when only one
 * axis is set to something other than `visible`, browsers force the other
 * axis to `auto` too — so that nav could silently pick up its own tiny
 * internal vertical scroll region. `mat-tab-nav-bar` handles genuine overflow
 * itself, with proper pagination arrows instead of a stray native scrollbar,
 * so this whole class of bug goes away.
 */
@Component({
  selector: 'tp-account-nav',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, MatTabsModule],
  template: `
    <nav mat-tab-nav-bar class="tp-account-nav" aria-label="My Account" [disableRipple]="false">
      <a
        mat-tab-link
        routerLink="/my-bookings"
        routerLinkActive
        #bookingsLink="routerLinkActive"
        [routerLinkActiveOptions]="{ exact: true }"
        [active]="bookingsLink.isActive"
      >
        Bookings
      </a>
      <a mat-tab-link routerLink="/my-bookings/profile" routerLinkActive #profileLink="routerLinkActive" [active]="profileLink.isActive">
        Profile
      </a>
      <a
        mat-tab-link
        routerLink="/my-bookings/addresses"
        routerLinkActive
        #addressesLink="routerLinkActive"
        [active]="addressesLink.isActive"
      >
        Addresses
      </a>
      <a mat-tab-link routerLink="/my-bookings/wallet" routerLinkActive #walletLink="routerLinkActive" [active]="walletLink.isActive">
        Wallet
      </a>
      <a
        mat-tab-link
        routerLink="/my-bookings/cancellations"
        routerLinkActive
        #cancellationsLink="routerLinkActive"
        [active]="cancellationsLink.isActive"
      >
        Cancellations &amp; Refunds
      </a>
    </nav>
  `,
  styles: [
    `
      .tp-account-nav {
        margin-bottom: var(--tp-space-5);
        border-bottom: 1px solid var(--tp-border);
        --mat-tab-header-label-text-weight: 600;
        --mat-tab-header-label-text-size: 14px;
        --mat-tab-header-label-text-tracking: 0.01em;
      }
    `,
  ],
})
export class AccountNavComponent {}
