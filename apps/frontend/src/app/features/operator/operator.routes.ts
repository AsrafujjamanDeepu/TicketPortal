import { Component } from '@angular/core';
import { Routes } from '@angular/router';
import { TpCardComponent, TpEmptyStateComponent } from '../../shared/ui';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * PIECE 4 STARTING POINT — Operator & Fleet Management Panel.
 *
 * Replace this placeholder with: operator profile/branches, fleet (buses,
 * categories, amenities, images, maintenance), network setup (terminals,
 * routes, route stops), trips & scheduling, crew assignment, fare/
 * cancellation-policy config.
 *
 * Watch BusOperator.inventoryMode (core/models/bus-operator.model.ts) —
 * disable write actions for ExternalApiManaged operators.
 */
@Component({
  selector: 'tp-operator-placeholder',
  standalone: true,
  imports: [TpCardComponent, TpEmptyStateComponent],
  template: `
    <div class="tp-page">
      <tp-card>
        <tp-empty-state
          title="Operator & Fleet Management — Piece 4"
          message="Fleet, routes/terminals, trips & scheduling, and crew assignment go here."
        />
      </tp-card>
    </div>
  `,
})
export class OperatorPlaceholderComponent {}

export const OPERATOR_ROUTES: Routes = [
  {
    path: '',
    component: OperatorPlaceholderComponent,
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Operator'] },
    title: 'Operator Panel — TicketPortal',
  },
];
