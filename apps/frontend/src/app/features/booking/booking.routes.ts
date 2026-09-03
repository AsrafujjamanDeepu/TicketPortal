import { Component } from '@angular/core';
import { Routes } from '@angular/router';
import { TpCardComponent, TpEmptyStateComponent } from '../../shared/ui';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';

/**
 * PIECE 3 STARTING POINT — Customer Portal: Checkout, Payment & My Account.
 *
 * Replace this placeholder with: passenger details form, payment
 * (POST 'payments/initiate' -> 'payments/{id}/confirm'), e-ticket view,
 * and the "My Account" dashboard (booking history via GET 'bookings',
 * profile, wallet, cancellation requests, reviews).
 */
@Component({
  selector: 'tp-booking-placeholder',
  standalone: true,
  imports: [TpCardComponent, TpEmptyStateComponent],
  template: `
    <div class="tp-page">
      <tp-card>
        <tp-empty-state
          title="Checkout & My Account — Piece 3"
          message="Passenger details, payment, e-ticket, and booking history/profile/wallet go here."
        />
      </tp-card>
    </div>
  `,
})
export class BookingPlaceholderComponent {}

export const BOOKING_ROUTES: Routes = [
  {
    path: '',
    component: BookingPlaceholderComponent,
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Customer'] },
    title: 'My Bookings — TicketPortal',
  },
];
