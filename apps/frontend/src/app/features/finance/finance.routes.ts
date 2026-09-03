import { Component } from '@angular/core';
import { Routes } from '@angular/router';
import { TpCardComponent, TpEmptyStateComponent } from '../../shared/ui';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * PIECE 6 STARTING POINT — Finance & Settlement Panel.
 *
 * Replace this placeholder with: commission rules, operator wallets/ledger,
 * the settlement engine (generate/approve), invoices & payouts, and
 * system finance config (tax rules, currencies, payment providers).
 *
 * This panel is dense with numbers — lean on <tp-table> with right-aligned
 * currency columns and <tp-status-pill> for lifecycle states.
 */
@Component({
  selector: 'tp-finance-placeholder',
  standalone: true,
  imports: [TpCardComponent, TpEmptyStateComponent],
  template: `
    <div class="tp-page">
      <tp-card>
        <tp-empty-state
          title="Finance & Settlement — Piece 6"
          message="Commission rules, wallets/ledger, settlements, invoices & payouts go here."
        />
      </tp-card>
    </div>
  `,
})
export class FinancePlaceholderComponent {}

export const FINANCE_ROUTES: Routes = [
  {
    path: '',
    component: FinancePlaceholderComponent,
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Staff'] },
    title: 'Finance — TicketPortal',
  },
];
