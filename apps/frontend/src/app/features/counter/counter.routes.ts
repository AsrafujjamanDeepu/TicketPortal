import { Component } from '@angular/core';
import { Routes } from '@angular/router';
import { TpCardComponent, TpEmptyStateComponent } from '../../shared/ui';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * PIECE 5 STARTING POINT — Counter & Agent Operations Panel.
 *
 * Replace this placeholder with: counter setup, walk-in booking flow
 * (search -> seat map -> passenger details -> POST 'payments/counter-sale/confirm'
 * — NOT the same endpoint as the online payment flow), agent bookings,
 * cancellations/refunds desk, staff HR, complaints intake.
 */
@Component({
  selector: 'tp-counter-placeholder',
  standalone: true,
  imports: [TpCardComponent, TpEmptyStateComponent],
  template: `
    <div class="tp-page">
      <tp-card>
        <tp-empty-state
          title="Counter & Agent Operations — Piece 5"
          message="Walk-in cash-counter booking flow, agent bookings, and the cancellations/refunds desk go here."
        />
      </tp-card>
    </div>
  `,
})
export class CounterPlaceholderComponent {}

export const COUNTER_ROUTES: Routes = [
  {
    path: '',
    component: CounterPlaceholderComponent,
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Staff'] },
    title: 'Counter Desk — TicketPortal',
  },
];
