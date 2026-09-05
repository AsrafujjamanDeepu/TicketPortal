import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

/**
 * Piece 6 — Finance & Settlement Panel shell. Mounted once at 'finance/**',
 * renders the sub-nav across the six screens and hosts <router-outlet> for
 * whichever one is active — same shape as the top-level ShellComponent, one
 * level down.
 *
 * Per the Frontend Guideline's design note for this piece: dense with
 * numbers, so this stays deliberately neutral (white/border, no yellow fill)
 * — individual screens reserve yellow for primary actions and key totals.
 */
@Component({
  selector: 'tp-finance-shell',
  standalone: true,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="tp-page">
      <div class="tp-finance__header">
        <h2>Finance &amp; Settlement</h2>
        <p class="tp-muted">Commission, invoices, payouts, settlements, ledgers, wallets &amp; currencies.</p>
      </div>

      <nav class="tp-finance__nav">
        <a routerLink="commissions" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          Commission Rules
        </a>
        <a routerLink="wallets" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          Wallets &amp; Ledger
        </a>
        <a routerLink="settlements" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          Settlements
        </a>
        <a routerLink="invoices" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          Invoices
        </a>
        <a routerLink="payouts" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          Payouts
        </a>
        <a routerLink="config" routerLinkActive="tp-finance__nav-link--active" class="tp-finance__nav-link">
          System Config
        </a>
      </nav>

      <div class="tp-finance__content">
        <router-outlet />
      </div>
    </div>
  `,
  styles: [
    `
      .tp-finance__header {
        margin-bottom: var(--tp-space-5);
      }

      .tp-finance__header h2 {
        margin-bottom: var(--tp-space-1);
      }

      .tp-finance__nav {
        display: flex;
        flex-wrap: wrap;
        gap: var(--tp-space-2);
        border-bottom: 1px solid var(--tp-border);
        margin-bottom: var(--tp-space-5);
        padding-bottom: var(--tp-space-3);
      }

      .tp-finance__nav-link {
        font-size: 14px;
        font-weight: 600;
        color: var(--tp-text-muted);
        padding: var(--tp-space-2) var(--tp-space-3);
        border-radius: var(--tp-radius-sm);
        border: 1px solid transparent;
        transition: color var(--tp-transition-fast), border-color var(--tp-transition-fast), background var(--tp-transition-fast);
      }

      .tp-finance__nav-link:hover {
        color: var(--tp-text);
        background: var(--tp-bg-soft);
      }

      .tp-finance__nav-link--active {
        color: var(--tp-text);
        border-color: var(--tp-yellow-dark);
        background: var(--tp-yellow-tint);
      }

      .tp-finance__content {
        display: block;
      }
    `,
  ],
})
export class FinanceShellComponent {}
