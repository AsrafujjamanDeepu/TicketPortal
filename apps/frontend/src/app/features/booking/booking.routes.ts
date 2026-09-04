import { Routes } from '@angular/router';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';
import { checkoutBookingGuard, checkoutHoldGuard } from './guards/checkout.guard';

/**
 * Piece 3 — Customer Portal: Checkout, Payment & My Account.
 *
 * A single canActivate/data pair on the wrapping '' route (rather than repeating it on every
 * child) gates the whole feature to a logged-in Customer — matches the pattern already used
 * for Piece 4-6's OPERATOR_ROUTES/COUNTER_ROUTES/FINANCE_ROUTES in app.routes.ts, just applied
 * one level down since this feature has its own internal structure to keep organized.
 *
 * Static paths are listed before the ':id' catch-all so 'profile'/'addresses'/etc. don't get
 * swallowed by it.
 */
export const BOOKING_ROUTES: Routes = [
  {
    path: '',
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Customer'] },
    children: [
      {
        path: '',
        loadComponent: () => import('./my-account/booking-history/booking-history.component').then((m) => m.BookingHistoryComponent),
        title: 'My Bookings — TicketPortal',
      },
      { path: 'checkout', redirectTo: 'checkout/start', pathMatch: 'full' },
      {
        path: 'checkout/start',
        loadComponent: () => import('./checkout/checkout-start/checkout-start.component').then((m) => m.CheckoutStartComponent),
        title: 'Checkout — TicketPortal',
      },
      {
        path: 'checkout/passengers',
        loadComponent: () =>
          import('./checkout/passenger-details/passenger-details.component').then((m) => m.PassengerDetailsComponent),
        canActivate: [checkoutHoldGuard],
        title: 'Passenger Details — TicketPortal',
      },
      {
        path: 'checkout/payment',
        loadComponent: () => import('./checkout/payment/payment.component').then((m) => m.PaymentComponent),
        canActivate: [checkoutBookingGuard],
        title: 'Payment — TicketPortal',
      },
      {
        path: 'checkout/confirmation/:bookingId',
        loadComponent: () => import('./checkout/confirmation/confirmation.component').then((m) => m.ConfirmationComponent),
        title: 'Booking Confirmed — TicketPortal',
      },
      {
        path: 'profile',
        loadComponent: () => import('./my-account/profile/profile.component').then((m) => m.ProfileComponent),
        title: 'Profile — TicketPortal',
      },
      {
        path: 'addresses',
        loadComponent: () => import('./my-account/addresses/addresses.component').then((m) => m.AddressesComponent),
        title: 'Saved Addresses — TicketPortal',
      },
      {
        path: 'wallet',
        loadComponent: () => import('./my-account/wallet/wallet.component').then((m) => m.WalletComponent),
        title: 'Wallet — TicketPortal',
      },
      {
        path: 'cancellations',
        loadComponent: () => import('./my-account/cancellations/cancellations.component').then((m) => m.CancellationsComponent),
        title: 'Cancellations & Refunds — TicketPortal',
      },
      {
        // Catch-all MUST stay last — a booking id would otherwise shadow every static path above.
        path: ':id',
        loadComponent: () => import('./my-account/booking-detail/booking-detail.component').then((m) => m.BookingDetailComponent),
        title: 'Booking Details — TicketPortal',
      },
    ],
  },
];
