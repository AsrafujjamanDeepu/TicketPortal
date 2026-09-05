import { Routes } from '@angular/router';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * Piece 6 — Finance & Settlement Panel. Guard matches Counter's (Piece 5):
 * only 'Staff' is checked here, same convention noted in role.guard.ts —
 * Admin uses the separate React admin app and Operator has its own Angular
 * panel (Piece 4), so in practice only Staff reaches this module.
 */
export const FINANCE_ROUTES: Routes = [
  {
    path: '',
    loadComponent: () => import('./finance-shell/finance-shell.component').then((m) => m.FinanceShellComponent),
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Staff'] },
    children: [
      { path: '', redirectTo: 'commissions', pathMatch: 'full' },
      {
        path: 'commissions',
        loadComponent: () => import('./commission-rules/commission-rules.component').then((m) => m.CommissionRulesComponent),
        title: 'Commission Rules — Finance',
      },
      {
        path: 'wallets',
        loadComponent: () => import('./wallets-ledger/wallets-ledger.component').then((m) => m.WalletsLedgerComponent),
        title: 'Wallets & Ledger — Finance',
      },
      {
        path: 'settlements',
        loadComponent: () => import('./settlements/settlements.component').then((m) => m.SettlementsComponent),
        title: 'Settlements — Finance',
      },
      {
        path: 'settlements/:id',
        loadComponent: () =>
          import('./settlements/settlement-detail.component').then((m) => m.SettlementDetailComponent),
        title: 'Settlement — Finance',
      },
      {
        path: 'invoices',
        loadComponent: () => import('./invoices-payouts/invoices.component').then((m) => m.InvoicesComponent),
        title: 'Invoices — Finance',
      },
      {
        path: 'payouts',
        loadComponent: () => import('./invoices-payouts/payouts.component').then((m) => m.PayoutsComponent),
        title: 'Payouts — Finance',
      },
      {
        path: 'config',
        loadComponent: () => import('./system-config/system-config.component').then((m) => m.SystemConfigComponent),
        title: 'System Config — Finance',
      },
    ],
  },
];
