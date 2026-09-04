import { Routes } from '@angular/router';
import { authGuard } from '../../core/guards/auth.guard';
import { roleGuard } from '../../core/guards/role.guard';
import { CounterShellComponent } from './counter-shell.component';

/**
 * Piece 5 — Counter & Agent Operations Panel. One guarded shell route
 * (Staff/Operator/Admin — 'Staff' per role.guard.ts's note that Counter
 * doesn't get a role of its own) with six child screens underneath it:
 *
 *  - walk-in   : search -> seat map -> passenger details -> one-click cash
 *                confirm via POST 'payments/counter-sale/confirm' (NOT the
 *                online initiate/confirm pair)
 *  - setup     : sales counter CRUD (SalesCountersController)
 *  - agents    : agent roster CRUD (AgentsController) — see
 *                agent-bookings.component.ts for the booking-attribution
 *                gap this screen flags rather than papers over
 *  - cancellations : cancellations & refunds desk (CancellationRequestsController
 *                    + RefundsController)
 *  - staff     : HR mini-module — profiles/attendance/salary
 *  - complaints: complaints intake/status board
 */
export const COUNTER_ROUTES: Routes = [
  {
    path: '',
    component: CounterShellComponent,
    canActivate: [authGuard, roleGuard],
    data: { roles: ['Staff'] },
    title: 'Counter Desk — TicketPortal',
    children: [
      { path: '', redirectTo: 'walk-in', pathMatch: 'full' },
      {
        path: 'walk-in',
        loadComponent: () => import('./walk-in-booking/walk-in-booking.component').then((m) => m.WalkInBookingComponent),
        title: 'Walk-in Booking — Counter Desk',
      },
      {
        path: 'setup',
        loadComponent: () => import('./counter-setup/counter-setup.component').then((m) => m.CounterSetupComponent),
        title: 'Counter Setup — Counter Desk',
      },
      {
        path: 'agents',
        loadComponent: () => import('./agent-bookings/agent-bookings.component').then((m) => m.AgentBookingsComponent),
        title: 'Agents — Counter Desk',
      },
      {
        path: 'cancellations',
        loadComponent: () =>
          import('./cancellations-refunds/cancellations-refunds.component').then((m) => m.CancellationsRefundsComponent),
        title: 'Cancellations & Refunds — Counter Desk',
      },
      {
        path: 'staff',
        loadComponent: () => import('./staff-hr/staff-hr.component').then((m) => m.StaffHrComponent),
        title: 'Staff HR — Counter Desk',
      },
      {
        path: 'complaints',
        loadComponent: () => import('./complaints/complaints.component').then((m) => m.ComplaintsComponent),
        title: 'Complaints — Counter Desk',
      },
    ],
  },
];
